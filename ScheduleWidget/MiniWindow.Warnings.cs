using System.Linq;
using System.Windows;

namespace ScheduleWidget
{
    public partial class MiniWindow
    {
        private string reminderWarning, scheduleSaveWarning, googleSaveWarning;
        public void SetReminderWarning(string message) { reminderWarning = message; UpdateSettingsWarnings(); }
        internal void SetScheduleSaveWarning(string message) { scheduleSaveWarning = message; UpdateSettingsWarnings(); }
        internal void SetGoogleSaveWarning(string message) { googleSaveWarning = message; UpdateSettingsWarnings(); }

        private void UpdateSettingsWarnings()
        {
            var warnings = new[] { googleSaveWarning, scheduleSaveWarning, reminderWarning }.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            HeaderSettingsButton.ToolTip = warnings.Length == 0 ? "설정" : string.Join("\n", warnings);
            SettingsWarningBadge.Visibility = warnings.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
