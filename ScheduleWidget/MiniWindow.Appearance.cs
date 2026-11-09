using System;
using System.Windows.Media;

namespace ScheduleWidget
{
    public partial class MiniWindow
    {
        private AppearanceSettings scheduleAppearance = new AppearanceSettings();

        // Compact titles/statuses and month titles have independent saved sizes and live previews.
        public void ApplyAppearance(AppearanceSettings appearance, bool refresh = true)
        {
            appearance = appearance ?? new AppearanceSettings();
            bool themeChanged = !Resources.Contains("MiniPaperBrush") || ThemeName != appearance.ThemePreset;
            if (themeChanged) ApplyTheme(appearance.ThemePreset, refresh: false);
            scheduleAppearance = appearance;
            MiniRoot.Opacity = FiniteRange(appearance.Opacity, 0.3, 1, 1);
            // Popups have a separate visual tree: they do not inherit the calendar's opacity.
            DayBubble.Opacity = MiniRoot.Opacity; // schedule details, new schedules, and edits
            BlockBubble.Opacity = MiniRoot.Opacity;
            if (flipWindow != null) flipWindow.Opacity = MiniRoot.Opacity;
            double compactTitle = FiniteRange(appearance.CompactTitleFontSize, 10, 24, 11);
            Resources["CompactTitleFontSize"] = compactTitle;
            Resources["CompactDDayFontSize"] = FiniteRange(appearance.CompactDDayFontSize, 10, 24, 10);
            Resources["CompactTitleLineHeight"] = Math.Ceiling(compactTitle * 1.25);
            Resources["CompactTitleMaxHeight"] = Math.Ceiling(compactTitle * 1.25) * 2;
            Resources["MonthTitleFontSize"] = FiniteRange(appearance.EffectiveMonthTitleFontSize, 10, 24, 14);
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
            else if (refresh)
            {
                UpdateWeekRangeLayout();
                RefreshMonthCalendar();
            }
        }

        private static double FiniteRange(double value, double min, double max, double fallback) =>
            double.IsNaN(value) || double.IsInfinity(value) ? fallback : Math.Max(min, Math.Min(max, value));

        private static string AppearanceColor(string color, string fallback) => FeatureRules.IsColor(color) ? color : fallback;
    }
}
