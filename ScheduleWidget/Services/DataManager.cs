using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using Newtonsoft.Json;

namespace ScheduleWidget
{
    public sealed class DataManager : IAppDataStore
    {
        private const string AppName = "ScheduleWidget";
        private const string DataFileName = "schedules.json";

        private readonly string dataDirectory;
        private readonly string jsonPath;
        private readonly string backupPath;
        private readonly string legacyJsonPath;

        // What this manager last wrote to schedules.json (and the file's time and size right after): a save of exactly the
        // same data while the file is still that one writes nothing — no disk flush, and .bak keeps the previous version.
        private string lastWrittenJson;
        private DateTime lastWrittenTime;
        private long lastWrittenLength;

        public DataManager()
            : this(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DataFileName))
        {
        }

        // 별도 경로를 주입할 수 있어 로컬 저장소를 독립적으로 검증할 수 있습니다.
        public DataManager(string dataDirectory, string legacyJsonPath)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory))
                throw new ArgumentException("데이터 저장 경로가 비어 있습니다.", nameof(dataDirectory));

            this.dataDirectory = Path.GetFullPath(dataDirectory);
            jsonPath = Path.Combine(this.dataDirectory, DataFileName);
            backupPath = jsonPath + ".bak";
            this.legacyJsonPath = string.IsNullOrWhiteSpace(legacyJsonPath)
                ? null
                : Path.GetFullPath(legacyJsonPath);
        }

        public string DataFilePath => jsonPath;
        public string BackupFilePath => backupPath;

        /// <summary>
        /// How long a data file another program has open (a backup or sync tool, a scanner, an editor) is waited for. Such a
        /// file is not damaged: loading then fails with DataStorageException — it is never moved aside and replaced by the
        /// older backup, which the next save would have written over the newer file. Checks shorten it.
        /// </summary>
        public TimeSpan BusyWait { get; set; } = TimeSpan.FromSeconds(2);

        public DataLoadResult LoadData()
        {
            try
            {
                Directory.CreateDirectory(dataDirectory);
                lastWrittenJson = null; // the next save writes (and rotates .bak) whatever the file holds now
                CleanUpStaleFiles();

                string migrationWarning = MigrateLegacyDataIfNeeded();

                if (!File.Exists(jsonPath))
                {
                    if (File.Exists(backupPath))
                        return RecoverFromBackup("기본 데이터 파일이 없어 백업에서 복구했습니다.");

                    return new DataLoadResult(new AppData(), migrationWarning);
                }

                AppData data;
                Exception primaryError;
                if (TryReadData(jsonPath, out data, out primaryError))
                    return new DataLoadResult(NormalizeData(data), migrationWarning);

                string quarantinedPrimary = TryQuarantine(jsonPath);

                if (File.Exists(backupPath))
                {
                    Exception backupError;
                    if (TryReadData(backupPath, out data, out backupError))
                    {
                        TryRestorePrimaryFromBackup();
                        string warning = "일정 데이터 손상을 감지해 백업에서 복구했습니다.";
                        if (!string.IsNullOrEmpty(quarantinedPrimary))
                            warning += Environment.NewLine + "손상 파일: " + quarantinedPrimary;

                        return new DataLoadResult(NormalizeData(data), warning);
                    }

                    string quarantinedBackup = TryQuarantine(backupPath);
                    return new DataLoadResult(
                        new AppData(),
                        BuildResetWarning(quarantinedPrimary, quarantinedBackup));
                }

                return new DataLoadResult(
                    new AppData(),
                    BuildResetWarning(quarantinedPrimary, null));
            }
            catch (DataStorageException)
            {
                throw;
            }
            catch (Exception ex) when (IsStorageException(ex))
            {
                throw new DataStorageException(
                    "일정 데이터를 불러올 수 없습니다. 저장 경로를 확인해 주세요.", ex);
            }
        }

        public void SaveData(AppData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            try
            {
                Directory.CreateDirectory(dataDirectory);
                WriteDataAtomically(NormalizeData(data));
            }
            catch (Exception ex) when (IsStorageException(ex))
            {
                throw new DataStorageException(
                    "일정 데이터를 저장할 수 없습니다. 디스크 공간과 저장 경로를 확인해 주세요.", ex);
            }
        }

        private string MigrateLegacyDataIfNeeded()
        {
            if (string.IsNullOrEmpty(legacyJsonPath) ||
                PathsEqual(legacyJsonPath, jsonPath) ||
                File.Exists(jsonPath) ||
                File.Exists(backupPath) ||
                !File.Exists(legacyJsonPath))
                return null;

            AppData legacyData;
            Exception legacyError;
            if (!TryReadData(legacyJsonPath, out legacyData, out legacyError))
            {
                return "기존 일정 파일을 읽을 수 없어 자동 이전하지 못했습니다."
                    + Environment.NewLine + "기존 파일: " + legacyJsonPath;
            }

            WriteDataAtomically(NormalizeData(legacyData));

            // 이전 작업이 실제로 읽을 수 있는 파일을 만들었는지 확인한 뒤에만
            // 구버전 파일을 삭제합니다. 검증에 실패하면 원본을 보존합니다.
            if (!TryReadData(jsonPath, out _, out _))
            {
                string quarantinedPrimary = TryQuarantine(jsonPath);
                string warning = "구버전 일정 파일을 새 위치에 저장했지만 저장 결과를 확인하지 못했습니다."
                    + Environment.NewLine + "구버전 파일은 보존됩니다: " + legacyJsonPath;

                if (!string.IsNullOrEmpty(quarantinedPrimary))
                    warning += Environment.NewLine + "검증에 실패한 새 파일: " + quarantinedPrimary;

                return warning;
            }

            if (!TryDeleteLegacyFile())
            {
                return "일정 데이터는 새 위치로 이전했지만 구버전 파일을 삭제하지 못했습니다."
                    + Environment.NewLine + "구버전 파일: " + legacyJsonPath;
            }

            return null;
        }

        private bool TryDeleteLegacyFile()
        {
            if (!File.Exists(legacyJsonPath)) return true;

            try
            {
                File.Delete(legacyJsonPath);
                return !File.Exists(legacyJsonPath);
            }
            catch (Exception ex) when (IsStorageException(ex))
            {
                return false;
            }
        }

        private DataLoadResult RecoverFromBackup(string warning)
        {
            AppData data;
            Exception backupError;
            if (TryReadData(backupPath, out data, out backupError))
            {
                TryRestorePrimaryFromBackup();
                return new DataLoadResult(NormalizeData(data), warning);
            }

            string quarantinedBackup = TryQuarantine(backupPath);
            return new DataLoadResult(new AppData(), BuildResetWarning(null, quarantinedBackup));
        }

        // False only when the content is no valid data (a damaged file: the caller moves it aside and uses the backup) or the
        // file is gone. A file in use by another program, or one that may not be read, throws DataStorageException instead.
        private bool TryReadData(string path, out AppData data, out Exception error)
        {
            data = null;
            string json;
            try
            {
                json = ReadWhenFree(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                error = ex; // gone since it was looked for: handled like a damaged one, as before
                return false;
            }

            try
            {
                data = JsonConvert.DeserializeObject<AppData>(json);
                if (data == null)
                    throw new JsonSerializationException("저장 파일에 유효한 데이터가 없습니다.");

                error = null;
                return true;
            }
            catch (Exception ex) when (IsStorageException(ex))
            {
                data = null;
                error = ex;
                return false;
            }
        }

        // Reads the file, waiting BusyWait while another program holds it (a sharing or lock violation) or access is denied;
        // still so after that, loading stops with DataStorageException and nothing is changed on disk.
        private string ReadWhenFree(string path)
        {
            DateTime giveUp = DateTime.UtcNow + BusyWait;
            while (true)
            {
                try
                {
                    return File.ReadAllText(path, Encoding.UTF8);
                }
                catch (Exception ex) when (IsBusy(ex))
                {
                    if (DateTime.UtcNow >= giveUp)
                        throw new DataStorageException(
                            "일정 파일을 열 수 없습니다(다른 프로그램이 사용 중일 수 있습니다). 잠시 후 다시 실행해 주세요."
                                + Environment.NewLine + "파일: " + path, ex);
                    System.Threading.Thread.Sleep(100);
                }
            }
        }

        // Not a damaged file: in use by another program, or not ours to read (a missing file is handled by the callers).
        private static bool IsBusy(Exception ex)
        {
            return ex is UnauthorizedAccessException ||
                   ex is SecurityException ||
                   ex is IOException && !(ex is FileNotFoundException) && !(ex is DirectoryNotFoundException);
        }

        private void WriteDataAtomically(AppData data)
        {
            string json = JsonConvert.SerializeObject(data, Formatting.Indented);
            if (lastWrittenJson != null && string.Equals(json, lastWrittenJson, StringComparison.Ordinal) && IsLastWrittenFile())
                return; // nothing changed since this manager wrote the file (a window move, a sync that found nothing new, …)

            lastWrittenJson = null;
            string tempPath = Path.Combine(
                dataDirectory,
                DataFileName + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(true);
                }

                if (File.Exists(jsonPath))
                    File.Replace(tempPath, jsonPath, backupPath, true);
                else
                    File.Move(tempPath, jsonPath);

                var written = new FileInfo(jsonPath);
                lastWrittenTime = written.LastWriteTimeUtc;
                lastWrittenLength = written.Length;
                lastWrittenJson = json;
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        // schedules.json is still the file this manager wrote last (not replaced, restored or deleted since).
        private bool IsLastWrittenFile()
        {
            try
            {
                var file = new FileInfo(jsonPath);
                return file.Exists && file.LastWriteTimeUtc == lastWrittenTime && file.Length == lastWrittenLength;
            }
            catch (Exception ex) when (IsStorageException(ex))
            {
                return false;
            }
        }

        // Left behind by a save or a restore cut short (the app killed mid-write): temp files over 10 minutes old go, and of
        // the quarantined broken files (.corrupt.*) the newest 5 stay. schedules.json and .bak are never touched.
        private void CleanUpStaleFiles()
        {
            try
            {
                DateTime stale = DateTime.UtcNow.AddMinutes(-10);
                foreach (string path in Directory.GetFiles(dataDirectory, DataFileName + ".*.tmp"))
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"\Aschedules\.json\.(restore\.)?[0-9a-f]{32}\.tmp\z")) continue;
                    if (File.GetLastWriteTimeUtc(path) < stale) TryDelete(path);
                }
                var corrupt = new List<KeyValuePair<string, string>>();
                foreach (string path in Directory.GetFiles(dataDirectory, "schedules*.corrupt.*"))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(path), @"\Aschedules(\.json)?\.corrupt\.(\d{17})\.[0-9a-f]{32}\.(json|bak)\z");
                    if (match.Success) corrupt.Add(new KeyValuePair<string, string>(match.Groups[2].Value, path));
                }
                foreach (var old in corrupt.OrderByDescending(c => c.Key, StringComparer.Ordinal).Skip(5)) TryDelete(old.Value);
            }
            catch (Exception ex) when (IsStorageException(ex))
            {
                // Only housekeeping: the data itself loads either way.
            }
        }

        private void TryRestorePrimaryFromBackup()
        {
            lastWrittenJson = null;
            string tempPath = Path.Combine(
                dataDirectory,
                DataFileName + ".restore." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                File.Copy(backupPath, tempPath, false);

                if (File.Exists(jsonPath))
                    File.Replace(tempPath, jsonPath, null, true);
                else
                    File.Move(tempPath, jsonPath);
            }
            catch (Exception ex) when (IsStorageException(ex))
            {
                // 백업 데이터는 이미 메모리에 복구했습니다. 파일 복원은 다음 저장 때 재시도됩니다.
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        private string TryQuarantine(string sourcePath)
        {
            if (!File.Exists(sourcePath)) return null;

            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            string extension = Path.GetExtension(sourcePath);
            string quarantinedPath = Path.Combine(
                dataDirectory,
                baseName + ".corrupt." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")
                    + "." + Guid.NewGuid().ToString("N") + extension);

            try
            {
                File.Move(sourcePath, quarantinedPath);
                return quarantinedPath;
            }
            catch (Exception ex) when (IsStorageException(ex))
            {
                return null;
            }
        }

        private static string BuildResetWarning(string quarantinedPrimary, string quarantinedBackup)
        {
            var preservedFiles = new List<string>();
            if (!string.IsNullOrEmpty(quarantinedPrimary)) preservedFiles.Add(quarantinedPrimary);
            if (!string.IsNullOrEmpty(quarantinedBackup)) preservedFiles.Add(quarantinedBackup);

            string message = "일정 데이터와 백업을 읽을 수 없어 빈 데이터로 시작합니다.";
            if (preservedFiles.Count > 0)
                message += Environment.NewLine + "손상 파일을 보존했습니다:"
                    + Environment.NewLine + string.Join(Environment.NewLine, preservedFiles);

            return message;
        }

        private static AppData NormalizeData(AppData data)
        {
            if (data == null) data = new AppData();
            if (data.WindowState == null) data.WindowState = new WindowStateData();
            if (data.WindowState.MonitorStates == null)
                data.WindowState.MonitorStates = new Dictionary<string, MonitorStateData>();
            if (data.Schedules == null) data.Schedules = new List<ScheduleItem>();
            if (data.Appearance == null) data.Appearance = new AppearanceSettings();
            if (data.Communication == null) data.Communication = new CommunicationSettings();
            if (data.Reminders == null) data.Reminders = new ReminderSettings();
            data.Reminders.DaysBefore = Math.Max(0, Math.Min(30, data.Reminders.DaysBefore));
            data.Reminders.Hour = Math.Max(0, Math.Min(23, data.Reminders.Hour));
            data.Reminders.Minute = Math.Max(0, Math.Min(59, data.Reminders.Minute));
            if (data.Music == null) data.Music = new MusicSettings();
            if (string.IsNullOrWhiteSpace(data.CharacterManifest)) data.CharacterManifest = CharacterCatalog.DefaultManifest;
            data.MiniDayCount = Math.Max(1, Math.Min(7, data.MiniDayCount));
            data.MiniFlipEffect = Math.Max(0, Math.Min(6, data.MiniFlipEffect)); // 1–6 = 효과 1–6, 0 = 애니메이션 없음
            data.MiniCharacterScale = Math.Max(50, Math.Min(300, data.MiniCharacterScale));
            if (data.MiniExtraCharacters == null) data.MiniExtraCharacters = new List<MiniCharacterSlot>();
            data.MiniExtraCharacters.RemoveAll(s => s == null || string.IsNullOrWhiteSpace(s.Manifest));
            if (data.MiniExtraCharacters.Count > 2) data.MiniExtraCharacters.RemoveRange(2, data.MiniExtraCharacters.Count - 2);
            foreach (var slot in data.MiniExtraCharacters) if (string.IsNullOrWhiteSpace(slot.Animation)) slot.Animation = "idle";
            foreach (var slot in data.MiniExtraCharacters) if (slot.Scale.HasValue) slot.Scale = Math.Max(50, Math.Min(300, slot.Scale.Value));
            data.MiniCharacterSide = string.Equals(data.MiniCharacterSide, "Right", StringComparison.OrdinalIgnoreCase) ? "Right" : "Left";
            data.MiniCharacterVertical = Math.Max(0, Math.Min(100, data.MiniCharacterVertical));
            data.MiniCharacterGap = Math.Max(-80, Math.Min(60, data.MiniCharacterGap));
            data.BringToFrontHotKey = HotKeyGesture.ParseOrDefault(data.BringToFrontHotKey).ToString();
            if (data.GoogleCalendar == null) data.GoogleCalendar = new GoogleCalendarSettings();
            if (data.GoogleCalendar.SyncedEventIds == null) data.GoogleCalendar.SyncedEventIds = new List<string>();
            data.GoogleCalendar.SyncedEventIds.RemoveAll(string.IsNullOrWhiteSpace);
            if (data.GoogleCalendar.OwnedEventIds == null) data.GoogleCalendar.OwnedEventIds = new List<string>();
            data.GoogleCalendar.OwnedEventIds.RemoveAll(string.IsNullOrWhiteSpace);
            if (data.GoogleCalendar.HiddenEvents == null) data.GoogleCalendar.HiddenEvents = new Dictionary<string, string>();
            foreach (string key in data.GoogleCalendar.HiddenEvents.Keys.Where(string.IsNullOrWhiteSpace).ToList()) data.GoogleCalendar.HiddenEvents.Remove(key);
            if (data.GoogleCalendar.SyncedPetIds == null) data.GoogleCalendar.SyncedPetIds = new List<string>();
            data.GoogleCalendar.SyncedPetIds.RemoveAll(id => !CharacterCatalog.IsPetId(id));
            if (data.GoogleCalendar.FailedPetFiles == null) data.GoogleCalendar.FailedPetFiles = new List<string>();
            data.GoogleCalendar.FailedPetFiles.RemoveAll(string.IsNullOrWhiteSpace);
            if (data.GoogleCalendar.RefusedPetDeletes == null) data.GoogleCalendar.RefusedPetDeletes = new List<string>();
            data.GoogleCalendar.RefusedPetDeletes.RemoveAll(id => !CharacterCatalog.IsPetId(id));
            // Dragged spots: the mini window's pets, and the TODO window's pets (👤) apart from them.
            data.MiniPetSpots = NormalizeSpots(data.MiniPetSpots);
            data.CompanionPetSpots = NormalizeSpots(data.CompanionPetSpots);
            data.CompanionCharacterSide = string.Equals(data.CompanionCharacterSide, "Right", StringComparison.OrdinalIgnoreCase) ? "Right" : "Left";
            data.CompanionCharacterVertical = Math.Max(0, Math.Min(100, data.CompanionCharacterVertical));
            data.CompanionCharacterGap = Math.Max(-80, Math.Min(60, data.CompanionCharacterGap));
            // Each character's last spot: keys are character ids ("DefaultPets/mochi-white", "Characters/codex","Pet/<folder>", "#2" for a second
            // copy); at most 100 kept.
            data.MiniPetSpotsByCharacter = NormalizeSpotMemory(data.MiniPetSpotsByCharacter, data);
            data.CompanionPetSpotsByCharacter = NormalizeSpotMemory(data.CompanionPetSpotsByCharacter, data);
            data.Music.Volume = ClampFinite(data.Music.Volume, 0, 1, 0.5);
            data.Music.VolumeBeforeMute = data.Music.VolumeBeforeMute > 0.001 ? ClampFinite(data.Music.VolumeBeforeMute, 0.01, 1, 0.5) : 0.5;
            if (data.Music.Playlists == null) data.Music.Playlists = new List<MusicPlaylist>();
            data.Music.Playlists.RemoveAll(p => p == null);
            var playlistIds = new HashSet<Guid>();
            foreach (var playlist in data.Music.Playlists)
            {
                if (playlist.Id == Guid.Empty || !playlistIds.Add(playlist.Id))
                {
                    playlist.Id = Guid.NewGuid();
                    playlistIds.Add(playlist.Id);
                }
                if (string.IsNullOrWhiteSpace(playlist.Name)) playlist.Name = "내 플레이리스트";
                if (playlist.Tracks == null) playlist.Tracks = new List<MusicTrack>();
                playlist.Tracks.RemoveAll(t => t == null || string.IsNullOrWhiteSpace(t.Source));
            }

            MigrateLegacyLightAppearance(data.Appearance);

            data.Schedules.RemoveAll(item => item == null);

            // 구버전 파일에는 ID가 없고, 손상된 파일에는 중복 ID가 있을 수 있습니다.
            // 로드·저장 시점에 모두 보정해 이후 수정/삭제 대상을 안정적으로 식별합니다.
            var usedScheduleIds = new HashSet<Guid>();
            foreach (ScheduleItem item in data.Schedules)
            {
                // A date written any other way (an old or hand-edited file: "2026-9-3") is stored as yyyy-MM-dd, which the
                // mini calendar and the Google sync read; one that is not a date at all stays as it is (the list shows 날짜 확인).
                if (!string.IsNullOrWhiteSpace(item.Period) &&
                    !DateTime.TryParseExact(item.Period, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
                    DateTime.TryParse(item.Period, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime loose))
                    item.Period = loose.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                // 여러 날 일정의 끝: a later day as yyyy-MM-dd (at most ScheduleItem.MaxRangeDays on), else none (one day).
                item.EndPeriod = ScheduleItem.NormalizeEndPeriod(item.Period, item.EndPeriod);
                item.Time =FeatureRules.TryScheduleTime(item.Time, out string time) ? time : null;
                if (!FeatureRules.IsColor(item.Color)) item.Color = null;
                if (item.ReminderReceipts == null) item.ReminderReceipts = new Dictionary<string, string>();
                if (item.Id != Guid.Empty && usedScheduleIds.Add(item.Id))
                    continue;

                do
                {
                    item.Id = Guid.NewGuid();
                }
                while (!usedScheduleIds.Add(item.Id));
            }

            if (!IsFinite(data.WindowState.Left)) data.WindowState.Left = 0;
            if (!IsFinite(data.WindowState.Top)) data.WindowState.Top = 0;
            if (!IsFinite(data.WindowState.Width) || data.WindowState.Width < 0)
                data.WindowState.Width = 0;
            if (!IsFinite(data.WindowState.Height) || data.WindowState.Height < 0)
                data.WindowState.Height = 0;

            var invalidMonitorIds = new List<string>();
            foreach (KeyValuePair<string, MonitorStateData> pair in data.WindowState.MonitorStates)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null)
                {
                    invalidMonitorIds.Add(pair.Key);
                    continue;
                }

                MonitorStateData monitorState = pair.Value;
                if (!IsFinite(monitorState.Left)) monitorState.Left = 0;
                if (!IsFinite(monitorState.Top)) monitorState.Top = 0;
                if (!IsFinite(monitorState.Width) || monitorState.Width < 0)
                    monitorState.Width = 0;
                if (!IsFinite(monitorState.Height) || monitorState.Height < 0)
                    monitorState.Height = 0;
            }

            foreach (string monitorId in invalidMonitorIds)
            {
                if (monitorId != null)
                    data.WindowState.MonitorStates.Remove(monitorId);
            }

            var defaults = new AppearanceSettings();
            if (!FeatureRules.IsColor(data.Appearance.TodayColor)) data.Appearance.TodayColor = defaults.TodayColor;
            if (!FeatureRules.IsColor(data.Appearance.FutureColor)) data.Appearance.FutureColor = defaults.FutureColor;
            if (!FeatureRules.IsColor(data.Appearance.PastColor)) data.Appearance.PastColor = defaults.PastColor;
            data.Appearance.Opacity = ClampFinite(data.Appearance.Opacity, 0.3, 1.0, defaults.Opacity);
            data.Appearance.TitleFontSize = ClampFinite(
                data.Appearance.TitleFontSize, 10, 24, defaults.TitleFontSize);
            data.Appearance.DDayFontSize = ClampFinite(
                data.Appearance.DDayFontSize, 10, 24, defaults.DDayFontSize);

            if (string.IsNullOrWhiteSpace(data.Appearance.ThemePreset))
                data.Appearance.ThemePreset = defaults.ThemePreset;
            AppearanceSettings presetDefaults = defaults;
            if (AppearanceSettings.Presets.ContainsKey(data.Appearance.ThemePreset))
                presetDefaults = AppearanceSettings.Presets[data.Appearance.ThemePreset];
            if (string.IsNullOrWhiteSpace(data.Appearance.TopBarColor))
                data.Appearance.TopBarColor = defaults.TopBarColor;
            if (string.IsNullOrWhiteSpace(data.Appearance.BackgroundColor))
                data.Appearance.BackgroundColor = defaults.BackgroundColor;
            if (string.IsNullOrWhiteSpace(data.Appearance.CardColor))
                data.Appearance.CardColor = defaults.CardColor;
            if (string.IsNullOrWhiteSpace(data.Appearance.CardBorderColor))
                data.Appearance.CardBorderColor = defaults.CardBorderColor;
            if (string.IsNullOrWhiteSpace(data.Appearance.BottomBarColor))
                data.Appearance.BottomBarColor = defaults.BottomBarColor;
            if (string.IsNullOrWhiteSpace(data.Appearance.TextColor))
                data.Appearance.TextColor = defaults.TextColor;
            if (string.IsNullOrWhiteSpace(data.Appearance.SubTextColor))
                data.Appearance.SubTextColor = defaults.SubTextColor;
            if (string.IsNullOrWhiteSpace(data.Appearance.BorderColor))
                data.Appearance.BorderColor = defaults.BorderColor;
            if (string.IsNullOrWhiteSpace(data.Appearance.AccentColor))
                data.Appearance.AccentColor = presetDefaults.AccentColor;
            if (string.IsNullOrWhiteSpace(data.Appearance.ControlHoverColor))
                data.Appearance.ControlHoverColor = presetDefaults.ControlHoverColor;

            // 새 테마별 강조색이 없던 버전에서 저장된 데이터는
            // 기본 Light 색상이 들어올 수 있으므로 선택된 프리셋에 맞춰 보완합니다.
            if (!string.Equals(data.Appearance.ThemePreset, "Light", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(data.Appearance.AccentColor, defaults.AccentColor, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(data.Appearance.ControlHoverColor, defaults.ControlHoverColor, StringComparison.OrdinalIgnoreCase))
            {
                data.Appearance.AccentColor = presetDefaults.AccentColor;
                data.Appearance.ControlHoverColor = presetDefaults.ControlHoverColor;
            }

            return data;
        }

        private static void MigrateLegacyLightAppearance(AppearanceSettings appearance)
        {
            if (!string.Equals(appearance.ThemePreset, "Light", StringComparison.OrdinalIgnoreCase))
                return;

            // 저장된 값이 앱의 이전 기본값과 완전히 같을 때만 새 디자인으로 갱신합니다.
            // 사용자가 직접 바꾼 색상은 그대로 유지합니다.
            bool isLegacyPalette =
                string.Equals(appearance.TopBarColor, "#FFD9D9D9", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.BackgroundColor, "#FFFFFF", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.CardColor, "#FFFFFF", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.CardBorderColor, "#DDDDDD", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.BottomBarColor, "#FFE0E0E0", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.TextColor, "#000000", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.SubTextColor, "#808080", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.BorderColor, "#808080", StringComparison.OrdinalIgnoreCase);

            bool isPreviousLightPalette =
                string.Equals(appearance.TopBarColor, "#FFE8EBFF", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.BackgroundColor, "#FFF7F8FC", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.CardColor, "#FFFFFFFF", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.CardBorderColor, "#FFE4E8F0", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.BottomBarColor, "#FFF0F3F8", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.TextColor, "#FF172033", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.SubTextColor, "#FF667085", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.BorderColor, "#FFD1D8E5", StringComparison.OrdinalIgnoreCase);

            bool isPreviousNeutralLightPalette =
                string.Equals(appearance.TopBarColor, "#FFF8FAFC", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.BackgroundColor, "#FFF4F6F8", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.CardColor, "#FFFFFFFF", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.CardBorderColor, "#FFE2E8F0", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.BottomBarColor, "#FFF1F4F7", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.TextColor, "#FF1F2937", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.SubTextColor, "#FF64748B", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(appearance.BorderColor, "#FFCBD5E1", StringComparison.OrdinalIgnoreCase);

            if (!isLegacyPalette && !isPreviousLightPalette && !isPreviousNeutralLightPalette)
                return;

            appearance.CopyColorsFrom(AppearanceSettings.Presets["Light"]);
        }

        private static void NormalizeSpot(MiniPetSpot spot)
        {
            if (spot.Left.HasValue && (double.IsNaN(spot.Left.Value) || double.IsInfinity(spot.Left.Value))) spot.Left = null;
            if (spot.Top.HasValue && (double.IsNaN(spot.Top.Value) || double.IsInfinity(spot.Top.Value))) spot.Top = null;
            if (spot.Left.HasValue) spot.Left = Math.Max(-400, Math.Min(500, spot.Left.Value));
            if (spot.Top.HasValue) spot.Top = Math.Max(-200, Math.Min(300, spot.Top.Value));
            if (spot.EdgeX != "L" && spot.EdgeX != "R") spot.EdgeX = null;
            if (spot.EdgeY != "T" && spot.EdgeY != "B") spot.EdgeY = null;
            if (string.IsNullOrWhiteSpace(spot.Key) || spot.Key.Length > 200) spot.Key = null; // whose spot: unknown → the slot's character now
            spot.OffsetX =double.IsNaN(spot.OffsetX) || double.IsInfinity(spot.OffsetX) ? 0 : Math.Max(-2000, Math.Min(2000, spot.OffsetX));
            spot.OffsetY = double.IsNaN(spot.OffsetY) || double.IsInfinity(spot.OffsetY) ? 0 : Math.Max(-2000, Math.Min(2000, spot.OffsetY));
        }

        // The spots of the (up to 3) pets in slot order. A missing entry becomes "not placed" so the pets after it keep theirs.
        private static List<MiniPetSpot> NormalizeSpots(List<MiniPetSpot> spots)
        {
            if (spots == null) return new List<MiniPetSpot>();
            if (spots.Count > 3) spots.RemoveRange(3, spots.Count - 3);
            for (int i = 0; i < spots.Count; i++)
            {
                if (spots[i] == null) spots[i] = new MiniPetSpot();
                NormalizeSpot(spots[i]);
            }
            return spots;
        }

        private static Dictionary<string, MiniPetSpot> NormalizeSpotMemory(Dictionary<string, MiniPetSpot> memory, AppData data)
        {
            if (memory == null) return new Dictionary<string, MiniPetSpot>();
            // "Pet/abc#2" (a second copy of a character) belongs to "Pet/abc".
            string Character(string key) { int copy = key.IndexOf('#'); return copy < 0 ? key : key.Substring(0, copy); }
            foreach (string key in memory.Keys.ToList())
            {
                var spot = memory[key];
                if (string.IsNullOrWhiteSpace(key) || key.Length > 200 || spot == null || CharacterCatalog.IsDeletedKey(Character(key)))
                {
                    memory.Remove(key); // also a character deleted in 캐릭터 선택
                    continue;
                }
                NormalizeSpot(spot);
                if (spot.EdgeX == null && spot.Left == null && spot.EdgeY == null && spot.Top == null) memory.Remove(key);
            }
            if (memory.Count > 100)
            {
                // Over 100: the oldest entries go first (the file keeps them in the order they came), never those of the
                // characters in the slots now.
                var inUse = new HashSet<string>(new[] { data.CharacterManifest }.Concat(data.MiniExtraCharacters.Select(s => s.Manifest))
                    .Select(CharacterCatalog.SelectionKey), StringComparer.OrdinalIgnoreCase);
                foreach (string key in memory.Keys.Where(k => !inUse.Contains(Character(k))).Take(memory.Count - 100).ToList())
                    memory.Remove(key);
            }
            return memory;
        }

        private static double ClampFinite(double value, double minimum, double maximum, double defaultValue)
        {
            if (!IsFinite(value)) return defaultValue;
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsStorageException(Exception ex)
        {
            return ex is IOException ||
                   ex is UnauthorizedAccessException ||
                   ex is SecurityException ||
                   ex is JsonException ||
                   ex is NotSupportedException;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (IsStorageException(ex))
            {
            }
        }
    }
}
