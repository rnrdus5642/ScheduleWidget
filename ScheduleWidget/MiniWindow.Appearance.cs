using System;
using System.Windows.Media;

namespace ScheduleWidget
{
    public partial class MiniWindow
    {
        private AppearanceSettings scheduleAppearance = new AppearanceSettings();

        // Month cells and day details share the saved appearance and live settings preview.
        public void ApplyAppearance(AppearanceSettings appearance, bool refresh = true)
        {
            appearance = appearance ?? new AppearanceSettings();
            bool themeChanged = !Resources.Contains("MiniPaperBrush") || ThemeName != appearance.ThemePreset;
            if (themeChanged) ApplyTheme(appearance.ThemePreset, refresh: false);
            scheduleAppearance = appearance;
            MiniRoot.Opacity = FiniteRange(appearance.Opacity, 0.3, 1, 1);
            if (flipWindow != null) flipWindow.Opacity = MiniRoot.Opacity;
            Resources["TitleFontSize"] = FiniteRange(appearance.TitleFontSize, 10, 24, 14);
            Resources["DDayFontSize"] = FiniteRange(appearance.DDayFontSize, 10, 24, 13);
            void Brush(string key, string color, string fallback)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(AppearanceColor(color, fallback)));
                brush.Freeze();
                Resources[key] = brush;
            }
            Brush("BackgroundBrush", appearance.BackgroundColor, theme.Paper);
            Brush("CardBrush", appearance.CardColor, theme.Day);
            Brush("TextBrush", appearance.TextColor, theme.Ink);
            Brush("SubTextBrush", appearance.TextColor, theme.Ink);
            Brush("TodayBrush", appearance.TodayColor, theme.Ink);
            Brush("FutureBrush", appearance.FutureColor, theme.Ink);
            Brush("PastBrush", appearance.PastColor, theme.Ink);
            if (refresh && themeChanged) Refresh();
            else if (refresh) RefreshMonthCalendar();
        }

        private static double FiniteRange(double value, double min, double max, double fallback) =>
            double.IsNaN(value) || double.IsInfinity(value) ? fallback : Math.Max(min, Math.Min(max, value));

        private static string AppearanceColor(string color, string fallback) => FeatureRules.IsColor(color) ? color : fallback;
    }
}
