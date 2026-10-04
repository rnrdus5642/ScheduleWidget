using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ScheduleWidget
{
    public sealed class CharacterEntry
    {
        public string Name { get; set; }
        public string ManifestPath { get; set; }
        public string ImagePath { get; set; }
        public int Rows { get; set; }
        public string Group { get; set; }
        // Optional extra actions declared in pet.json "animations" (music reactions). Only these get buttons.
        public Dictionary<string, SpriteAnimation> Animations { get; set; } = new Dictionary<string, SpriteAnimation>();
    }

    public sealed class SpriteAnimation
    {
        public int Row { get; set; }
        public int Frames { get; set; }
        public int Duration { get; set; }
    }

    /// <summary>캐릭터 가져오기 of a character that is already here (a default character's very sheet): nothing is added.</summary>
    public sealed class DuplicateCharacterException : InvalidOperationException
    {
        public DuplicateCharacterException(string existingManifest, string name)
            : base("같은 캐릭터가 이미 있습니다 (" + CharacterCatalog.DefaultGroup + ": " + name + ").") { ExistingManifest = existingManifest; }
        /// <summary>The selection of the character already here ("DefaultPets/&lt;id&gt;/pet.json").</summary>
        public string ExistingManifest { get; }
    }

    public static class CharacterCatalog
    {
        // The app's own characters (흰·검은·파란·빨간 모찌) ship next to the exe in DefaultPets\<id>\ (pet.json + spritesheet.webp)
        // and are saved as "DefaultPets/<id>/pet.json". 흰 모찌 is the character of fresh data, and the one a pet shows in place
        // of a saved character that is not available (deleted, Codex not installed, unreadable) — its saved selection stays.
        public const string DefaultManifest = "DefaultPets/mochi-white/pet.json";
        public const string DefaultGroup = "기본 캐릭터";
        public const string DefaultsFolder = "DefaultPets";
        /// <summary>The default characters' folder names, in the order 캐릭터 선택 lists them.</summary>
        public static readonly string[] DefaultIds = { "mochi-white", "mochi-black", "mochi-blue", "mochi-red" };
        /// <summary>Tests: where the default characters are (null: DefaultPets next to the exe).</summary>
        public static string DefaultPetsRoot { get; set; }
        public static string DefaultPetsDirectory => DefaultPetsRoot ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DefaultsFolder);
        // A Codex character's selection: "Characters/<id>/pet.json" (as when they were bundled), read from the copies of the
        // installed Codex (CodexPets). Kept as it is when Codex is not installed: the pet then shows 흰 모찌 in its place.
        public const string CodexManifest = "Characters/codex/pet.json";
        public const string CodexGroup = "Codex";
        public const string CodexUserGroup = "내 Codex 펫";
        public const string ImportedGroup = "가져온 캐릭터";
        /// <summary>캐릭터 선택's note when no Codex characters are available.</summary>
        public const string NoCodexNote = "Codex가 설치되어 있으면 Codex 캐릭터를 쓸 수 있습니다. 캐릭터 가져오기로 직접 추가할 수 있습니다.";
        // Imported characters live with this PC's data (like the schedules): %LocalAppData%/ScheduleWidget/Pet.
        // Moving or updating the app folder keeps them. Tests point PetRoot at their own folder.
        public static string PetRoot { get; set; }
        public static string PetDirectory => PetRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScheduleWidget", "Pet");
        // Where older versions kept them: next to the app.
        public static string LegacyPetDirectory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Pet");
        private static readonly string[] Extensions = { ".png", ".webp", ".gif", ".jpg", ".jpeg", ".bmp" };

        /// <summary>How many characters the last <see cref="MigrateLegacyPets"/> added (the startup sync then backs them up at once).</summary>
        public static int LastMigrated { get; private set; }

        /// <summary>
        /// How many old characters the last migration could not bring over (unreadable, or a picture of the wrong size): they
        /// stay in the app folder's Pet. 캐릭터 선택 can show <see cref="MigrationWarning"/>.
        /// </summary>
        public static int LastMigrationFailures { get; private set; }

        public static string MigrationWarning => LastMigrationFailures == 0 ? "" :
            "이전 버전의 캐릭터 " + LastMigrationFailures + "개를 옮기지 못했습니다. 앱 폴더의 Pet에 그대로 있습니다 (형식이나 이미지 크기를 확인해 주세요).";

        private static int loggedMigrationFailures;

        /// <summary>
        /// 레거시 Pet 이관 (app start and when 캐릭터 선택 opens): see <see cref="MigratePets"/>. Tells the app when characters
        /// were added, so 캐릭터도 구글 드라이브에 보관 (when on) backs them up right away.
        /// </summary>
        public static int MigrateLegacyPets()
        {
            int failures = 0;
            int added = PetRoot == null ? MigratePets(LegacyPetDirectory, PetDirectory, out failures) : 0;
            LastMigrated = added;
            LastMigrationFailures = failures;
            if (failures > 0 && failures != loggedMigrationFailures)
                PetLog.Write("pet-migration", failures + " old character(s) in " + LegacyPetDirectory + " could not be moved");
            loggedMigrationFailures = failures;
            if (added > 0) NotifyChanged();
            return added;
        }

        /// <summary>Goes up by one whenever a character is imported or deleted here (the Drive sync tells its own imports apart).</summary>
        public static int Version => version;
        private static int version;

        private static void NotifyChanged()
        {
            System.Threading.Interlocked.Increment(ref version);
            Changed?.Invoke();
        }

        private const string MigratedMarker = ".migrated";

        /// <summary>
        /// Characters an older version kept next to the app (app folder \Pet) become this PC's characters. Each one — a
        /// sub-folder with pet.json, a pet.json right in Pet, or a picture right in Pet — is copied as a proper character
        /// (pet.json and image checked, like 캐릭터 가져오기). Only after the copy reads back fine is the old one deleted,
        /// and the Pet folder itself once nothing is left in it; anything that could not be copied stays where it was.
        /// A folder name that is a safe ID is kept (saved selections find the character by it); other names get a new ID,
        /// written with the old name in ".migrated" so ResolveSelection still finds them. ".migrated" also lists what was
        /// copied, so a character deleted later is not brought back when the old copy could not be deleted (read-only app
        /// folder). Returns how many characters were added. Never throws: startup must not fail over old files.
        /// Links are never deleted through: when Pet (or one of its entries) is a junction or symbolic link, its characters
        /// are copied but nothing there is deleted — the files belong to wherever the link points.
        /// </summary>
        public static int MigratePets(string legacy, string target) => MigratePets(legacy, target, out _);

        public static int MigratePets(string legacy, string target, out int failures)
        {
            int added = 0, failed = 0;
            try
            {
                if (!Directory.Exists(legacy)) { failures = 0; return 0; }
                if (string.Equals(Path.GetFullPath(legacy).TrimEnd('\\'), Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { failures = 0; return 0; }
                Directory.CreateDirectory(target);
                string marker = Path.Combine(target, MigratedMarker);
                var migrated = ReadMigrated(marker);
                var done = new HashSet<string>(migrated.Keys, StringComparer.OrdinalIgnoreCase);
                bool linkedRoot = HasLinkedAncestor(legacy, Path.GetPathRoot(Path.GetFullPath(legacy)));

                // What to bring over: (key in .migrated, the pet.json or picture to import, what to delete afterwards — null
                // when it must stay: a link, or anything inside a linked Pet).
                var items = new List<Tuple<string, string, Action>>();
                foreach (string folder in Directory.GetDirectories(legacy))
                {
                    string manifest = Path.Combine(folder, "pet.json");
                    if (!File.Exists(manifest)) continue;
                    // Only what was copied goes (pet.json and its image); anything else the user kept in that folder stays,
                    // and the folder goes only once it is empty.
                    string folderImage = LooseImagePath(manifest);
                    items.Add(Tuple.Create(Path.GetFileName(folder), manifest, linkedRoot || IsLink(folder) ? null : (Action)(() => DeleteCopied(folder, manifest, folderImage))));
                }
                string rootManifest = Path.Combine(legacy, "pet.json");
                string rootImage = null;
                bool skipRootPictures = false;
                if (File.Exists(rootManifest))
                {
                    try { rootImage = Read(rootManifest).ImagePath; }
                    catch (Exception ex) when (IsImportError(ex))
                    {
                        // A pet.json that cannot be read (stays, counted): its sheet must not become a plain picture and be
                        // deleted. Unknown which picture it names → no picture right in Pet is brought over.
                        rootImage = LooseImagePath(rootManifest);
                        skipRootPictures = rootImage == null;
                    }
                    string image = rootImage;
                    items.Add(Tuple.Create(".", rootManifest, linkedRoot || IsLink(rootManifest) ? null : (Action)(() =>
                    {
                        File.Delete(rootManifest);
                        if (image != null && !IsLink(image) && string.Equals(Path.GetDirectoryName(image), Path.GetFullPath(legacy).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            File.Delete(image);
                    })));
                }
                foreach (string file in skipRootPictures ? new string[0] : Directory.GetFiles(legacy))
                {
                    if (!Extensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                    if (rootImage != null && string.Equals(Path.GetFullPath(file), rootImage, StringComparison.OrdinalIgnoreCase)) continue;
                    string picture = file;
                    items.Add(Tuple.Create(Path.GetFileName(file), file, linkedRoot || IsLink(file) ? null : (Action)(() => File.Delete(picture))));
                }

                foreach (var item in items)
                {
                    string key = item.Item1;
                    if (done.Contains(key))
                    {
                        // Copied before, but the old one could not be deleted then (read-only app folder). Delete it now only
                        // while the copy still exists: if the user deleted the copy since, the old one is the last one left.
                        string earlier = Path.Combine(target, migrated.TryGetValue(key, out string earlierId) ? earlierId : key, "pet.json");
                        if (File.Exists(earlier)) TryDelete(item.Item3);
                        continue;
                    }
                    try
                    {
                        string id = IsPetId(key) ? key : Guid.NewGuid().ToString("N");
                        string copy = Path.Combine(target, id, "pet.json");
                        bool imported = false;
                        if (!File.Exists(copy)) { ImportCore(item.Item2, target, id, allowCodex: true); imported = true; }
                        else if (!SameCharacter(item.Item2, copy))
                        {
                            // Another character already has that folder name here (a hand-made "cat" twice): this one gets a
                            // new ID instead of being taken for it (and deleted as "already copied").
                            id = Guid.NewGuid().ToString("N");
                            copy = Path.Combine(target, id, "pet.json");
                            ImportCore(item.Item2, target, id, allowCodex: true);
                            imported = true;
                        }
                        Read(copy); // the copy must read back before the original goes
                        File.AppendAllText(marker, key + (id == key ? "" : "\t" + id) + Environment.NewLine);
                        done.Add(key);
                        if (imported) added++;
                        TryDelete(item.Item3);
                    }
                    catch (Exception ex) when (IsImportError(ex)) { failed++; } // unreadable old character: left untouched (and counted)
                }
                if (!linkedRoot && !Directory.EnumerateFileSystemEntries(legacy).Any()) Directory.Delete(legacy);
            }
            catch (Exception ex) when (IsImportError(ex)) { } // never block startup; whatever was not copied stays as it was
            migratedCache = null;
            failures = failed;
            return added;
        }

        // A junction or symbolic link (deleting through it would delete someone else's files).
        private static bool IsLink(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (Exception ex) when (IsImportError(ex)) { return true; } // cannot tell: treat it as one (nothing is deleted)
        }

        // The same character already copied (a copy whose marker line was lost, or one synced back): same name and picture.
        private static bool SameCharacter(string source, string copyManifest)
        {
            try
            {
                var copy = Read(copyManifest);
                string name, image;
                if (Path.GetExtension(source).Equals(".json", StringComparison.OrdinalIgnoreCase)) { var old = Read(source); name = old.Name; image = old.ImagePath; }
                else { name = Path.GetFileNameWithoutExtension(source); image = Path.GetFullPath(source); }
                if (!string.Equals(name, copy.Name, StringComparison.Ordinal) || new FileInfo(image).Length != new FileInfo(copy.ImagePath).Length) return false;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var a = File.OpenRead(image))
                using (var b = File.OpenRead(copy.ImagePath))
                    return sha.ComputeHash(a).SequenceEqual(sha.ComputeHash(b));
            }
            catch (Exception ex) when (IsImportError(ex)) { return false; }
        }

        private static void TryDelete(Action delete)
        {
            if (delete == null) return; // behind a link: only copied
            try { delete(); } catch (Exception ex) when (IsImportError(ex)) { } // read-only app folder: ".migrated" remembers the copy
        }

        // The picture a pet.json names (full path, inside its folder), read leniently: also from a pet.json that Read refuses
        // (an unknown format version, a sheet of the wrong size). Null when it names none.
        private static string LooseImagePath(string manifestPath)
        {
            try
            {
                if (new FileInfo(manifestPath).Length > 65536) return null;
                var manifest = JObject.Parse(File.ReadAllText(manifestPath));
                string image = (manifest["spritesheetPath"] as JValue)?.Value as string ?? (manifest["imagePath"] as JValue)?.Value as string;
                if (string.IsNullOrWhiteSpace(image) || Path.IsPathRooted(image)) return null;
                string folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
                string full = Path.GetFullPath(Path.Combine(folder, image));
                return full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch (Exception ex) when (IsImportError(ex)) { return null; }
        }

        // After a legacy folder's character was copied: its image and pet.json go, then the folders they leave empty (never
        // other files the user kept there). Links are not deleted through.
        private static void DeleteCopied(string folder, string manifest, string image)
        {
            string root = Path.GetFullPath(folder).TrimEnd('\\');
            // The image itself can be an ordinary file reached through a junction in its path.
            // Never delete anything at or below a reparse point, including when checking cleanup folders.
            if (image != null && !HasLinkedAncestor(image, root) && File.Exists(image)) File.Delete(image);
            File.Delete(manifest);
            // The image's own sub-folders (e.g. "sprites\"), deepest first, while empty; then the character folder.
            for (string dir = image == null ? null : Path.GetDirectoryName(image);
                 dir != null && dir.Length > root.Length && dir.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
                 dir = Path.GetDirectoryName(dir))
            {
                if (!Directory.Exists(dir) || IsLink(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) break;
                Directory.Delete(dir);
            }
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }

        // ".migrated": one old name per line, "old<TAB>new ID" when the character got a new ID.
        private static Dictionary<string, string> ReadMigrated(string marker)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(marker)) return map;
                foreach (string line in File.ReadAllLines(marker))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string[] parts = line.Split('\t');
                    map[parts[0]] = parts.Length > 1 && IsPetId(parts[1]) ? parts[1] : parts[0];
                }
            }
            catch (Exception ex) when (IsImportError(ex)) { }
            return map;
        }

        private static Dictionary<string, string> migratedCache;
        private static string migratedCacheKey;

        /// <summary>The folder a legacy character was copied to (its own name unless it got a new ID).</summary>
        private static string MigratedId(string oldName)
        {
            string marker = Path.Combine(PetDirectory, MigratedMarker);
            string key = marker + "|" + (File.Exists(marker) ? File.GetLastWriteTimeUtc(marker).Ticks : 0);
            if (migratedCache == null || migratedCacheKey != key) { migratedCache = ReadMigrated(marker); migratedCacheKey = key; }
            return migratedCache.TryGetValue(oldName ?? "", out string id) ? id : oldName;
        }

        /// <summary>
        /// The file a saved manifest names: "Characters\&lt;id&gt;\pet.json" is a Codex character (its copy in
        /// CodexPets.BuiltInDirectory); "DefaultPets\&lt;id&gt;\pet.json" is a default character (<see cref="DefaultPetsDirectory"/>);
        /// another relative path is next to the app (as before); a full path is itself.
        /// </summary>
        public static string FullManifestPath(string manifestPath)
        {
            if (Path.IsPathRooted(manifestPath)) return Path.GetFullPath(manifestPath);
            string relative = manifestPath.Replace('/', Path.DirectorySeparatorChar);
            const string prefix = "Characters\\";
            if (relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(Path.Combine(CodexPets.BuiltInDirectory, relative.Substring(prefix.Length)));
            const string defaults = DefaultsFolder + "\\";
            if (relative.StartsWith(defaults, StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(Path.Combine(DefaultPetsDirectory, relative.Substring(defaults.Length)));
            return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, manifestPath));
        }

        public static CharacterEntry Read(string manifestPath)
        {
            string path = FullManifestPath(manifestPath);
            if (new Uri(path).IsUnc || !File.Exists(path) || new FileInfo(path).Length > 65536)
                throw new InvalidOperationException("로컬 pet.json 파일을 선택해 주세요 (최대 64KB).");
            var manifest = JObject.Parse(File.ReadAllText(path));
            string folder = Path.GetDirectoryName(path);
            string image = (string)manifest["spritesheetPath"] ?? (string)manifest["imagePath"];
            if (string.IsNullOrWhiteSpace(image) || Path.IsPathRooted(image))
                throw new InvalidOperationException("캐릭터 이미지 경로는 pet.json 폴더 안의 상대 경로여야 합니다.");
            string imagePath = Path.GetFullPath(Path.Combine(folder, image));
            if (!imagePath.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("캐릭터 폴더 밖의 이미지는 가져올 수 없습니다.");
            ValidateImage(imagePath);
            bool sprite = manifest["spritesheetPath"] != null;
            // Any other value (text like "v3", a fraction, a huge number) is not a known format — never a crash.
            JToken versionToken = manifest["spriteVersionNumber"];
            object versionValue = versionToken is JValue plain ? plain.Value : versionToken; // an object or list is no version either
            int version = versionValue == null ? 1
                : versionValue is long whole && whole >= 1 && whole <= MaxSpriteVersion ? (int)whole
                : versionValue is double real && real >= 1 && real <= MaxSpriteVersion && real == Math.Floor(real) ? (int)real : 0;
            if (sprite && RowsForVersion(version) == 0) throw new InvalidOperationException("캐릭터 형식 v1~v5(9·11·13·15·17행)를 선택해 주세요.");
            int rows = sprite ? RowsForVersion(version) : 0;
            return new CharacterEntry
            {
                ManifestPath = path, ImagePath = imagePath,
                Name = (string)manifest["displayName"] ?? Path.GetFileName(folder), Rows = rows,
                Animations = ReadExtraAnimations(manifest["animations"] as JObject, rows)
            };
        }

        // Sheet formats: spriteVersionNumber 1~5 = 9, 11, 13, 15 or 17 rows of 208 px (1536 wide). v4 / v5 are the app's own
        // larger sheets (room for the keyboard actions); Codex itself makes v1~v3 (CodexPets copies only those).
        public const int MaxSpriteVersion = 5;
        public static int RowsForVersion(int version) => version >= 1 && version <= MaxSpriteVersion ? 7 + 2 * version : 0;
        public static int VersionForRows(int rows) => rows >= 9 && rows <= 7 + 2 * MaxSpriteVersion && rows % 2 == 1 ? (rows - 7) / 2 : 1;

        // Extra actions a pet.json may declare, with their button labels. Keys not listed here are ignored.
        // keyboard1 / keyboard2: the pet's typing motions (타이핑 반응 uses them; they are ordinary actions too, but not
        // music actions: 음악 반복 leaves them out and they alone do not offer it).
        public static readonly (string Key, string Label)[] ExtraAnimationKeys =
        {
            ("listening", "음악 듣기"), ("grooving", "신남"), ("disliking", "싫어함"), ("immersed", "심취"),
            ("keyboard1", "키보드 1"), ("keyboard2", "키보드 2")
        };
        public static readonly string[] KeyboardAnimationKeys = { "keyboard1", "keyboard2" };
        /// <summary>Whether the pet declares a music action (listening, grooving, …): what 음악 반복 plays.</summary>
        public static bool HasMusicActions(CharacterEntry entry) =>
            entry?.Animations != null && entry.Animations.Keys.Any(k => Array.IndexOf(KeyboardAnimationKeys, k) < 0);

        // "animations": { "listening": { "row": 9, "frames": 6, "duration": 150 }, ... }
        // An entry is used only when its row exists in the spritesheet; frames 1~8, duration 40~2000ms (default 150).
        private static Dictionary<string, SpriteAnimation> ReadExtraAnimations(JObject animations, int rows)
        {
            var result = new Dictionary<string, SpriteAnimation>();
            if (animations == null || rows <= 0) return result;
            foreach (var (key, _) in ExtraAnimationKeys)
            {
                if (!(animations[key] is JObject spec)) continue;
                // A number too big for an int (1e20 written out) only skips or clamps that action — never the whole character.
                long? row = WholeNumber(spec["row"]);
                if (!row.HasValue || row < 0 || row >= rows) continue;
                long frames = WholeNumber(spec["frames"]) ?? 6;
                long duration = WholeNumber(spec["duration"]) ?? 150;
                result[key] = new SpriteAnimation
                {
                    Row = (int)row.Value, Frames = (int)Math.Max(1, Math.Min(8, frames)), Duration = (int)Math.Max(40, Math.Min(2000, duration))
                };
            }
            return result;
        }

        // A JSON whole number as a long; one beyond long's range (a BigInteger) becomes long.MaxValue / MinValue. Else null.
        private static long? WholeNumber(JToken token)
        {
            if (token == null || token.Type != JTokenType.Integer || !(token is JValue value)) return null;
            if (value.Value is long whole) return whole;
            if (value.Value is int small) return small;
            return (value.Value?.ToString() ?? "").TrimStart().StartsWith("-") ? long.MinValue : long.MaxValue;
        }

        /// <summary>
        /// The page draws a sheet only at exactly 1536 × 208·rows, and any picture only up to 25 million pixels. Checked from
        /// the file header whenever a character comes in (가져오기, .zip, 레거시 이관, Drive download), so a bad file is refused
        /// there instead of the pet silently not showing later. A format the header reader does not know is left to the page.
        /// (Characters already stored are not re-judged here: the page reports those, and the app shows it at that pet.)
        /// </summary>
        public static void CheckImageSize(string imagePath, int rows)
        {
            if (!TryImageSize(imagePath, out int width, out int height)) return;
            if ((long)width * height > 25000000)
                throw new InvalidOperationException("이미지가 너무 큽니다 (" + width + "×" + height + ", 최대 2,500만 픽셀).");
            if (rows > 0 && (width != 1536 || height != 208 * rows))
                throw new InvalidOperationException("스프라이트시트 크기는 1536×" + (208 * rows) + "이어야 합니다 (" + rows + "행, 지금 " + width + "×" + height + ").");
        }

        /// <summary>
        /// Width and height from the image file's header (PNG, GIF, BMP, JPEG, WebP), reading only a few bytes. False when
        /// the format is not one of these or the header is damaged — the caller then does not judge the size.
        /// </summary>
        public static bool TryImageSize(string path, out int width, out int height)
            => TryImageSize(path, out width, out height, false);

        // Import callers retry transient file-system failures while still treating an unknown image header as unsupported.
        // Keep the existing public helper lenient for its other callers.
        internal static bool TryImageSizeForImport(string path, out int width, out int height)
            => TryImageSize(path, out width, out height, true);

        private static bool TryImageSize(string path, out int width, out int height, bool propagateIo)
        {
            width = height = 0;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var head = new byte[32];
                    int read = ReadFully(stream, head, 0, head.Length);
                    if (read >= 24 && head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G' &&
                        head[12] == 'I' && head[13] == 'H' && head[14] == 'D' && head[15] == 'R')
                    { width = BigEndian(head, 16, 4); height = BigEndian(head, 20, 4); }
                    else if (read >= 10 && head[0] == 'G' && head[1] == 'I' && head[2] == 'F')
                    { width = head[6] | head[7] << 8; height = head[8] | head[9] << 8; }
                    else if (read >= 26 && head[0] == 'B' && head[1] == 'M')
                    {
                        int header = LittleEndian(head, 14, 4);
                        if (header == 12) { width = LittleEndian(head, 18, 2); height = LittleEndian(head, 20, 2); }
                        else if (header >= 40) { width = LittleEndian(head, 18, 4); height = Math.Abs(LittleEndian(head, 22, 4)); }
                    }
                    else if (read >= 30 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' &&
                             head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P')
                    {
                        string chunk = System.Text.Encoding.ASCII.GetString(head, 12, 4);
                        if (chunk == "VP8 " && head[23] == 0x9D && head[24] == 0x01 && head[25] == 0x2A)
                        { width = LittleEndian(head, 26, 2) & 0x3FFF; height = LittleEndian(head, 28, 2) & 0x3FFF; }
                        else if (chunk == "VP8L" && head[20] == 0x2F)
                        {
                            width = 1 + (((head[22] & 0x3F) << 8) | head[21]);
                            height = 1 + (((head[24] & 0x0F) << 10) | (head[23] << 2) | ((head[22] & 0xC0) >> 6));
                        }
                        else if (chunk == "VP8X") { width = 1 + LittleEndian(head, 24, 3); height = 1 + LittleEndian(head, 27, 3); }
                    }
                    else if (read >= 4 && head[0] == 0xFF && head[1] == 0xD8)
                    {
                        // JPEG: walk the segments up to the frame header (SOF0-15 except DHT / JPG / DAC).
                        stream.Position = 2;
                        var marker = new byte[9];
                        for (int segments = 0; segments < 512; segments++)
                        {
                            if (ReadFully(stream, marker, 0, 4) < 4 || marker[0] != 0xFF) break;
                            int type = marker[1];
                            if (type == 0xFF) { stream.Position -= 3; continue; } // fill byte
                            int length = BigEndian(marker, 2, 2);
                            if (length < 2) break;
                            if (type >= 0xC0 && type <= 0xCF && type != 0xC4 && type != 0xC8 && type != 0xCC)
                            {
                                if (ReadFully(stream, marker, 0, 5) < 5) break;
                                height = BigEndian(marker, 1, 2); width = BigEndian(marker, 3, 2);
                                break;
                            }
                            stream.Position += length - 2;
                        }
                    }
                }
            }
            catch (Exception ex) when (IsImportError(ex) && (!propagateIo || !(ex is IOException || ex is UnauthorizedAccessException)))
            { width = height = 0; }
            return width > 0 && height > 0;
        }

        private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
        {
            int total = 0, n;
            while (total < count && (n = stream.Read(buffer, offset + total, count - total)) > 0) total += n;
            return total;
        }

        private static int BigEndian(byte[] data, int offset, int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++) value = value << 8 | data[offset + i];
            return value;
        }

        private static int LittleEndian(byte[] data, int offset, int count)
        {
            int value = 0;
            for (int i = count - 1; i >= 0; i--) value = value << 8 | data[offset + i];
            return value;
        }

        private static void ValidateImage(string path)
        {
            if (new Uri(Path.GetFullPath(path)).IsUnc || !Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()) ||
                !File.Exists(path) || new FileInfo(path).Length > 25 * 1024 * 1024)
                throw new InvalidOperationException("25MB 이하의 로컬 PNG, WebP, GIF, JPG 또는 BMP 이미지를 선택해 주세요.");
        }

        public static List<CharacterEntry> Load(out string warning)
        {
            var result = new List<CharacterEntry>();
            int failures = 0;
            // The app's default characters, the Codex characters (only while Codex is installed: CodexPets), the user's own
            // Codex pets, the imported ones.
            string[] roots = { DefaultPetsDirectory, CodexPets.BuiltInDirectory, CodexPets.UserPetsDirectory, PetDirectory };
            string[] groups = { DefaultGroup, CodexGroup, CodexUserGroup, ImportedGroup };
            for (int i = 0; i < roots.Length; i++)
            {
                try
                {
                    if (roots[i] == null || !Directory.Exists(roots[i])) continue;
                    foreach (string directory in Directory.GetDirectories(roots[i]))
                    {
                        if (directory.EndsWith(".importing", StringComparison.OrdinalIgnoreCase) || directory.EndsWith(".copying", StringComparison.OrdinalIgnoreCase)) continue;
                        string path = Path.Combine(directory, "pet.json");
                        if (!File.Exists(path)) continue;
                        try
                        {
                            var entry = Read(path);
                            entry.Group = groups[i];
                            result.Add(entry);
                        }
                        catch (Exception ex) when (IsImportError(ex)) { failures++; }
                    }
                }
                catch (Exception ex) when (IsImportError(ex)) { failures++; }
            }
            warning = failures == 0 ? "" : failures + "개 캐릭터를 읽지 못했습니다. 파일 형식을 확인해 주세요.";
            // 기본 캐릭터 first (흰 모찌 first), then as before.
            return result.OrderBy(c => c.Group == DefaultGroup ? 0 : 1).ThenBy(DefaultOrder)
                .ThenBy(c => c.Name == "Codex" ? 0 : 1).ThenBy(c => c.Group).ThenBy(c => c.Name).ToList();
        }

        private static int DefaultOrder(CharacterEntry entry)
        {
            if (entry.Group != DefaultGroup) return 0;
            int index = Array.FindIndex(DefaultIds, id => string.Equals(id, Path.GetFileName(Path.GetDirectoryName(entry.ManifestPath)), StringComparison.OrdinalIgnoreCase));
            return index < 0 ? DefaultIds.Length : index;
        }

        /// <summary>
        /// "DefaultPets/&lt;id&gt;/pet.json" when a saved manifest means a default character: that key (any slashes) or a full path
        /// into the default characters' folder (also the app folder's DefaultPets of this or an older install). Else null.
        /// </summary>
        public static string DefaultKey(string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath)) return null;
            try
            {
                string normalized = manifestPath.Trim().Replace('/', Path.DirectorySeparatorChar);
                if (!Path.GetFileName(normalized).Equals("pet.json", StringComparison.OrdinalIgnoreCase)) return null;
                string folder = Path.GetDirectoryName(normalized);
                string id = Path.GetFileName(folder);
                if (!IsPetId(id)) return null;
                bool isDefault = Path.IsPathRooted(normalized)
                    ? IsDirectChild(DefaultPetsDirectory, folder) || DefaultIds.Contains(id, StringComparer.OrdinalIgnoreCase) &&
                      string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(folder))), DefaultsFolder, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(Path.GetDirectoryName(folder), DefaultsFolder, StringComparison.OrdinalIgnoreCase);
                return isDefault ? DefaultsFolder + "/" + id + "/pet.json" : null;
            }
            catch (Exception ex) when (IsImportError(ex)) { return null; }
        }

        /// <summary>The default character (its pet.json) whose sheet is byte for byte this picture, or null.</summary>
        public static string DefaultWithSheet(string imagePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath) || !Directory.Exists(DefaultPetsDirectory)) return null;
                foreach (string directory in Directory.GetDirectories(DefaultPetsDirectory))
                {
                    string manifest = Path.Combine(directory, "pet.json");
                    if (!File.Exists(manifest)) continue;
                    try
                    {
                        var entry = Read(manifest);
                        if (SameImage(entry.ImagePath, imagePath)) return DefaultsFolder + "/" + Path.GetFileName(directory) + "/pet.json";
                    }
                    catch (Exception ex) when (IsImportError(ex)) { }
                }
            }
            catch (Exception ex) when (IsImportError(ex)) { }
            return null;
        }

        /// <summary>Raised after a character is imported or deleted here (구글 드라이브 캐릭터 보관 syncs soon after).</summary>
        public static event Action Changed;

        /// <summary>Folder names (IDs) of the imported characters in <see cref="PetDirectory"/>.</summary>
        public static List<string> ImportedIds()
        {
            try
            {
                if (!Directory.Exists(PetDirectory)) return new List<string>();
                return Directory.GetDirectories(PetDirectory).Select(Path.GetFileName)
                    .Where(IsPetId).Where(id => File.Exists(Path.Combine(PetDirectory, id, "pet.json"))).ToList();
            }
            catch (Exception ex) when (IsImportError(ex)) { return new List<string>(); }
        }

        // Letters, digits, - and _ only: safe as a folder name whatever a synced file claims.
        public static bool IsPetId(string id) => !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsLetterOrDigit(c) && c < 128 || c == '-' || c == '_');

        public static string Import(string source, string destinationDirectory = null) => Import(source, destinationDirectory, null);

        /// <summary>Copies a character into its own folder (a new ID, or <paramref name="folderId"/> for one synced from another PC).</summary>
        public static string Import(string source, string destinationDirectory, string folderId)
        {
            string result = ImportCore(source, destinationDirectory, folderId);
            NotifyChanged();
            return result;
        }

        // allowCodex: only for checks that unpack into a scratch folder (ZipCopiesBuiltIn, ZipHasCodexSheet) and the old
        // app-folder Pet 이관; 캐릭터 가져오기 and Drive downloads never bring in a Codex character (its files, or its very sheet).
        private static string ImportCore(string source, string destinationDirectory, string folderId, string fallbackName = null, bool allowCodex = false)
        {
            if (!allowCodex && CodexPets.IsCodexSource(source)) throw new InvalidOperationException(CodexPets.ImportRefused);
            if (Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase)) return ImportZip(source, destinationDirectory, folderId, allowCodex);
            CharacterEntry entry = null;
            if (Path.GetExtension(source).Equals(".json", StringComparison.OrdinalIgnoreCase)) entry = Read(source);
            string image = entry?.ImagePath ?? Path.GetFullPath(source);
            ValidateImage(image);
            if (!allowCodex && (CodexPets.IsCodexSource(image) || CodexPets.IsCodexSheet(image))) throw new InvalidOperationException(CodexPets.ImportRefused);
            CheckImageSize(image, entry?.Rows ?? 0);
            // A default character's very sheet is already here (기본 캐릭터): not added a second time.
            string existingDefault = allowCodex ? null : DefaultWithSheet(image);
            if (existingDefault != null) throw new DuplicateCharacterException(existingDefault, Read(existingDefault).Name);
            if (folderId != null && !IsPetId(folderId)) throw new InvalidOperationException("캐릭터 ID가 올바르지 않습니다.");
            string folder = Path.Combine(destinationDirectory ?? PetDirectory, folderId ?? Guid.NewGuid().ToString("N"));
            if (Directory.Exists(folder))
            {
                if (File.Exists(Path.Combine(folder, "pet.json"))) throw new InvalidOperationException("같은 캐릭터가 이미 있습니다.");
                // Leftovers of a broken earlier copy (no pet.json: never listed, never synced) — like the phone app, they make
                // room instead of blocking this character for good (a Drive download would otherwise fail every time).
                Directory.Delete(folder, true);
            }
            // Built in a side folder and moved into place at the end, so the list never sees half a character.
            string building = folder + ".importing";
            if (Directory.Exists(building)) Directory.Delete(building, true);
            Directory.CreateDirectory(building);
            try
            {
                string name = "image" + Path.GetExtension(image).ToLowerInvariant();
                File.Copy(image, Path.Combine(building, name));
                var manifest = BuildManifest(fallbackName ?? entry?.Name ?? Path.GetFileNameWithoutExtension(source), name, entry?.Rows ?? 0, entry?.Animations);
                File.WriteAllText(Path.Combine(building, "pet.json"), manifest.ToString());
                Directory.Move(building, folder);
            }
            finally { if (Directory.Exists(building)) try { Directory.Delete(building, true); } catch (Exception ex) when (IsImportError(ex)) { } }
            return Path.Combine(folder, "pet.json");
        }

        // A clean pet.json for a copied character: name, its image (spritesheet or single picture), format and extra actions.
        private static JObject BuildManifest(string displayName, string imageName, int rows, Dictionary<string, SpriteAnimation> animations)
        {
            var manifest = new JObject { ["displayName"] = displayName };
            manifest[rows > 0 ? "spritesheetPath" : "imagePath"] = imageName;
            if (rows > 0) manifest["spriteVersionNumber"] = VersionForRows(rows);
            if (animations?.Count > 0)
                manifest["animations"] = new JObject(animations.Select(a => new JProperty(a.Key,
                    new JObject { ["row"] = a.Value.Row, ["frames"] = a.Value.Frames, ["duration"] = a.Value.Duration })));
            return manifest;
        }

        /// <summary>
        /// 캐릭터 내보내기: one .zip with pet.json and the image, which 캐릭터 가져오기 on another PC reads back as the same
        /// character (name, format and extra actions). Written to a temp file first so a failed export never leaves half a file.
        /// </summary>
        public static void Export(CharacterEntry entry, string zipPath)
        {
            if (entry == null) throw new InvalidOperationException("내보낼 캐릭터를 선택해 주세요.");
            if (!CanExport(entry)) throw new InvalidOperationException("Codex 캐릭터는 Codex에 포함된 캐릭터라 내보낼 수 없습니다.");
            ValidateImage(entry.ImagePath);
            string imageName = (entry.Rows > 0 ? "spritesheet" : "image") + Path.GetExtension(entry.ImagePath).ToLowerInvariant();
            string manifest = BuildManifest(entry.Name, imageName, entry.Rows, entry.Animations).ToString();
            string temp = zipPath + ".exporting";
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
                using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create))
                {
                    using (var writer = new StreamWriter(zip.CreateEntry("pet.json").Open(), new System.Text.UTF8Encoding(false)))
                        writer.Write(manifest);
                    using (var target = zip.CreateEntry(imageName, System.IO.Compression.CompressionLevel.NoCompression).Open())
                    using (var image = File.OpenRead(entry.ImagePath))
                        image.CopyTo(target);
                }
                if (File.Exists(zipPath)) File.Delete(zipPath);
                File.Move(temp, zipPath);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        /// <summary>
        /// Reads an exported character (.zip): pet.json at the top or in one folder, plus the image it names. Only those two
        /// small files are unpacked (into a temp folder, never outside it), then they go through the normal pet.json import.
        /// </summary>
        private static string ImportZip(string zipPath, string destinationDirectory, string folderId, bool allowCodex)
        {
            string full = Path.GetFullPath(zipPath);
            if (new Uri(full).IsUnc || !File.Exists(full) || new FileInfo(full).Length > 30 * 1024 * 1024)
                throw new InvalidOperationException("30MB 이하의 로컬 캐릭터 파일(.zip)을 선택해 주세요.");
            string temp = Path.Combine(Path.GetTempPath(), "ScheduleWidgetPet-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                using (var zip = System.IO.Compression.ZipFile.OpenRead(full))
                {
                    var manifestEntry = zip.Entries
                        .Where(e => e.Name.Equals("pet.json", StringComparison.OrdinalIgnoreCase) && e.FullName.Count(c => c == '/' || c == '\\') <= 1)
                        .OrderBy(e => e.FullName.Length).FirstOrDefault();
                    if (manifestEntry == null) throw new InvalidOperationException("캐릭터 파일 안에 pet.json이 없습니다.");
                    if (manifestEntry.Length > 65536) throw new InvalidOperationException("pet.json이 너무 큽니다 (최대 64KB).");
                    string prefix = manifestEntry.FullName.Substring(0, manifestEntry.FullName.Length - manifestEntry.Name.Length);
                    string manifestPath = Path.Combine(temp, "pet.json");
                    Extract(manifestEntry, manifestPath, 65536);
                    var manifest = JObject.Parse(File.ReadAllText(manifestPath));
                    string image = ((string)manifest["spritesheetPath"] ?? (string)manifest["imagePath"])?.Replace('\\', '/');
                    if (string.IsNullOrWhiteSpace(image) || image.StartsWith("/") || image.Split('/').Contains(".."))
                        throw new InvalidOperationException("캐릭터 이미지 경로는 pet.json 폴더 안의 상대 경로여야 합니다.");
                    var imageEntry = zip.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(prefix.Replace('\\', '/') + image, StringComparison.OrdinalIgnoreCase));
                    if (imageEntry == null) throw new InvalidOperationException("캐릭터 파일 안에 이미지(" + image + ")가 없습니다.");
                    string imagePath = Path.GetFullPath(Path.Combine(temp, image.Replace('/', Path.DirectorySeparatorChar)));
                    if (!imagePath.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("캐릭터 폴더 밖의 이미지는 가져올 수 없습니다.");
                    Directory.CreateDirectory(Path.GetDirectoryName(imagePath));
                    Extract(imageEntry, imagePath, 25 * 1024 * 1024);
                    // No displayName: named after the folder inside the zip, else the zip file — never the temp folder
                    // ("ScheduleWidgetPet-<guid>") this pet.json was unpacked into.
                    string fallback = null;
                    if (!(manifest["displayName"] is JValue named) || named.Value == null)
                    {
                        string inner = prefix.Replace('\\', '/').Trim('/');
                        fallback = inner.Length > 0 ? inner : Path.GetFileNameWithoutExtension(full);
                    }
                    return ImportCore(manifestPath, destinationDirectory, folderId, fallback, allowCodex);
                }
            }
            catch (InvalidDataException ex) { throw new InvalidOperationException("캐릭터 파일(.zip)을 읽을 수 없습니다. " + ex.Message, ex); }
            finally { try { Directory.Delete(temp, true); } catch (Exception ex) when (IsImportError(ex)) { } }
        }

        /// <summary>
        /// True when an exported character (.zip) is just a copy of a Codex one (copied from the installed Codex, or one of the
        /// user's own Codex pets): one with the same name (case and spaces aside) and the very same picture. The Drive sync uses it for a Drive character named like a built-in — a
        /// copy is not brought in twice, a pet of the user's own that merely shares the name is. Unpacked into a temp folder
        /// only (nothing is added to Pet). Throws like 가져오기 when the file cannot be read.
        /// </summary>
        public static bool ZipCopiesBuiltIn(string zipPath)
        {
            string scratch = Path.Combine(Path.GetTempPath(), "ScheduleWidgetPetCheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(scratch);
                var entry = Read(ImportCore(zipPath, scratch, null, allowCodex: true));
                string Key(string name) => (name ?? "").Trim().ToLowerInvariant();
                var folders = new List<string>();
                foreach (string root in new[] { DefaultPetsDirectory, CodexPets.BuiltInDirectory, CodexPets.UserPetsDirectory })
                    try { if (root != null && Directory.Exists(root)) folders.AddRange(Directory.GetDirectories(root)); }
                    catch (Exception ex) when (IsImportError(ex)) { }
                foreach (string directory in folders)
                {
                    string manifest = Path.Combine(directory, "pet.json");
                    if (!File.Exists(manifest)) continue;
                    try
                    {
                        var builtIn = Read(manifest);
                        if (Key(builtIn.Name) == Key(entry.Name) && SameImage(builtIn.ImagePath, entry.ImagePath)) return true;
                    }
                    catch (Exception ex) when (IsImportError(ex)) { }
                }
                return false;
            }
            finally { try { Directory.Delete(scratch, true); } catch (Exception ex) when (IsImportError(ex)) { } }
        }

        /// <summary>
        /// True when an exported character (.zip)'s picture is byte for byte a current Codex character's sheet (whatever its
        /// name): the Drive sync never brings that in. Unpacked into a temp folder only. Throws like 가져오기 when unreadable.
        /// </summary>
        public static bool ZipHasCodexSheet(string zipPath)
        {
            string scratch = Path.Combine(Path.GetTempPath(), "ScheduleWidgetPetCheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(scratch);
                return CodexPets.IsCodexSheet(Read(ImportCore(zipPath, scratch, null, allowCodex: true)).ImagePath);
            }
            finally { try { Directory.Delete(scratch, true); } catch (Exception ex) when (IsImportError(ex)) { } }
        }

        /// <summary>
        /// True when an exported character (.zip)'s picture is byte for byte a default character's sheet (whatever its name):
        /// the Drive sync skips it like a copy (that character is already here). Unpacked into a temp folder only.
        /// </summary>
        public static bool ZipHasDefaultSheet(string zipPath)
        {
            string scratch = Path.Combine(Path.GetTempPath(), "ScheduleWidgetPetCheck-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(scratch);
                return DefaultWithSheet(Read(ImportCore(zipPath, scratch, null, allowCodex: true)).ImagePath) != null;
            }
            finally { try { Directory.Delete(scratch, true); } catch (Exception ex) when (IsImportError(ex)) { } }
        }

        private static bool SameImage(string a, string b)
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var first = File.OpenRead(a))
            using (var second = File.OpenRead(b))
                return sha.ComputeHash(first).SequenceEqual(sha.ComputeHash(second));
        }

        // Copies one zip entry, refusing more than `limit` bytes whatever the entry claims.
        private static void Extract(System.IO.Compression.ZipArchiveEntry entry, string path, long limit)
        {
            if (entry.Length > limit) throw new InvalidOperationException("25MB 이하의 로컬 PNG, WebP, GIF, JPG 또는 BMP 이미지를 선택해 주세요.");
            using (var source = entry.Open())
            using (var target = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > limit) throw new InvalidOperationException("캐릭터 파일 안의 항목이 너무 큽니다.");
                    target.Write(buffer, 0, read);
                }
            }
        }

        /// <summary>
        /// 내보내기: not for the characters copied from the installed Codex (they are Codex's own, not the user's to pass on).
        /// The user's own Codex pets and imported characters can be exported.
        /// </summary>
        public static bool CanExport(CharacterEntry entry) =>
            entry != null && entry.Group != CodexGroup && !CodexPets.IsBuiltInPath(entry.ManifestPath) && !CodexPets.IsBuiltInPath(entry.ImagePath);

        public static bool CanDelete(CharacterEntry entry)
        {
            if (entry == null || entry.Group != ImportedGroup) return false;
            try
            {
                string manifest = Path.GetFullPath(entry.ManifestPath);
                if (!Path.GetFileName(manifest).Equals("pet.json", StringComparison.OrdinalIgnoreCase) || !File.Exists(manifest)) return false;
                string folder = Path.GetDirectoryName(manifest);
                return IsDirectChild(PetDirectory, folder);
            }
            catch (Exception ex) when (IsImportError(ex)) { return false; }
        }

        public static void Delete(CharacterEntry entry)
        {
            if (!CanDelete(entry)) throw new InvalidOperationException("가져온 캐릭터만 삭제할 수 있습니다.");
            string folder = Path.GetDirectoryName(Path.GetFullPath(entry.ManifestPath));
            SpriteSharpener.Forget(entry.ImagePath); // its sharper 2× sheet goes too (and one being made is never kept)
            try { Directory.Delete(folder, true); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // The sharpener may have been reading the sheet just then (a file being deleted keeps its folder a moment).
                System.Threading.Thread.Sleep(300);
                Directory.Delete(folder, true);
            }
            string id = Path.GetFileName(folder);
            lock (DeletedGate) deletedKeys.Add("Pet/" + id); // its remembered spot on the calendar goes too (DataManager)
            if (IsPetId(id)) RememberDeleted(id);
            NotifyChanged();
        }

        // ".deleted" in the Pet folder: characters deleted here, one ID per line, until the Drive sync (캐릭터도 구글 드라이브에
        // 보관) has deleted them there too. Only these are ever deleted on Drive — a character that is just missing here (a
        // wiped or unreadable Pet folder) is downloaded again instead.
        private const string DeletedMarker = ".deleted";
        private static readonly object DeletedGate = new object();
        private static readonly HashSet<string> deletedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A character deleted while the app runs ("Pet/&lt;ID&gt;", like SelectionKey): its remembered spot is dropped.</summary>
        public static bool IsDeletedKey(string key)
        {
            lock (DeletedGate) return key != null && deletedKeys.Contains(key);
        }

        /// <summary>Characters deleted here that Drive may still have.</summary>
        public static List<string> DeletedIds()
        {
            lock (DeletedGate)
            {
                try
                {
                    string marker = Path.Combine(PetDirectory, DeletedMarker);
                    return File.Exists(marker) ? File.ReadAllLines(marker).Select(l => l.Trim()).Where(IsPetId).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
                }
                catch (Exception ex) when (IsImportError(ex)) { return new List<string>(); }
            }
        }

        /// <summary>The Drive sync is done with these (deleted there, or never there).</summary>
        public static void ForgetDeleted(IEnumerable<string> ids)
        {
            var done = new HashSet<string>(ids ?? new string[0], StringComparer.OrdinalIgnoreCase); // IDs are folder names (Windows ignores case)
            if (done.Count == 0) return;
            lock (DeletedGate)
            {
                try
                {
                    string marker = Path.Combine(PetDirectory, DeletedMarker);
                    var left = DeletedIds().Where(id => !done.Contains(id)).ToList();
                    if (left.Count == 0) { if (File.Exists(marker)) File.Delete(marker); }
                    else File.WriteAllLines(marker, left);
                }
                catch (Exception ex) when (IsImportError(ex)) { } // tried again after the next sync
            }
        }

        private static void RememberDeleted(string id)
        {
            lock (DeletedGate)
            {
                try
                {
                    var ids = DeletedIds();
                    if (ids.Contains(id, StringComparer.OrdinalIgnoreCase)) return;
                    ids.Add(id);
                    File.WriteAllLines(Path.Combine(PetDirectory, DeletedMarker), ids.Skip(Math.Max(0, ids.Count - 500))); // the latest 500
                }
                catch (Exception ex) when (IsImportError(ex)) { } // not remembered: Drive keeps its copy (nothing is lost)
            }
        }

        /// <summary>
        /// Characters an older version deleted here: it kept no record of them (a synced character missing here was deleted on
        /// Drive by its next sync), so one deleted while 캐릭터도 구글 드라이브에 보관 was off — or just before that sync — would
        /// come back. Of <paramref name="synced"/> (SyncedPetIds), each without a folder here is remembered as deleted now,
        /// like a delete in 캐릭터 선택. With no imported character here at all (a wiped Pet folder, a new PC) nothing is:
        /// those are downloaded again. Once, by the first character sync of this version. Returns how many were remembered.
        /// </summary>
        /// <param name="legacyDirectory">the app folder's old Pet (default: <see cref="LegacyPetDirectory"/> when PetRoot is not
        /// set): a character still there (its 이관 failed, e.g. a sheet of the wrong size) is not deleted — its Drive copy may
        /// be the only other one.</param>
        public static int RememberOldDeletions(IEnumerable<string> synced, string legacyDirectory = null)
        {
            if (ImportedIds().Count == 0) return 0;
            string legacy = legacyDirectory ?? (PetRoot == null ? LegacyPetDirectory : null);
            int remembered = 0;
            foreach (string id in (synced ?? new string[0]).Where(IsPetId).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                if (Directory.Exists(Path.Combine(PetDirectory, id))) continue; // here (also a folder that cannot be read now)
                if (legacy != null && Directory.Exists(Path.Combine(legacy, id))) continue; // still in the old Pet (not brought over)
                RememberDeleted(id);
                remembered++;
            }
            return remembered;
        }

        /// <summary>
        /// A character's id that stays the same across installs and PCs: "Characters/&lt;folder&gt;" for the Codex ones,
        /// "CodexPet/&lt;folder&gt;" for the user's own Codex pets, "Pet/&lt;folder&gt;" for imported ones (an old absolute path
        /// maps by its folder name, like ResolveSelection).
        /// </summary>
        public static string SelectionKey(string manifestPath)
        {
            string resolved = ResolveSelection(manifestPath);
            try
            {
                string folder = Path.GetFileName(Path.GetDirectoryName(resolved.Replace('/', Path.DirectorySeparatorChar)));
                if (!Path.IsPathRooted(resolved) && resolved.Replace('\\', '/').StartsWith(DefaultsFolder + "/", StringComparison.OrdinalIgnoreCase))
                    return DefaultsFolder + "/" + folder;
                bool builtIn =!Path.IsPathRooted(resolved) && resolved.Replace('\\', '/').StartsWith("Characters/", StringComparison.OrdinalIgnoreCase);
                if (builtIn) return "Characters/" + folder;
                string user = CodexPets.UserPetsDirectory;
                if (user != null && IsDirectChild(user, Path.GetDirectoryName(resolved))) return "CodexPet/" + folder;
                return "Pet/" + folder;
            }
            catch (Exception ex) when (IsImportError(ex)) { return DefaultsFolder + "/" + DefaultIds[0]; }
        }

        /// <summary>
        /// The character a saved selection means: a default character's "DefaultPets/&lt;id&gt;/pet.json", a Codex character's
        /// "Characters\&lt;id&gt;\pet.json" (also while Codex is not installed — the pet then shows 흰 모찌 in its place, and gets
        /// it back with Codex), an imported or the user's own Codex pet's pet.json, or <see cref="DefaultManifest"/> for one
        /// that is gone.
        /// </summary>
        public static string ResolveSelection(string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath)) return DefaultManifest;
            try
            {
                string builtInDefault = DefaultKey(manifestPath);
                if (builtInDefault != null) return builtInDefault;
                string codex = CodexKey(manifestPath);
                if (codex != null) return codex;
                string path = FullManifestPath(manifestPath);
                if (!Path.GetFileName(path).Equals("pet.json", StringComparison.OrdinalIgnoreCase)) return DefaultManifest;
                string folder = Path.GetDirectoryName(path);
                if (IsDirectChild(PetDirectory, folder) && File.Exists(path)) return path;
                string user = CodexPets.UserPetsDirectory;
                if (user != null && IsDirectChild(user, folder) && File.Exists(path)) return path;
                // A saved path from an older install can select only its existing Pet copy (a legacy character that got a
                // new ID on 이관 is found through ".migrated").
                string petCopy = Path.Combine(PetDirectory, MigratedId(Path.GetFileName(folder)), "pet.json");
                return File.Exists(petCopy) ? petCopy : DefaultManifest;
            }
            catch (Exception ex) when (IsImportError(ex)) { return DefaultManifest; }
        }

        /// <summary>
        /// "Characters\&lt;id&gt;\pet.json" when a saved manifest means a Codex character: that key itself, a full path into the
        /// copied Codex characters, or an older install's app-folder Characters\&lt;id&gt;\pet.json (one of the old built-in IDs,
        /// or this app's own Characters folder). Else null.
        /// </summary>
        private static string CodexKey(string manifestPath)
        {
            string normalized = manifestPath.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (!Path.GetFileName(normalized).Equals("pet.json", StringComparison.OrdinalIgnoreCase)) return null;
            if (!Path.IsPathRooted(normalized))
            {
                string relativeFolder = Path.GetDirectoryName(normalized);
                string relativeId = Path.GetFileName(relativeFolder);
                return IsPetId(relativeId) && string.Equals(Path.GetDirectoryName(relativeFolder), "Characters", StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine("Characters", relativeId, "pet.json") : null;
            }
            string folder = Path.GetDirectoryName(Path.GetFullPath(normalized));
            string id = Path.GetFileName(folder);
            if (!IsPetId(id)) return null;
            if (IsDirectChild(CodexPets.BuiltInDirectory, folder)) return Path.Combine("Characters", id, "pet.json");
            if (string.Equals(Path.GetFileName(Path.GetDirectoryName(folder)), "Characters", StringComparison.OrdinalIgnoreCase) &&
                (CodexPets.KnownNames.ContainsKey(id) || IsDirectChild(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Characters"), folder)))
                return Path.Combine("Characters", id, "pet.json");
            return null;
        }

        /// <summary>
        /// True when a resolved selection is a Codex character that is not here now (Codex not installed, or this version of
        /// it has no such character): the pet shows 흰 모찌 in its place — no error, and its saved selection stays.
        /// </summary>
        public static bool IsMissingCodex(string resolvedManifest)
        {
            if (string.IsNullOrWhiteSpace(resolvedManifest) || Path.IsPathRooted(resolvedManifest)) return false;
            if (!resolvedManifest.Replace('\\', '/').StartsWith("Characters/", StringComparison.OrdinalIgnoreCase)) return false;
            try { return !File.Exists(FullManifestPath(resolvedManifest)); }
            catch (Exception ex) when (IsImportError(ex)) { return true; }
        }

        /// <summary>
        /// True when a resolved selection names a character that is not here now: a Codex one (see <see cref="IsMissingCodex"/>)
        /// or a default one whose files are missing. The pet then shows 흰 모찌 in its place (when that one is here).
        /// </summary>
        public static bool IsUnavailable(string resolvedManifest)
        {
            if (IsMissingCodex(resolvedManifest)) return true;
            if (string.IsNullOrWhiteSpace(resolvedManifest) || Path.IsPathRooted(resolvedManifest)) return false;
            if (!resolvedManifest.Replace('\\', '/').StartsWith(DefaultsFolder + "/", StringComparison.OrdinalIgnoreCase)) return false;
            try { return !File.Exists(FullManifestPath(resolvedManifest)); }
            catch (Exception ex) when (IsImportError(ex)) { return true; }
        }

        /// <summary>What a pet saves when this character is chosen (a default or Codex character by its stable key).</summary>
        public static string SelectionManifest(CharacterEntry entry)
        {
            string folder = Path.GetFileName(Path.GetDirectoryName(entry.ManifestPath));
            if (entry.Group == DefaultGroup || DefaultKey(entry.ManifestPath) != null) return DefaultsFolder + "/" + folder + "/pet.json";
            return entry.Group == CodexGroup || CodexPets.IsBuiltInPath(entry.ManifestPath)
                ? Path.Combine("Characters", folder, "pet.json") : entry.ManifestPath;
        }

        /// <summary>
        /// True when a saved selection that <see cref="ResolveSelection"/> shows as Codex must still be kept as it is: its
        /// character is not gone, only not brought over yet (a legacy app-folder Pet whose 이관 failed). The pet shows Codex
        /// meanwhile, and gets its own character back once it is brought over — instead of the slot being rewritten for good.
        /// </summary>
        public static bool KeepSavedSelection(string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || ResolveSelection(manifestPath) != DefaultManifest) return false;
            try
            {
                if (CodexKey(manifestPath) != null || DefaultKey(manifestPath) != null) return false; // ResolveSelection keeps these by itself
                string path = FullManifestPath(manifestPath);
                if (!Path.GetFileName(path).Equals("pet.json", StringComparison.OrdinalIgnoreCase)) return false;
                string folder = Path.GetDirectoryName(path);
                // A deleted character (its folder in Pet, or in the user's Codex pets, gone) is not kept: that one does fall back.
                string user = CodexPets.UserPetsDirectory;
                if (IsDirectChild(PetDirectory, folder) || user != null && IsDirectChild(user, folder)) return false;
                string legacy = PetRoot == null ? LegacyPetDirectory : null;
                return File.Exists(path) || legacy != null && File.Exists(Path.Combine(legacy, Path.GetFileName(folder), "pet.json"));
            }
            catch (Exception ex) when (IsImportError(ex)) { return false; }
        }

        private static bool IsDirectChild(string root, string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return false;
            char[] separators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            string parent = Path.GetDirectoryName(Path.GetFullPath(folder))?.TrimEnd(separators);
            return string.Equals(parent, Path.GetFullPath(root).TrimEnd(separators), StringComparison.OrdinalIgnoreCase);
        }

        // sharpen (the pet windows): the sheet's sharper 2× copy (SpriteSharpener) goes along as "sharp" when it is ready, else
        // it is made in the background. The page draws it only where the pet is shown larger than the sheet.
        internal static object BrowserEntry(CoreWebView2 browser, CharacterEntry entry, int index, bool sharpen = false)
        {
            // Every character ships its image as spritesheet.webp / image.*, so an index-based host gave the same URL
            // to different characters and WebView2 kept showing the cached previous image. Key the host by the image
            // file's path and version so each character (and each re-import) gets its own URL.
            var file = new FileInfo(entry.ImagePath);
            string version = file.FullName.ToLowerInvariant() + "|" + (file.Exists ? file.LastWriteTimeUtc.Ticks + "|" + file.Length : "missing");
            string key;
            using (var sha = System.Security.Cryptography.SHA1.Create())
                key = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(version)), 0, 8).Replace("-", "").ToLowerInvariant();
            string host = "character-" + key + ".example";
            browser.SetVirtualHostNameToFolderMapping(host, file.DirectoryName, CoreWebView2HostResourceAccessKind.Allow);
            var extra = entry.Animations.ToDictionary(a => a.Key, a => (object)new { row = a.Value.Row, count = a.Value.Frames, duration = a.Value.Duration, last = a.Value.Duration });
            // Photos stay as they are; the picker (small frames) never gets it, so it never loads a 66 MB-decoded sheet.
            string sharp = sharpen && entry.Rows > 0 ? SpriteSharpener.PageAddress(browser, entry.ImagePath) : "";
            return new { name = entry.Name, group = entry.Group, rows = entry.Rows, extra,
                image = "https://" + host + "/" + Uri.EscapeDataString(file.Name) + "?v=" + key, sharp };
        }
        private static bool HasLinkedAncestor(string path, string root)
        {
            string boundary = Path.GetFullPath(root).TrimEnd('\\');
            string current = Path.GetFullPath(path);
            if (!string.Equals(current, boundary, StringComparison.OrdinalIgnoreCase) &&
                !current.StartsWith(boundary + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            while (current != null)
            {
                if (IsLink(current)) return true;
                current = Path.GetDirectoryName(current);
            }
            return false;
        }

        // FormatException too: a bad path (UriFormatException) or an odd value in a pet.json must fail that one character, never
        // crash the picker, an import or the app's start.
        public static bool IsImportError(Exception ex) => ex is IOException || ex is UnauthorizedAccessException ||
            ex is ArgumentException || ex is InvalidOperationException || ex is JsonException || ex is NotSupportedException || ex is OverflowException ||
            ex is FormatException || ex is System.Security.SecurityException;
    }

}
