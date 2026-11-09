using System;
using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScheduleWidget
{
    public partial class MiniWindow
    {
        private bool appearancePreviewOnly;

        internal static MiniWindow CreateAppearancePreviewWindow()
        {
            // This never joins a real data store or runs Loaded. Its calendar/templates are rendered offscreen only.
            var sample = new AppData { StartupEnabled = false, BringToFrontHotKeyEnabled = false,
                MiniCharacterVisible = false, MiniFlipEffect = 0,
                MiniBoard = new MonitorStateData { Left = 30, Top = 30, Width = 720, Height = 240 } };
            DateTime today = DateTime.Today;
            sample.Schedules.Add(new ScheduleItem { Title = "보고서 제출", Period = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Time = "09:00" });
            sample.Schedules.Add(new ScheduleItem { Title = "치과 예약", Period = today.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Time = "14:00" });
            sample.Schedules.Add(new ScheduleItem { Title = "자료 정리", Period = today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
            sample.Schedules.Add(new ScheduleItem { Title = "휴가", Period = today.AddDays(3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                EndPeriod = today.AddDays(5).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
            return new MiniWindow(sample, () => true, () => { }) { appearancePreviewOnly = true, ShowActivated = false, IsHitTestVisible = false };
        }

        internal BitmapSource RenderAppearancePreview(string section, AppearanceSettings appearance, int days, bool showDDay)
        {
            if (!appearancePreviewOnly || IsVisible || new WindowInteropHelper(this).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Appearance previews require an unshown sample window.");
            bool month = section == "Month";
            data.Appearance = appearance;
            data.MiniDayCount = Math.Max(1, Math.Min(7, days));
            data.MiniBlockDDayVisible = showDDay;
            data.MiniMonthView = month;
            weekStart = DateTime.Today;
            displayedMonth = MonthStart(DateTime.Today);
            changingCalendarMode = true;
            try
            {
                ApplyCalendarModeVisibility(month);
                ApplyAppearance(appearance, refresh: false);
                int leading = ((int)displayedMonth.DayOfWeek + 6) % 7;
                int weeks = (leading + DateTime.DaysInMonth(displayedMonth.Year, displayedMonth.Month) + 6) / 7;
                double row = Math.Ceiling(FiniteRange(appearance.EffectiveMonthTitleFontSize, 10, 24, 14) * 1.3) + 5;
                ResizeCalendarBoard(new MonitorStateData { Left = 30, Top = 30, Width = 720, Height = month ? weeks * (row + 51) + 120 : 240 }, updateLayout: false);
                Refresh();
                var root = (FrameworkElement)Content;
                root.Measure(new Size(Width, Height));
                root.Arrange(new Rect(0, 0, Width, Height));
                root.UpdateLayout();
            }
            finally { changingCalendarMode = false; }
            BitmapSource calendar = PreviewBitmap(WeekCalendar);
            BitmapSource detail = null;
            if (section == "Details" || section == "Common")
            {
                PrepareDayDetails(DateTime.Today);
                // Show the actual entry controls in Common so the shared opacity includes the editor.
                if (section == "Common")
                {
                    DayPopupTitle.Text = "새 일정";
                    DaySchedulesScroll.Visibility = MiniScheduleEmpty.Visibility = NewMiniScheduleButton.Visibility = Visibility.Collapsed;
                    MiniEditorPanel.Visibility = Visibility.Visible;
                    MiniTitleInput.Text = "새 일정 입력";
                }
                else DaySchedulesScroll.Visibility = NewMiniScheduleButton.Visibility = Visibility.Visible;
                DaySchedulesScroll.MaxHeight = double.PositiveInfinity;
                DayBubble.Measure(new Size(DayBubble.Width, double.PositiveInfinity));
                DayBubble.Arrange(new Rect(new Point(), DayBubble.DesiredSize));
                DayBubble.UpdateLayout();
                // Draw opacity once below, independently of VisualBrush's treatment of the root visual.
                double opacity = DayBubble.Opacity;
                DayBubble.Opacity = 1;
                try { detail = PreviewBitmap(DayBubble); }
                finally { DayBubble.Opacity = opacity; }
            }

            double width = calendar.Width + 40, height = calendar.Height + 40;
            Rect calendarRect = new Rect(20, 20, calendar.Width, calendar.Height), detailRect = Rect.Empty;
            if (detail != null)
            {
                if (section == "Common")
                {
                    detailRect = new Rect(width - detail.Width - 20, 50, detail.Width, detail.Height);
                    height = Math.Max(height, detailRect.Bottom + 20);
                }
                else
                {
                    width = detail.Width + 40;
                    height = detail.Height + 40;
                    detailRect = new Rect(20, 20, detail.Width, detail.Height);
                }
            }
            var canvas = new DrawingVisual();
            using (DrawingContext drawing = canvas.RenderOpen())
            {
                // A stable desktop backdrop makes opacity changes visible instead of blending with the settings theme.
                var desktop = new LinearGradientBrush(Color.FromRgb(221, 230, 241), Color.FromRgb(171, 192, 215), 35);
                drawing.DrawRoundedRectangle(desktop, null, new Rect(0, 0, width, height), 12, 12);
                drawing.PushOpacity(FiniteRange(appearance.Opacity, .3, 1, 1));
                if (section != "Details") drawing.DrawImage(calendar, calendarRect);
                if (detail != null) drawing.DrawImage(detail, detailRect);
                drawing.Pop();
            }
            var picture = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
            picture.Render(canvas);
            picture.Freeze();
            return picture;
        }

        private static BitmapSource PreviewBitmap(FrameworkElement element)
        {
            var size = new Size(Math.Max(1, element.ActualWidth), Math.Max(1, element.ActualHeight));
            var visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen())
            {
                var brush = new VisualBrush(element) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size), Stretch = Stretch.Fill };
                drawing.DrawRectangle(brush, null, new Rect(size));
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
