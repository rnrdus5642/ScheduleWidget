using Microsoft.Win32;

namespace ScheduleWidget
{
    public sealed class StartupService
    {
        private const string AppName = "ScheduleWidget";

        public void EnableStartup()
        {
            string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                key?.SetValue(AppName, "\"" + exePath + "\"");
            }
        }
    }
}
