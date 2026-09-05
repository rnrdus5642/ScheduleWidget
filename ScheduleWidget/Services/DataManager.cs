using System;
using System.Collections.Generic;
using System.IO;
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

        public DataLoadResult LoadData()
        {
            try
            {
                Directory.CreateDirectory(dataDirectory);

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

        private static bool TryReadData(string path, out AppData data, out Exception error)
        {
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
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

        private void WriteDataAtomically(AppData data)
        {
            string json = JsonConvert.SerializeObject(data, Formatting.Indented);
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
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        private void TryRestorePrimaryFromBackup()
        {
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
            if (data.Schedules == null) data.Schedules = new List<ScheduleItem>();
            if (data.Appearance == null) data.Appearance = new AppearanceSettings();

            data.Schedules.RemoveAll(item => item == null);

            if (!IsFinite(data.WindowState.Left)) data.WindowState.Left = 0;
            if (!IsFinite(data.WindowState.Top)) data.WindowState.Top = 0;
            if (!IsFinite(data.WindowState.Width) || data.WindowState.Width < 0)
                data.WindowState.Width = 0;
            if (!IsFinite(data.WindowState.Height) || data.WindowState.Height < 0)
                data.WindowState.Height = 0;

            var defaults = new AppearanceSettings();
            data.Appearance.Opacity = ClampFinite(data.Appearance.Opacity, 0.3, 1.0, defaults.Opacity);
            data.Appearance.TitleFontSize = ClampFinite(
                data.Appearance.TitleFontSize, 10, 24, defaults.TitleFontSize);
            data.Appearance.DDayFontSize = ClampFinite(
                data.Appearance.DDayFontSize, 10, 24, defaults.DDayFontSize);

            if (string.IsNullOrWhiteSpace(data.Appearance.ThemePreset))
                data.Appearance.ThemePreset = defaults.ThemePreset;
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

            return data;
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
