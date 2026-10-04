using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ScheduleWidget
{
    /// <summary>latest.json as tools\Publish-Update.ps1 writes it (see the update spec).</summary>
    public sealed class UpdateManifest
    {
        [JsonProperty("app")] public string App { get; set; }
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("published")] public string Published { get; set; }
        [JsonProperty("file")] public string File { get; set; }
        [JsonProperty("size")] public long Size { get; set; }
        [JsonProperty("sha256")] public string Sha256 { get; set; }
        [JsonProperty("notes")] public string Notes { get; set; }
        [JsonProperty("signature")] public string Signature { get; set; }
    }

    /// <summary>The running app's version: AssemblyInformationalVersion (YYYY.MMDD.HHmm), compared as System.Version.</summary>
    public static class UpdateInfo
    {
        // Checks set it (by reflection) to test "older / same version" without rebuilding the app.
        internal static Version CurrentVersionOverride { get; set; }

        public static Version CurrentVersion => CurrentVersionOverride ?? ReadVersion(typeof(UpdateInfo).Assembly);

        // Shown as YYYY.MMDD.HHmm (Version drops the leading zeros: 2026.1001.0001 would read 2026.1001.1).
        public static string CurrentVersionText => Format(CurrentVersion);

        public static string Format(Version version) =>
            version == null ? "" : version.Build >= 0 && version.Major >= 2000
                ? version.Major + "." + version.Minor.ToString("0000") + "." + version.Build.ToString("0000")
                : version.ToString();

        private static Version ReadVersion(Assembly assembly)
        {
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (TryParseVersion(informational, out Version version)) return version;
            return assembly.GetName().Version ?? new Version(0, 0);
        }

        public static bool TryParseVersion(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string trimmed = text.Trim();
            if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed.Substring(1);
            int meta = trimmed.IndexOfAny(new[] { '+', '-', ' ' });
            if (meta >= 0) trimmed = trimmed.Substring(0, meta);
            return System.Version.TryParse(trimmed, out version);
        }
    }

    /// <summary>What the update check found: a manifest whose signature was verified, and whether it is newer than this app.</summary>
    public sealed class UpdateCheckResult
    {
        public UpdateManifest Manifest { get; internal set; }
        public Version Version { get; internal set; }
        public Version Current { get; internal set; }
        public bool IsNewer { get; internal set; }
        public bool SignatureVerified { get; internal set; }
        // Where the zip comes from (a download address of the release).
        public string PackageUrl { get; internal set; }
    }

    /// <summary>
    /// The in-app half of the network update: reads latest.json from the latest release of the public GitHub repository
    /// GitHubRepository (tools\Publish-Update.ps1 publishes it), checks its RSA signature with the public key built into the
    /// app FIRST, then compares the version, downloads the zip (size and SHA-256 checked), unpacks it into a staging folder
    /// and hands over to a copy of ScheduleWidget.Updater.exe, which replaces the app files once this app has exited.
    /// Nothing installs without the user confirming it. Errors are InvalidOperationException with a Korean message (shown
    /// as the status line).
    /// </summary>
    public sealed class UpdateClient
    {
        public const string AppName = "ScheduleWidget";
        public const string ExeName = "ScheduleWidget.exe";
        public const string UpdaterExeName = "ScheduleWidget.Updater.exe";

        /// <summary>
        /// The public GitHub repository (owner/repo) updates come from — the one place to set it; users never enter it.
        /// Blank: no repository yet, so the 업데이트 button in 설정 is disabled and nothing is checked.
        /// </summary>
        public static readonly string GitHubRepository = "rnrdus5642/ScheduleWidget";

        private const long MaxManifestBytes = 256 * 1024;
        private const long MaxPackageBytes = 1024L * 1024 * 1024;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(60);

        // Test hooks (set by reflection in the checks): a test key pair's public key, and a folder of the run instead of
        // the data root's updates folder.
        internal static string PublicKeyXmlOverride { get; set; }
        internal static string UpdatesRootOverride { get; set; }
        // More test hooks: a repository in place of GitHubRepository, and a local HTTP base in place of https://github.com/.
        internal static string GitHubRepositoryOverride { get; set; }
        internal static string GitHubBaseUrlOverride { get; set; }

        internal static string PublicKeyXml => PublicKeyXmlOverride ?? UpdatePublicKey.Xml;

        private static string GitHubWebBase => GitHubBaseUrlOverride != null ? GitHubBaseUrlOverride.TrimEnd('/') + "/" : "https://github.com/";

        /// <summary>%LocalAppData%\ScheduleWidget\updates — under the same data root the app (and the checks' redirection) use.</summary>
        public static string UpdatesDirectory => UpdatesRootOverride ?? Path.Combine(EmbeddedBrowser.UserDataRoot, "updates");

        // Redirects are followed by hand (GitHub sends release downloads on to its storage host; only https is followed).
        private static readonly HttpClient SharedClient =
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        private readonly HttpClient client;

        public UpdateClient(HttpClient client = null) { this.client = client ?? SharedClient; }

        // ---------------------------------------------------------------- repository

        /// <summary>The repository updates come from (GitHubRepository). "" = none yet: updates are off.</summary>
        public static string Repository => (GitHubRepositoryOverride ?? GitHubRepository ?? "").Trim();

        public static bool IsConfigured => Repository.Length > 0;

        public static bool TryParseRepository(string text, out string owner, out string repository)
        {
            owner = repository = null;
            string value = (text ?? "").Trim();
            var match = Regex.Match(value, @"\A(?:https?://(?:www\.)?github\.com/)?([A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/([A-Za-z0-9._-]{1,100}?)(?:\.git)?/?\z");
            if (!match.Success) return false;
            string repo = match.Groups[2].Value;
            if (repo == "." || repo == "..") return false;
            owner = match.Groups[1].Value;
            repository = repo;
            return true;
        }

        // ---------------------------------------------------------------- manifest

        public static UpdateManifest ParseManifest(string json)
        {
            UpdateManifest manifest;
            try { manifest = JsonConvert.DeserializeObject<UpdateManifest>(json ?? ""); }
            catch (JsonException) { throw new InvalidOperationException("업데이트 정보(latest.json)를 읽을 수 없습니다."); }
            if (manifest == null) throw new InvalidOperationException("업데이트 정보(latest.json)가 비어 있습니다.");
            if (!string.Equals(manifest.App, AppName, StringComparison.Ordinal))
                throw new InvalidOperationException("다른 앱의 업데이트 정보입니다.");
            if (!UpdateInfo.TryParseVersion(manifest.Version, out _))
                throw new InvalidOperationException("업데이트 정보의 버전을 읽을 수 없습니다.");
            if (string.IsNullOrEmpty(manifest.File) || !Regex.IsMatch(manifest.File, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,199}\.zip\z") ||
                manifest.File.Contains(".."))
                throw new InvalidOperationException("업데이트 정보의 파일 이름이 올바르지 않습니다.");
            if (manifest.Size <= 0 || manifest.Size > MaxPackageBytes)
                throw new InvalidOperationException("업데이트 정보의 파일 크기가 올바르지 않습니다.");
            if (string.IsNullOrEmpty(manifest.Sha256) || !Regex.IsMatch(manifest.Sha256, @"\A[0-9a-fA-F]{64}\z"))
                throw new InvalidOperationException("업데이트 정보의 SHA-256 값이 올바르지 않습니다.");
            return manifest;
        }

        /// <summary>The signed text: ScheduleWidget|&lt;version&gt;|&lt;file&gt;|&lt;size&gt;|&lt;sha256&gt; (UTF-8).</summary>
        public static string CanonicalString(UpdateManifest manifest) =>
            AppName + "|" + manifest.Version + "|" + manifest.File + "|" + manifest.Size.ToString(CultureInfo.InvariantCulture) + "|" + manifest.Sha256;

        /// <summary>RSA, SHA-256, PKCS#1 v1.5 over CanonicalString. False for a missing key or signature, or any bad input.</summary>
        public static bool VerifySignature(UpdateManifest manifest, string publicKeyXml)
        {
            if (manifest == null || string.IsNullOrWhiteSpace(publicKeyXml) || string.IsNullOrWhiteSpace(manifest.Signature)) return false;
            try
            {
                byte[] signature = Convert.FromBase64String(manifest.Signature.Trim());
                using (var rsa = new RSACng())
                {
                    rsa.FromXmlString(publicKeyXml);
                    if (rsa.KeySize < 2048) return false;
                    return rsa.VerifyData(Encoding.UTF8.GetBytes(CanonicalString(manifest)), signature,
                        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch (Exception ex) when (ex is FormatException || ex is CryptographicException || ex is ArgumentException ||
                                       ex is System.Xml.XmlException || ex is PlatformNotSupportedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Signature first (an unsigned or tampered manifest is rejected whatever its version), then the version.
        /// </summary>
        public static UpdateCheckResult Evaluate(UpdateManifest manifest, string publicKeyXml, Version current)
        {
            if (string.IsNullOrWhiteSpace(publicKeyXml))
                throw new InvalidOperationException("이 앱에 업데이트 서명 확인 키가 없어 업데이트를 받을 수 없습니다.");
            if (!VerifySignature(manifest, publicKeyXml))
                throw new InvalidOperationException("업데이트 서명이 올바르지 않아 거부했습니다. 업데이트 파일이 바뀌었을 수 있습니다.");
            UpdateInfo.TryParseVersion(manifest.Version, out Version version);
            return new UpdateCheckResult
            {
                Manifest = manifest,
                Version = version,
                Current = current,
                SignatureVerified = true,
                IsNewer = version > current
            };
        }

        // ---------------------------------------------------------------- check

        public const string NoRepositoryMessage = "업데이트 저장소가 아직 정해지지 않았습니다.";
        public const string NetworkErrorMessage = "GitHub에 연결할 수 없습니다. 네트워크 연결을 확인해 주세요.";

        public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellation = default(CancellationToken))
        {
            try
            {
                string repository = Repository;
                if (repository.Length == 0) throw new InvalidOperationException(NoRepositoryMessage);
                if (!TryParseRepository(repository, out string owner, out string repo))
                    throw new InvalidOperationException("업데이트 저장소(" + repository + ")가 owner/repo 형식이 아닙니다.");
                string baseUrl = GitHubWebBase + owner + "/" + repo + "/releases/latest/download/";
                var manifest = ParseManifest(await GetTextAsync(baseUrl + "latest.json", cancellation).ConfigureAwait(false));
                var result = Evaluate(manifest, PublicKeyXml, UpdateInfo.CurrentVersion);
                result.PackageUrl = baseUrl + Uri.EscapeDataString(manifest.File);
                return result;
            }
            catch (Exception ex) when (IsNetworkError(ex) && !cancellation.IsCancellationRequested)
            {
                throw new InvalidOperationException(NetworkErrorMessage);
            }
        }

        // ---------------------------------------------------------------- download

        /// <summary>
        /// Downloads the zip of a verified, newer update into the updates folder (size and SHA-256 must match the signed
        /// manifest), unpacks it into staging-&lt;version&gt; and returns the folder holding ScheduleWidget.exe.
        /// </summary>
        public async Task<string> DownloadAsync(UpdateCheckResult check, IProgress<double> progress = null,
            CancellationToken cancellation = default(CancellationToken))
        {
            if (check == null || check.Manifest == null || !check.SignatureVerified)
                throw new InvalidOperationException("먼저 업데이트 확인을 해 주세요.");
            if (!check.IsNewer) throw new InvalidOperationException("이미 최신 버전입니다.");
            var manifest = check.Manifest;
            string folder = UpdatesDirectory;
            Directory.CreateDirectory(folder);
            CleanUpOld(folder, manifest);
            string zipPath = Path.Combine(folder, manifest.File);
            string partPath = zipPath + ".part";

            if (!(System.IO.File.Exists(zipPath) && MatchesManifest(zipPath, manifest)))
            {
                TryDeleteFile(partPath);
                try
                {
                    HttpResponseMessage downloadResponse;
                    using (var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                    {
                        // Bound the complete redirect/header phase. The token is no longer used once headers arrive, so
                        // the independent 60-second body-stall limit below remains in force.
                        headerTimeout.CancelAfter(RequestTimeout);
                        downloadResponse = await SendAsync(check.PackageUrl, headerTimeout.Token).ConfigureAwait(false);
                    }
                    using (var response = downloadResponse)
                    {
                        long? announced = response.Content.Headers.ContentLength;
                        if (announced.HasValue && announced.Value != manifest.Size)
                            throw new InvalidOperationException("내려받을 파일 크기가 업데이트 정보와 다릅니다.");
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                        {
                            var buffer = new byte[81920];
                            long total = 0;
                            while (true)
                            {
                                int read;
                                using (var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                                {
                                    stall.CancelAfter(ReadStallTimeout);
                                    read = await input.ReadAsync(buffer, 0, buffer.Length, stall.Token).ConfigureAwait(false);
                                }
                                if (read <= 0) break;
                                total += read;
                                if (total > manifest.Size) throw new InvalidOperationException("내려받은 파일이 업데이트 정보보다 큽니다.");
                                await output.WriteAsync(buffer, 0, read, cancellation).ConfigureAwait(false);
                                progress?.Report((double)total / manifest.Size);
                            }
                        }
                    }
                    if (!MatchesManifest(partPath, manifest, out string why)) throw new InvalidOperationException(why);
                    TryDeleteFile(zipPath);
                    System.IO.File.Move(partPath, zipPath);
                }
                catch (Exception ex) when (IsNetworkError(ex) && !cancellation.IsCancellationRequested)
                {
                    TryDeleteFile(partPath);
                    throw new InvalidOperationException("업데이트 파일을 내려받지 못했습니다. 네트워크를 확인해 주세요.");
                }
                catch
                {
                    TryDeleteFile(partPath);
                    throw;
                }
            }
            progress?.Report(1);
            // Hash checked again on the very handle the zip is unpacked from (writes refused meanwhile): what is unpacked is
            // exactly the signed package, even if the file in the updates folder were swapped after the download.
            return ExtractVerified(zipPath, manifest, Path.Combine(folder, "staging-" + manifest.Version));
        }

        /// <summary>Opens the zip once (no writers allowed), checks size + SHA-256 against the signed manifest, then unpacks that same stream.</summary>
        public static string ExtractVerified(string zipPath, UpdateManifest manifest, string staging)
        {
            using (var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
            {
                if (stream.Length != manifest.Size) throw new InvalidOperationException("내려받은 파일 크기가 업데이트 정보와 다릅니다.");
                string hash;
                using (var sha = SHA256.Create())
                    hash = string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
                if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("내려받은 파일이 손상되었거나 바뀌었습니다 (SHA-256 불일치). 설치하지 않았습니다.");
                stream.Position = 0;
                return ExtractCore(stream, staging);
            }
        }

        private static bool MatchesManifest(string path, UpdateManifest manifest) => MatchesManifest(path, manifest, out _);

        private static bool MatchesManifest(string path, UpdateManifest manifest, out string why)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != manifest.Size) { why = "내려받은 파일 크기가 업데이트 정보와 다릅니다."; return false; }
            string hash;
            using (var sha = SHA256.Create())
            using (var stream = System.IO.File.OpenRead(path))
                hash = string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                why = "내려받은 파일이 손상되었거나 바뀌었습니다 (SHA-256 불일치). 설치하지 않았습니다.";
                return false;
            }
            why = null;
            return true;
        }

        /// <summary>Unpacks the zip (no entry may leave the folder) and returns the folder that holds ScheduleWidget.exe.</summary>
        public static string ExtractToStaging(string zipPath, string staging)
        {
            using (var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
                return ExtractCore(stream, staging);
        }

        // Limits far above a real package (~13 MB, ~30 files) so a broken or hostile zip cannot fill the disk.
        private const int MaxZipEntries = 10000;
        private const long MaxUnpackedBytes = 2L * 1024 * 1024 * 1024;
        private static readonly Regex ReservedName = new Regex(@"\A(CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])(\..*)?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// A zip entry name is accepted only as plain relative segments: no drive, root, "." / "..", ':' (alternate data
        /// streams), wildcard or control characters, reserved device names (CON, NUL, COM1 …) or segments ending in '.' / ' '.
        /// </summary>
        internal static bool IsSafeEntryName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName) || fullName.Length > 400) return false;
            string name = fullName.Replace('\\', '/');
            if (name.StartsWith("/", StringComparison.Ordinal)) return false;
            string[] segments = name.TrimEnd('/').Split('/');
            foreach (string segment in segments)
            {
                if (segment.Length == 0 || segment == "." || segment == "..") return false;
                if (segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal)) return false;
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|' }) >= 0) return false;
                if (ReservedName.IsMatch(segment)) return false;
            }
            return true;
        }

        // A rejected or broken package leaves no half-unpacked staging folder behind.
        private static string ExtractCore(Stream zipStream, string staging)
        {
            try { return ExtractInto(zipStream, staging); }
            catch
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                throw;
            }
        }

        private static string ExtractInto(Stream zipStream, string staging)
        {
            const string badPath = "업데이트 파일에 잘못된 경로가 있어 설치하지 않았습니다.";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            string root = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            try
            {
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, true))
                {
                    var entries = archive.Entries;
                    if (entries.Count > MaxZipEntries) throw new InvalidOperationException("업데이트 파일에 항목이 너무 많아 설치하지 않았습니다.");
                    long declared = 0;
                    foreach (var entry in entries)
                    {
                        if (!IsSafeEntryName(entry.FullName)) throw new InvalidOperationException(badPath);
                        declared += Math.Max(0, entry.Length);
                    }
                    if (declared > MaxUnpackedBytes) throw new InvalidOperationException("업데이트 파일의 압축을 푼 크기가 너무 커서 설치하지 않았습니다.");

                    long written = 0;
                    var buffer = new byte[81920];
                    foreach (var entry in entries)
                    {
                        string target;
                        try { target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar))); }
                        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
                        { throw new InvalidOperationException(badPath); }
                        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) || target.Length == root.Length)
                            throw new InvalidOperationException(badPath);
                        if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        // Copied by hand so an entry can never produce more than its declared size (decompression bomb).
                        using (var input = entry.Open())
                        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                        {
                            long entryBytes = 0;
                            int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                entryBytes += read;
                                written += read;
                                if (entryBytes > entry.Length || written > MaxUnpackedBytes)
                                    throw new InvalidOperationException("업데이트 파일의 압축을 푼 크기가 너무 커서 설치하지 않았습니다.");
                                output.Write(buffer, 0, read);
                            }
                        }
                    }
                }
            }
            catch (InvalidDataException) { throw new InvalidOperationException("업데이트 파일(zip)을 열 수 없습니다."); }

            if (System.IO.File.Exists(Path.Combine(root, ExeName))) return root.TrimEnd(Path.DirectorySeparatorChar);
            // A zip made of the folder itself (one top folder): its content is the app.
            var folders = Directory.GetDirectories(root);
            if (folders.Length == 1 && Directory.GetFiles(root).Length == 0 && System.IO.File.Exists(Path.Combine(folders[0], ExeName)))
                return folders[0];
            throw new InvalidOperationException("업데이트 파일에 " + ExeName + "가 없어 설치하지 않았습니다.");
        }

        // Older zips, staging folders and updater copies go (best effort: one still in use stays until next time).
        private static void CleanUpOld(string folder, UpdateManifest keep)
        {
            try
            {
                foreach (string file in Directory.GetFiles(folder, "ScheduleWidget-*.zip*"))
                {
                    string name = Path.GetFileName(file);
                    if (!string.Equals(name, keep.File, StringComparison.OrdinalIgnoreCase)) TryDeleteFile(file);
                }
                foreach (string dir in Directory.GetDirectories(folder))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith("updater-", StringComparison.OrdinalIgnoreCase) ||
                        (name.StartsWith("staging-", StringComparison.OrdinalIgnoreCase) && name != "staging-" + keep.Version))
                        try { Directory.Delete(dir, true); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        // ---------------------------------------------------------------- apply

        public static string BuildUpdaterArguments(int processId, string appDirectory, string stagingDirectory) =>
            "--pid " + processId.ToString(CultureInfo.InvariantCulture) +
            " --app " + QuoteArgument(appDirectory) +
            " --staging " + QuoteArgument(stagingDirectory) +
            " --exe " + ExeName;

        // A quoted path must not end in a backslash (it would escape the closing quote).
        private static string QuoteArgument(string path)
        {
            string value = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (value.EndsWith(":", StringComparison.Ordinal)) value += "\\.";
            return "\"" + value + "\"";
        }

        /// <summary>
        /// Copies ScheduleWidget.Updater.exe (the new package's when it has one, else the one next to this app) into the
        /// updates folder and runs that copy, so the updater can replace its own file in the app folder. The caller then
        /// exits the app cleanly (ExitApplication): the updater waits for this process to end before touching any file.
        /// </summary>
        public static Process StartUpdater(string stagingRoot, string appDirectory = null, int? processId = null)
        {
            appDirectory = appDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
            string source = new[] { Path.Combine(stagingRoot, UpdaterExeName), Path.Combine(appDirectory, UpdaterExeName) }
                .FirstOrDefault(System.IO.File.Exists);
            if (source == null) throw new InvalidOperationException("업데이트 도우미(" + UpdaterExeName + ")를 찾을 수 없습니다.");
            string runFolder = Path.Combine(UpdatesDirectory, "updater-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(runFolder);
            string runExe = Path.Combine(runFolder, UpdaterExeName);
            System.IO.File.Copy(source, runExe, true);
            if (System.IO.File.Exists(source + ".config")) System.IO.File.Copy(source + ".config", runExe + ".config", true);
            int pid = processId ?? Process.GetCurrentProcess().Id;
            var start = new ProcessStartInfo(runExe, BuildUpdaterArguments(pid, appDirectory, stagingRoot))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = runFolder
            };
            return Process.Start(start);
        }

        // ---------------------------------------------------------------- HTTP

        private async Task<string> GetTextAsync(string url, CancellationToken cancellation)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(RequestTimeout);
                using (var response = await SendAsync(url, timeout.Token).ConfigureAwait(false))
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var memory = new MemoryStream())
                {
                    var buffer = new byte[16384];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        if (memory.Length + read > MaxManifestBytes) throw new InvalidOperationException("업데이트 정보가 너무 큽니다.");
                        memory.Write(buffer, 0, read);
                    }
                    return new UTF8Encoding(false).GetString(memory.ToArray()).TrimStart('﻿');
                }
            }
        }

        private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken cancellation)
        {
            Uri uri = new Uri(url);
            for (int hop = 0; hop < 6; hop++)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("ScheduleWidget-Updater/1.0");
                HttpResponseMessage response;
                using (request)
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                int code = (int)response.StatusCode;
                if (code >= 300 && code < 400 && response.Headers.Location != null)
                {
                    Uri next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                    response.Dispose();
                    if (next.Scheme != Uri.UriSchemeHttps && !(next.Scheme == Uri.UriSchemeHttp && uri.Scheme == Uri.UriSchemeHttp))
                        throw new InvalidOperationException("GitHub가 안전하지 않은 주소로 보냈습니다.");
                    uri = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    response.Dispose();
                    throw new InvalidOperationException(code == 404
                        ? "업데이트 정보를 찾을 수 없습니다 (404). 저장소와 릴리스 게시 여부를 확인해 주세요."
                        : code == 401 || code == 403
                            ? "업데이트를 받을 권한이 없습니다 (" + code + ")."
                            : "GitHub가 오류를 돌려주었습니다 (" + code + ").");
                }
                return response;
            }
            throw new InvalidOperationException("GitHub의 주소 이동이 너무 많습니다.");
        }

        private static bool IsNetworkError(Exception ex) =>
            ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException ||
            ex is System.Net.WebException || ex is IOException && !(ex is FileNotFoundException) ||
            ex is UriFormatException;

        private static void TryDeleteFile(string path)
        {
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }
    }
}
