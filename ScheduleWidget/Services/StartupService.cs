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
            errorMessage = null;

            string exePath = GetExecutablePath();
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                errorMessage = "실행 파일 경로를 확인할 수 없어 Windows 자동 시작을 등록하지 못했습니다.";
                return false;
            }

            string startupCommand = "\"" + exePath + "\"";

            try
            {
                // Run 키가 없는 환경에서도 등록할 수 있도록 OpenSubKey 대신 생성합니다.
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

                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                errorMessage = BuildErrorMessage("Windows 자동 시작을 등록할 권한이 없습니다.", ex);
            }
            catch (SecurityException ex)
            {
                errorMessage = BuildErrorMessage("Windows 자동 시작 등록이 보안 정책으로 차단되었습니다.", ex);
            }
            catch (IOException ex)
            {
                errorMessage = BuildErrorMessage("Windows 자동 시작 레지스트리에 접근하지 못했습니다.", ex);
            }
            catch (ArgumentException ex)
            {
                errorMessage = BuildErrorMessage("Windows 자동 시작 등록 값이 올바르지 않습니다.", ex);
            }
            catch (Exception ex)
            {
                // 자동 시작은 부가 기능이므로 등록 실패 때문에 앱 전체가 종료되면 안 됩니다.
                errorMessage = BuildErrorMessage("Windows 자동 시작 등록 중 알 수 없는 오류가 발생했습니다.", ex);
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
