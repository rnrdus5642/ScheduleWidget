using System;
using System.Windows.Media;

namespace ScheduleWidget
{
    public partial class MiniWindow
    {
        private AppearanceSettings scheduleAppearance = new AppearanceSettings();

        // Calendar opacity and the full agenda's typography/colors follow the current settings preview.
        public void ApplyAppearance(AppearanceSettings appearance, bool refresh = true)
        {
            appearance = appearance ?? new AppearanceSettings();
            bool themeChanged = !Resources.Contains("MiniPaperBrush") || ThemeName != appearance.ThemePreset;
            if (themeChanged) ApplyTheme(appearance.ThemePreset, refresh: false);
            scheduleAppearance = appearance;
            MiniRoot.Opacity = FiniteRange(appearance.Opacity, 0.3, 1, 1);
            if (flipWindow != null) flipWindow.Opacity = MiniRoot.Opacity;
            Resources["AgendaTitleFontSize"] = FiniteRange(appearance.TitleFontSize, 10, 24, 14);
            Resources["AgendaDDayFontSize"] = FiniteRange(appearance.DDayFontSize, 10, 24, 13);
            var background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(AgendaColor(appearance.BackgroundColor, theme.Paper)));
            background.Freeze();
            Resources["AgendaBackgroundBrush"] = background;
            allSchedulesSignature = null;
            if (refresh && themeChanged) Refresh();
            else if (refresh) RefreshAllSchedules();
        }

        private static double FiniteRange(double value, double min, double max, double fallback) =>
            double.IsNaN(value) || double.IsInfinity(value) ? fallback : Math.Max(min, Math.Min(max, value));

        private static string AgendaColor(string color, string fallback) => FeatureRules.IsColor(color) ? color : fallback;

        private string AgendaStatusInk(ScheduleItem item)
        {
            if (FeatureRules.IsColor(item.Color)) return FeatureRules.ReadableText(item.Color);
            if (item.IsCompleted || !item.StartDate.HasValue) return AgendaColor(scheduleAppearance.TextColor, theme.Ink);
            string color = item.RemainingDays == 0 ? scheduleAppearance.TodayColor
                : item.RemainingDays > 0 ? scheduleAppearance.FutureColor : scheduleAppearance.PastColor;
            return AgendaColor(color, theme.Ink);
        }
    }
}
