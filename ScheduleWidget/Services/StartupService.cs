using System;
using System.IO;
using System.Reflection;
using System.Security;
using Microsoft.Win32;

namespace ScheduleWidget
{
    public sealed class StartupService
    {
        private const string AppName = "ScheduleWidget";
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        public bool TryEnableStartup(out string errorMessage)
        {
            return TrySetStartup(true, out errorMessage);
        }

        public bool TryDisableStartup(out string errorMessage)
        {
            return TrySetStartup(false, out errorMessage);
        }

        public bool IsStartupEnabled(out bool enabled, out string errorMessage)
        {
            enabled = false;
            errorMessage = null;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null)
                        return true;

                    object value = key.GetValue(
                        AppName,
                        null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames);
                    enabled = value is string && !string.IsNullOrWhiteSpace((string)value);
                    return true;
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                errorMessage = BuildErrorMessage("Windows 자동 시작 상태를 확인할 권한이 없습니다.", ex);
            }
            catch (SecurityException ex)
            {
                errorMessage = BuildErrorMessage("Windows 자동 시작 상태 확인이 보안 정책으로 차단되었습니다.", ex);
            }
            catch (IOException ex)
            {
                errorMessage = BuildErrorMessage("Windows 자동 시작 레지스트리에 접근하지 못했습니다.", ex);
            }
            catch (Exception ex)
            {
                errorMessage = BuildErrorMessage("Windows 자동 시작 상태를 확인하지 못했습니다.", ex);
            }

            return false;
        }

        private bool TrySetStartup(bool enabled, out string errorMessage)
        {
            errorMessage = null;

            string exePath = GetExecutablePath();
            if (enabled && (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)))
            {
                errorMessage = "실행 파일 경로를 확인할 수 없어 Windows 자동 시작을 변경하지 못했습니다.";
                return false;
            }

            try
            {
                if (enabled)
                {
                    string startupCommand = "\"" + exePath + "\"";
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true))
                    {
                        if (key == null)
                        {
                            errorMessage = "Windows 자동 시작 레지스트리 키를 열 수 없습니다.";
                            return false;
                        }

                        key.SetValue(AppName, startupCommand, RegistryValueKind.String);
                        key.Flush();

                        object savedValue = key.GetValue(
                            AppName,
                            null,
                            RegistryValueOptions.DoNotExpandEnvironmentNames);
                        if (!(savedValue is string) ||
                            !string.Equals((string)savedValue, startupCommand, StringComparison.Ordinal))
                        {
                            errorMessage = "Windows 자동 시작 등록 결과를 확인할 수 없습니다.";
                            return false;
                        }
                    }
                }
                else
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                    {
                        if (key != null)
                        {
                            key.DeleteValue(AppName, false);
                            key.Flush();
                        }
                    }

                    bool remainsEnabled;
                    string verifyError;
                    if (!IsStartupEnabled(out remainsEnabled, out verifyError))
                    {
                        errorMessage = verifyError;
                        return false;
                    }

                    if (remainsEnabled)
                    {
                        errorMessage = "Windows 자동 시작 해제 결과를 확인할 수 없습니다.";
                        return false;
                    }
                }

                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                errorMessage = BuildErrorMessage(
                    enabled ? "Windows 자동 시작을 등록할 권한이 없습니다." : "Windows 자동 시작을 해제할 권한이 없습니다.",
                    ex);
            }
            catch (SecurityException ex)
            {
                errorMessage = BuildErrorMessage(
                    enabled ? "Windows 자동 시작 등록이 보안 정책으로 차단되었습니다." : "Windows 자동 시작 해제가 보안 정책으로 차단되었습니다.",
                    ex);
            }
            catch (IOException ex)
            {
                errorMessage = BuildErrorMessage("Windows 자동 시작 레지스트리에 접근하지 못했습니다.", ex);
            }
            catch (ArgumentException ex)
            {
                errorMessage = BuildErrorMessage("Windows 자동 시작 설정 값이 올바르지 않습니다.", ex);
            }
            catch (Exception ex)
            {
                // 자동 시작은 부가 기능이므로 변경 실패 때문에 앱 전체가 종료되면 안 됩니다.
                errorMessage = BuildErrorMessage("Windows 자동 시작 설정 중 알 수 없는 오류가 발생했습니다.", ex);
            }

            return false;
        }

        private static string GetExecutablePath()
        {
            Assembly entryAssembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            return entryAssembly == null ? null : entryAssembly.Location;
        }

        private static string BuildErrorMessage(string description, Exception exception)
        {
            if (string.IsNullOrWhiteSpace(exception?.Message))
                return description;

            return description + Environment.NewLine + exception.Message;
        }
    }
}
