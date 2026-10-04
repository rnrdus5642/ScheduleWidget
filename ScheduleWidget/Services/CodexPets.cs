using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ScheduleWidget
{
    /// <summary>
    /// The Codex characters (Codex, Dewey, …) are not shipped with the app: they belong to OpenAI Codex. When Codex is installed
    /// on this PC, its sheets are copied out of its own app.asar into the app data area (beside the Pet folder:
    /// CodexPets\builtin\&lt;id&gt;\pet.json + spritesheet) and listed in 캐릭터 선택 as the "Codex" group. They are copied again
    /// when Codex changes (another version), and deleted when Codex is gone — they never stay usable without Codex.
    /// The user's own Codex pets (%USERPROFILE%\.codex\pets\&lt;id&gt;) are listed straight from there (see UserPetsDirectory).
    /// <see cref="Refresh"/> runs off the UI thread (app start, 캐릭터 선택); <see cref="Changed"/> tells the pet windows.
    /// </summary>
    public static class CodexPets
    {
        /// <summary>Folder names of the old built-in characters → their names (selections saved as Characters/&lt;id&gt; keep them).</summary>
        public static readonly IReadOnlyDictionary<string, string> KnownNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bsod"] = "BSOD", ["codex"] = "Codex", ["dewey"] = "Dewey", ["fireball"] = "Fireball", ["hoots"] = "Hoots",
            ["null-signal"] = "Null Signal", ["rocky"] = "Rocky", ["seedy"] = "Seedy", ["stacky"] = "Stacky"
        };

        /// <summary>
        /// Tests and probes: finds Codex's install folder (package root) instead of this PC's real one; returns null for "not
        /// installed". Unset, the real Codex is looked for only while CharacterCatalog.PetRoot is not set (the app itself) —
        /// a test run never reads the real Codex unless it sets this to <see cref="FindInstalledRoot"/>.
        /// </summary>
        public static Func<string> InstallFinder { get; set; }

        /// <summary>Tests: where the user's own Codex pets are (null: %USERPROFILE%\.codex\pets for the app, none for tests).</summary>
        public static string UserPetsSource { get; set; }

        /// <summary>Tests: where the copies go (null: CodexPets beside the Pet folder in use).</summary>
        public static string RootOverride { get; set; }

        public static string Root => RootOverride ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(CharacterCatalog.PetDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), "CodexPets");

        /// <summary>The Codex characters copied from Codex (one folder per character, named like the old built-ins).</summary>
        public static string BuiltInDirectory => Path.Combine(Root, "builtin");

        /// <summary>The user's own Codex pets, or null when there is no such folder to read.</summary>
        public static string UserPetsDirectory
        {
            get
            {
                if (UserPetsSource != null) return UserPetsSource;
                if (CharacterCatalog.PetRoot != null) return null; // tests: never the real one
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, ".codex", "pets");
            }
        }

        /// <summary>Off: <see cref="RefreshInBackground"/> does nothing (tests call <see cref="Refresh"/> themselves).</summary>
        public static bool AutoRefresh { get; set; } = true;

        /// <summary>Raised (on a background thread) when the Codex characters were copied, replaced or removed.</summary>
        public static event Action Changed;

        /// <summary>Codex was found by the last <see cref="Refresh"/> (null: not looked yet).</summary>
        public static bool? LastFound { get; private set; }

        /// <summary>Changes whenever the copied characters do (the format of what is written here, too).</summary>
        private const int FormatVersion = 2;
        private const string SourceMarker = ".codex-source";
        private const long MaxSheetBytes = 25L * 1024 * 1024;
        private static readonly object Gate = new object();
        private static int refreshing;

        private static readonly Regex SheetName = new Regex(@"^(?<id>[a-z0-9]+(?:-[a-z0-9]+)*)-spritesheet-v(?<v>\d{1,4})-[0-9a-f]{6,64}\.(?<ext>webp|png)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Starts <see cref="Refresh"/> on a background thread (once at a time; asking while one runs does nothing).</summary>
        public static void RefreshInBackground()
        {
            if (!AutoRefresh) return;
            if (System.Threading.Interlocked.CompareExchange(ref refreshing, 1, 0) != 0) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { Refresh(); }
                catch (Exception ex) { PetLog.Write("codex-pets", ex.GetType().Name + ": " + ex.Message); }
                finally { System.Threading.Interlocked.Exchange(ref refreshing, 0); }
            });
        }

        /// <summary>
        /// Brings the Codex characters in step with Codex: copied when Codex is installed and changed since (or never copied),
        /// deleted when it is not installed. Returns true when anything changed (and raises <see cref="Changed"/>). Never
        /// throws for a missing, locked or unreadable Codex — that only means no Codex characters this time.
        /// </summary>
        public static bool Refresh()
        {
            bool changed;
            lock (Gate)
            {
                string asar = null;
                try { asar = FindAsar(); }
                catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { }
                LastFound = asar != null;
                try { changed = asar == null ? RemoveAll() : Extract(asar); }
                catch (Exception ex) when (CharacterCatalog.IsImportError(ex))
                {
                    PetLog.Write("codex-pets", ex.GetType().Name + ": " + ex.Message);
                    changed = true; // part of it may have changed: the windows read the list again
                }
            }
            if (changed) Changed?.Invoke();
            return changed;
        }

        // ---- Finding Codex ----

        /// <summary>The app.asar of the installed Codex, or null.</summary>
        public static string FindAsar()
        {
            string root;
            if (InstallFinder != null) root = InstallFinder();
            else if (CharacterCatalog.PetRoot != null) return null; // tests without a fake Codex: none
            else root = FindInstalledRoot();
            return AsarIn(root);
        }

        private static string AsarIn(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return null;
            foreach (string relative in new[] { @"app\resources\app.asar", @"resources\app.asar" })
            {
                try
                {
                    string path = Path.Combine(root, relative);
                    if (File.Exists(path)) return path;
                }
                catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { }
            }
            return null;
        }

        /// <summary>
        /// Codex's install folder on this PC, or null: the Store (MSIX) package registered for this user (the package
        /// repository in the registry, then the WindowsApps folder when it can be listed), else a classic install.
        /// </summary>
        public static string FindInstalledRoot()
        {
            var candidates = new List<string>();
            try
            {
                using (var packages = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
                {
                    if (packages != null)
                        foreach (string name in packages.GetSubKeyNames().Where(IsCodexPackage))
                            using (var key = packages.OpenSubKey(name))
                                if (key?.GetValue("PackageRootFolder") is string folder && folder.Length > 0) candidates.Add(folder);
                }
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { }
            try
            {
                string programFiles = Environment.GetEnvironmentVariable("ProgramW6432");
                if (string.IsNullOrEmpty(programFiles)) programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string apps = Path.Combine(programFiles, "WindowsApps");
                if (Directory.Exists(apps)) candidates.AddRange(Directory.GetDirectories(apps, "OpenAI.Codex_*").Where(d => IsCodexPackage(Path.GetFileName(d))));
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { } // listing WindowsApps is usually denied
            string found = candidates.Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(c => AsarIn(c) != null).OrderByDescending(c => PackageVersion(Path.GetFileName(c.TrimEnd('\\')))).FirstOrDefault();
            if (found != null) return found;
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (string classic in new[]
            {
                Path.Combine(local, "Programs", "OpenAI", "Codex"), Path.Combine(local, "Programs", "Codex"), Path.Combine(local, "Programs", "codex"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenAI", "Codex")
            })
                if (AsarIn(classic) != null) return classic;
            return null;
        }

        // "OpenAI.Codex_<version>_<arch>__<publisher>" (the desktop app; not other OpenAI packages).
        private static bool IsCodexPackage(string name) =>
            name != null && name.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase);

        private static Version PackageVersion(string name)
        {
            string[] parts = (name ?? "").Split('_');
            return parts.Length > 1 && Version.TryParse(parts[1], out Version v) ? v : new Version(0, 0);
        }

        // ---- Copying out of app.asar ----

        private sealed class AsarEntry
        {
            public string Path;
            public long Size, Offset;
            public bool Unpacked;
        }

        // What the copied characters were made from: the archive, its size and time (another Codex version changes them).
        private static string SourceStamp(string asar)
        {
            var file = new FileInfo(asar);
            return FormatVersion + "|" + file.FullName.ToLowerInvariant() + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
        }

        private static bool Extract(string asar)
        {
            string stamp;
            try { stamp = SourceStamp(asar); }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { return false; }
            string target = BuiltInDirectory;
            string marker = Path.Combine(target, SourceMarker);
            try
            {
                if (File.Exists(marker))
                {
                    string[] completed = File.ReadAllLines(marker);
                    if (completed.Length > 0 && completed[0] == stamp && completed.Skip(1)
                        .Where(CharacterCatalog.IsPetId)
                        .All(id => File.Exists(Path.Combine(target, id, "pet.json")))) return false; // as it was, including no supported sheets
                }
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { }

            List<AsarEntry> sheets;
            try { sheets = ReadIndex(asar, name => SheetName.IsMatch(name)); }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex) || ex is EndOfStreamException)
            {
                PetLog.Write("codex-pets", "cannot read " + asar + ": " + ex.Message);
                return false; // keep what was copied before; tried again next time
            }
            bool changed = false;
            bool allSettled = true;
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(target);
            // Per character the newest sheet version that is a proper sheet (1536 wide, 208 per row, 9/11/13 rows).
            foreach (var group in sheets.GroupBy(s => SheetName.Match(System.IO.Path.GetFileName(s.Path)).Groups["id"].Value.ToLowerInvariant()))
            {
                string id = group.Key;
                if (!CharacterCatalog.IsPetId(id)) continue;
                foreach (var entry in group.OrderByDescending(s => int.Parse(SheetName.Match(System.IO.Path.GetFileName(s.Path)).Groups["v"].Value)))
                {
                    try
                    {
                        if (WriteCharacter(asar, entry, target, id, out bool replaced)) { kept.Add(id); changed |= replaced; break; }
                    }
                    catch (Exception ex) when (CharacterCatalog.IsImportError(ex) || ex is EndOfStreamException)
                    {
                        // Only file access failures can clear on a later refresh. Invalid ASAR offsets, short/corrupt
                        // entries and unsupported paths are settled results; keep trying older sheet versions below.
                        if ((ex is IOException && !(ex is EndOfStreamException)) || ex is UnauthorizedAccessException)
                            allSettled = false;
                        PetLog.Write("codex-pets", id + ": " + ex.Message);
                        // A character copied before stays when it cannot be replaced just now (a sheet in use).
                        if (File.Exists(Path.Combine(target, id, "pet.json"))) { kept.Add(id); break; }
                    }
                }
            }
            foreach (string folder in Directory.GetDirectories(target))
            {
                string name = System.IO.Path.GetFileName(folder);
                if (kept.Contains(name)) continue;
                if (DeleteCharacterFolder(folder)) changed = true;
            }
            if (allSettled)
            {
                try { File.WriteAllText(marker, stamp + Environment.NewLine + string.Join(Environment.NewLine, kept.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))); }
                catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { }
            }
            if (changed) PetLog.Write("codex-pets", kept.Count + " Codex character(s) copied from " + asar);
            return changed;
        }

        // One character: its sheet copied out (only when it differs from the copy here) and checked, with a pet.json. False
        // when this sheet is no proper sheet (the next older version is tried).
        private static bool WriteCharacter(string asar, AsarEntry entry, string target, string id, out bool replaced)
        {
            replaced = false;
            if (entry.Size <= 0 || entry.Size > MaxSheetBytes) return false;
            string extension = System.IO.Path.GetExtension(entry.Path).ToLowerInvariant();
            string folder = Path.Combine(target, id);
            string building = folder + ".importing"; // never listed (CharacterCatalog.Load skips it)
            if (Directory.Exists(building)) Directory.Delete(building, true);
            Directory.CreateDirectory(building);
            try
            {
                string sheet = Path.Combine(building, "spritesheet" + extension);
                CopyEntry(asar, entry, sheet);
                if (!CharacterCatalog.TryImageSizeForImport(sheet, out int width, out int height) || width != 1536 ||
                    !(height == 208 * 9 || height == 208 * 11 || height == 208 * 13)) return false;
                int rows = height / 208;
                var manifest = new JObject
                {
                    ["id"] = id,
                    ["displayName"] = KnownNames.TryGetValue(id, out string known) ? known : TitleCase(id),
                    ["spritesheetPath"] = "spritesheet" + extension,
                    ["spriteVersionNumber"] = rows == 13 ? 3 : rows == 11 ? 2 : 1
                };
                string manifestText = manifest.ToString();
                File.WriteAllText(Path.Combine(building, "pet.json"), manifestText);
                string oldManifest = Path.Combine(folder, "pet.json");
                string oldSheet = Path.Combine(folder, "spritesheet" + extension);
                if (File.Exists(oldManifest) && File.ReadAllText(oldManifest) == manifestText && File.Exists(oldSheet) && SameBytes(oldSheet, sheet))
                    return true; // the same as before: left as it is (its sharper copy stays valid)
                if (Directory.Exists(folder))
                {
                    if (!DeleteCharacterFolder(folder)) throw new IOException("the old copy of " + id + " is in use");
                }
                Directory.Move(building, folder);
                replaced = true;
                return true;
            }
            finally
            {
                if (Directory.Exists(building)) try { Directory.Delete(building, true); } catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { }
            }
        }

        private static void CopyEntry(string asar, AsarEntry entry, string destination)
        {
            if (entry.Unpacked)
            {
                // Files kept outside the archive: app.asar.unpacked\<same path>, never outside that folder.
                string unpacked = asar + ".unpacked";
                string source = Path.GetFullPath(Path.Combine(unpacked, entry.Path.Replace('/', '\\')));
                if (!source.StartsWith(Path.GetFullPath(unpacked) + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("bad path in app.asar");
                if (new FileInfo(source).Length > MaxSheetBytes) throw new InvalidOperationException("sheet too large");
                File.Copy(source, destination, true);
                return;
            }
            using (var stream = new FileStream(asar, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess))
            {
                long dataStart = DataStart(stream);
                if (entry.Offset < 0 || dataStart + entry.Offset + entry.Size > stream.Length) throw new InvalidOperationException("app.asar entry out of range");
                stream.Position = dataStart + entry.Offset;
                using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write))
                {
                    var buffer = new byte[81920];
                    long left = entry.Size;
                    while (left > 0)
                    {
                        int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                        if (read <= 0) throw new EndOfStreamException("app.asar ended early");
                        output.Write(buffer, 0, read);
                        left -= read;
                    }
                }
            }
        }

        // Electron's asar: uint32 4, uint32 header size, then the header (uint32 payload size, uint32 JSON length, the JSON);
        // the files follow the header (offsets in the JSON count from there).
        private static long DataStart(Stream stream) => 8 + (long)ReadHead(stream).Item1;

        private static Tuple<uint, uint> ReadHead(Stream stream)
        {
            stream.Position = 0;
            var head = new byte[16];
            int total = 0, n;
            while (total < 16 && (n = stream.Read(head, total, 16 - total)) > 0) total += n;
            if (total < 16) throw new InvalidOperationException("not an asar archive");
            uint first = BitConverter.ToUInt32(head, 0), headerSize = BitConverter.ToUInt32(head, 4), jsonLength = BitConverter.ToUInt32(head, 12);
            if (first != 4 || headerSize < 8 || jsonLength == 0 || jsonLength > headerSize - 8 || headerSize > 256u * 1024 * 1024 || 8L + headerSize > stream.Length)
                throw new InvalidOperationException("not an asar archive");
            return Tuple.Create(headerSize, jsonLength);
        }

        // Only the header is read (a few MB of a 500 MB archive), streamed through the JSON reader: the files whose name
        // `wanted` accepts, with where they are.
        private static List<AsarEntry> ReadIndex(string asar, Func<string, bool> wanted)
        {
            var found = new List<AsarEntry>();
            using (var stream = new FileStream(asar, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan))
            {
                uint jsonLength = ReadHead(stream).Item2;
                stream.Position = 16;
                using (var text = new StreamReader(new BoundedStream(stream, jsonLength), Encoding.UTF8, false, 65536, leaveOpen: true))
                using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, MaxDepth = 256 })
                {
                    if (!reader.Read() || reader.TokenType != JsonToken.StartObject) throw new InvalidOperationException("bad asar header");
                    while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                    {
                        if ((string)reader.Value == "files") { reader.Read(); ReadFiles(reader, "", wanted, found); }
                        else { reader.Read(); reader.Skip(); }
                    }
                }
            }
            return found;
        }

        private static void ReadFiles(JsonTextReader reader, string prefix, Func<string, bool> wanted, List<AsarEntry> found)
        {
            if (reader.TokenType != JsonToken.StartObject) { reader.Skip(); return; }
            while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
            {
                string name = (string)reader.Value;
                reader.Read();
                if (reader.TokenType != JsonToken.StartObject) { reader.Skip(); continue; }
                string path = prefix + name;
                long size = -1, offset = -1;
                bool unpacked = false, folder = false, link = false;
                while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                {
                    string field = (string)reader.Value;
                    reader.Read();
                    switch (field)
                    {
                        case "files": folder = true; ReadFiles(reader, path + "/", wanted, found); break;
                        case "size": size = Number(reader.Value); break;
                        case "offset": offset = Number(reader.Value); break;
                        case "unpacked": unpacked = reader.TokenType == JsonToken.Boolean && (bool)reader.Value; break;
                        case "link": link = true; reader.Skip(); break;
                        default: reader.Skip(); break;
                    }
                }
                if (!folder && !link && size >= 0 && (unpacked || offset >= 0) && wanted(name) && !name.Contains("\\") && !name.Contains(".."))
                    found.Add(new AsarEntry { Path = path, Size = size, Offset = offset, Unpacked = unpacked });
            }
        }

        private static long Number(object value)
        {
            if (value is long whole) return whole;
            if (value is string text && long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long parsed)) return parsed;
            return -1;
        }

        // ---- Removing ----

        // Codex is not installed: no copy of its characters stays.
        private static bool RemoveAll()
        {
            string target = BuiltInDirectory;
            if (!Directory.Exists(target)) return false;
            bool changed = false;
            try { File.Delete(Path.Combine(target, SourceMarker)); } catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { }
            try
            {
                foreach (string folder in Directory.GetDirectories(target))
                    if (DeleteCharacterFolder(folder)) changed = true;
                if (!Directory.EnumerateFileSystemEntries(target).Any()) Directory.Delete(target);
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { }
            if (changed) PetLog.Write("codex-pets", "Codex not found: its characters were removed");
            return changed;
        }

        // A copied character's folder (and its sharper sheet). True when it is gone. A sheet in use for a moment is tried again.
        private static bool DeleteCharacterFolder(string folder)
        {
            try
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(folder); return true; } // a link: only the link
                foreach (string file in Directory.GetFiles(folder, "spritesheet.*")) SpriteSharpener.Forget(file);
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { }
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (!Directory.Exists(folder)) return true;
                    Directory.Delete(folder, true);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    System.Threading.Thread.Sleep(300);
                }
            }
            return !Directory.Exists(folder);
        }

        // ---- Helpers ----

        /// <summary>캐릭터 가져오기 / a Drive download refused: the Codex characters come only from the installed Codex.</summary>
        public const string ImportRefused = "Codex 기본 캐릭터는 가져올 수 없습니다. Codex가 설치되어 있으면 자동으로 쓸 수 있습니다.";

        /// <summary>The install folder of the Codex in use (the test hook's, or this PC's), or null.</summary>
        public static string InstallRoot()
        {
            try
            {
                if (InstallFinder != null) return InstallFinder();
                return CharacterCatalog.PetRoot != null ? null : FindInstalledRoot();
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { return null; }
        }

        /// <summary>
        /// True for a file of Codex's own: inside the copies here (CodexPets), inside the installed Codex (its package, its
        /// app.asar.unpacked), or inside any OpenAI.Codex_* package folder. 캐릭터 가져오기 refuses those.
        /// </summary>
        public static bool IsCodexSource(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string full = Path.GetFullPath(path);
                if (Under(full, Root)) return true;
                string install = InstallRoot();
                if (!string.IsNullOrWhiteSpace(install) && Under(full, install)) return true;
                for (string dir = Path.GetDirectoryName(full); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                    if (IsCodexPackage(Path.GetFileName(dir))) return true;
                return false;
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { return false; }
        }

        private static bool Under(string full, string root) =>
            full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        private static readonly Dictionary<string, byte[]> SheetHashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when this picture is byte for byte the sheet of one of the current Codex copies (SHA-256).</summary>
        public static bool IsCodexSheet(string imagePath)
        {
            try
            {
                long length = new FileInfo(imagePath).Length;
                if (!Directory.Exists(BuiltInDirectory)) return false;
                byte[] hash = null;
                foreach (string folder in Directory.GetDirectories(BuiltInDirectory))
                    foreach (string sheet in Directory.GetFiles(folder, "spritesheet.*"))
                    {
                        var file = new FileInfo(sheet);
                        if (file.Length != length) continue;
                        if (hash == null) hash = Sha256(imagePath);
                        if (Sha256Cached(file).SequenceEqual(hash)) return true;
                    }
                return false;
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { return false; }
        }

        private static byte[] Sha256Cached(FileInfo file)
        {
            string key = file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
            lock (SheetHashes)
                if (SheetHashes.TryGetValue(key, out byte[] known)) return known;
            byte[] hash = Sha256(file.FullName);
            lock (SheetHashes)
            {
                if (SheetHashes.Count > 64) SheetHashes.Clear();
                SheetHashes[key] = hash;
            }
            return hash;
        }

        private static byte[] Sha256(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(path))
                return sha.ComputeHash(stream);
        }

        /// <summary>True when this path is inside the copied Codex characters (they are Codex's: never exported).</summary>
        public static bool IsBuiltInPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string root = Path.GetFullPath(BuiltInDirectory).TrimEnd('\\') + "\\";
                return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { return false; }
        }

        private static string TitleCase(string id) =>
            string.Join(" ", id.Split('-').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));

        private static bool SameBytes(string a, string b)
        {
            var first = new FileInfo(a);
            var second = new FileInfo(b);
            if (first.Length != second.Length) return false;
            using (var x = File.OpenRead(a))
            using (var y = File.OpenRead(b))
            {
                var bx = new byte[65536];
                var by = new byte[65536];
                while (true)
                {
                    int nx = x.Read(bx, 0, bx.Length);
                    int ny = 0, n;
                    while (ny < nx && (n = y.Read(by, ny, nx - ny)) > 0) ny += n;
                    if (nx != ny) return false;
                    if (nx == 0) return true;
                    for (int i = 0; i < nx; i++) if (bx[i] != by[i]) return false;
                }
            }
        }

        // Reads at most `length` bytes of the stream from where it is (the asar's JSON header).
        private sealed class BoundedStream : Stream
        {
            private readonly Stream inner;
            private long left;
            public BoundedStream(Stream inner, long length) { this.inner = inner; left = length; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (left <= 0) return 0;
                int read = inner.Read(buffer, offset, (int)Math.Min(count, left));
                left -= read;
                return read;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
