using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static MiniWindow.CalendarRangeBar[] RangeBars(MiniWindow mini, string name) =>
            Control<ItemsControl>(mini, name).Items.Cast<MiniWindow.CalendarRangeBar>().ToArray();

        private static void CalendarRangeBars()
        {
            var data = new AppData { MiniCharacterVisible = false, MiniPlayerVisible = false };
            var longRange = new ScheduleItem { Title = "Across weeks", Period = "2026-09-27", EndPeriod = "2026-10-07", Color = "#336699" };
            var overlap = new ScheduleItem { Title = "Overlapping", Period = "2026-09-29", EndPeriod = "2026-10-02" };
            var disjoint = new ScheduleItem { Title = "After overlap", Period = "2026-10-03", EndPeriod = "2026-10-04" };
            data.Schedules.AddRange(new[] { longRange, overlap, disjoint });
            var mini = new MiniWindow(data, () => true, () => { });
            SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1040)));
            try
            {
                mini.SetMonthViewOpen(true); mini.ShowRangeFrom(new DateTime(2026, 10, 1)); LayoutCalendarMode(mini);
                var bars = RangeBars(mini, "MonthRanges");
                var longBars = bars.Where(b => b.Item == longRange).ToArray();
                double columnWidth = Control<ItemsControl>(mini, "MonthDays").ActualWidth / 7;
                Require(longBars.Length == 2 && longBars[0].Before == "‹" && longBars[0].After == "›" && longBars[1].After == "",
                    "A range was duplicated per day, lost continuation arrows, or did not wrap at the week boundary.");
                Require(Math.Abs(longBars[0].Width - (7 * columnWidth - 17)) < 1 &&
                    Math.Abs(longBars[1].Width - (3 * columnWidth - 17)) < 1 && longBars.All(b => b.Background == "#336699"),
                    "Connected bar geometry or color changed between weeks.");
                Require(bars.Single(b => b.Item == overlap).Top == bars.Single(b => b.Item == disjoint).Top,
                    "Disjoint ranges unnecessarily occupy separate lanes.");
                for (int a = 0; a < bars.Length; a++)
                    for (int b = a + 1; b < bars.Length; b++)
                        Require(!new Rect(bars[a].Left, bars[a].Top, bars[a].Width, bars[a].Height)
                            .IntersectsWith(new Rect(bars[b].Left, bars[b].Top, bars[b].Width, bars[b].Height)), "Two visible range bars overlap.");
                var grid = Control<ItemsControl>(mini, "MonthDays");
                foreach (var cell in grid.Items.Cast<MiniWindow.MonthDayCell>())
                    Require(cell.Count == cell.VisibleEvents.Count + cell.VisibleRangeCount + cell.OverflowCount, "Monthly range counts omit a schedule.");
                Require(VisualDescendants<TextBlock>(grid).All(t => t.Name != "MonthEventTitle" || t.Text != longRange.Title),
                    "The old per-day duplicate range cards remain.");

                mini.SetMonthViewOpen(false);
                for (int days = 1; days <= 7; days++)
                {
                    data.MiniDayCount = days;
                    mini.ShowRangeFrom(new DateTime(2026, 9, 28)); LayoutCalendarMode(mini);
                    var weekBars = RangeBars(mini, "WeekRanges");
                    Require(weekBars.Count(b => b.Item == longRange) == 1, "Basic mode repeats or hides an otherwise visible range.");
                    Require(weekBars.All(b => b.Left >= 0 && b.Left + b.Width <= Control<ItemsControl>(mini, "WeekDays").ActualWidth),
                        "A basic range extends outside the visible dates.");
                }
                foreach (var limits in new[] { new[] { "0001-01-01", "0001-01-03" }, new[] { "9999-12-29", "9999-12-31" } })
                {
                    data.Schedules.Clear(); data.Schedules.Add(new ScheduleItem { Title = "Boundary", Period = limits[0], EndPeriod = limits[1] });
                    mini.SetMonthViewOpen(true); mini.ShowRangeFrom(DateTime.ParseExact(limits[0], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
                    LayoutCalendarMode(mini);
                    Require(RangeBars(mini, "MonthRanges").Length == 1, "Boundary range overflowed DateTime or missing valid dates.");
                }
            }
            finally { mini.Close(); }
        }

        private static void CalendarRangeActions()
        {
            var data = new AppData { MiniCharacterVisible = false, MiniPlayerVisible = false };
            for (int i = 0; i < 10; i++) data.Schedules.Add(new ScheduleItem { Title = "Range " + i, Period = "2026-10-05", EndPeriod = "2026-10-09", Time = "09:00" });
            data.Schedules.Add(new ScheduleItem { Title = "Single", Period = "2026-10-06" });
            var mini = new MiniWindow(data, () => true, () => { });
            SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1040)));
            try
            {
                mini.ShowRangeFrom(new DateTime(2026, 10, 5)); LayoutCalendarMode(mini);
                var weekCell = Control<ItemsControl>(mini, "WeekDays").Items.Cast<MiniWindow.DayCell>().Single(c => c.Date.Day == 6);
                Require(weekCell.HiddenRangeCount > 0 && RangeBars(mini, "WeekRanges").Length + weekCell.HiddenRangeCount == 10,
                    "Basic mode hides crowded ranges without a reachable overflow count.");
                mini.SetMonthViewOpen(true); mini.ShowRangeFrom(new DateTime(2026, 10, 6)); LayoutCalendarMode(mini);
                var bar = RangeBars(mini, "MonthRanges").First();
                Call(mini, "PrepareRangeEditor", bar);
                Require(Control<ItemsControl>(mini, "DaySchedules").Items.Count == 10 && Control<TextBox>(mini, "MiniTitleInput").Text == bar.Item.Title &&
                    Control<DatePicker>(mini, "MiniDateInput").SelectedDate == bar.Item.StartDate && Control<CheckBox>(mini, "MiniRangeToggle").IsChecked == true,
                    "The connected bar editor no longer targets the full original range.");
                SetField(mini, "selectedDay", null);
                SetField(mini, "blockItem", bar.Item); Call(mini, "BlockDone_Click", mini, ClickArgs()); LayoutCalendarMode(mini);
                Require(bar.Item.IsCompleted, "Completion from the range context menu did not reach the original schedule.");
                foreach (double font in new[] { 10.0, 14.0, 24.0 })
                {
                    mini.ApplyAppearance(new AppearanceSettings { TitleFontSize = font }); LayoutCalendarMode(mini);
                    var cells = Control<ItemsControl>(mini, "MonthDays").Items.Cast<MiniWindow.MonthDayCell>();
                    Require(cells.All(c => c.Count == c.VisibleEvents.Count + c.VisibleRangeCount + c.OverflowCount), "Large font loses hidden range counts.");
                    Require(RangeBars(mini, "MonthRanges").All(b => b.FontSize == font), "Settings font does not reach connected ranges.");
                }
                var removed = bar.Item; data.Schedules.Remove(removed); mini.Refresh(); LayoutCalendarMode(mini);
                Require(RangeBars(mini, "MonthRanges").All(b => b.Item != removed), "Deleted range remains visible.");
                Call(mini, "PrepareDayDetails", new DateTime(2026, 10, 6));
                Require(Control<ItemsControl>(mini, "DaySchedules").Items.Count == 10 && !mini.IsVisible, "Overflow items cannot be reached in date details.");
            }
            finally { mini.Close(); }
        }

        private static void CalendarModeTransition()
        {
            var data = new AppData { MiniCharacterVisible = true, MiniPlayerVisible = false,
                Appearance = new AppearanceSettings { Opacity = .6 },
                MiniBoard = new MonitorStateData { Left = 200, Top = 100, Width = 792, Height = 312 } };
            var mini = new MiniWindow(data, () => true, () => { });
            SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1040)));
            try
            {
                LayoutCalendarMode(mini);
                var from = CalendarBoard(mini);
                var target = new MonitorStateData { Left = from.Left, Top = 0, Width = from.Width, Height = 780 };
                double originalPetSize = (double)Field(mini, "characterBaseHeight");
                data.MiniMonthView = true;
                Call(mini, "ApplyCalendarModeVisibility", true);
                SetField(mini, "changingCalendarMode", true);
                mini.Refresh();
                Call(mini, "StartCalendarModeTransition", from, target, new DrawingImage());
                Require(Control<ItemsControl>(mini, "MonthDays").ActualWidth > from.Width - 40 && Control<ItemsControl>(mini, "MonthDays").ActualHeight > 600,
                    "The destination month was clipped to the old compact layout before it was captured.");
                var picture = (System.Windows.Media.Imaging.BitmapSource)Control<Image>(mini, "CalendarModeNext").Source;
                var pixel = new byte[4];
                picture.CopyPixels(new Int32Rect(picture.PixelWidth / 2, picture.PixelHeight - 30, 1, 1), pixel, 4, 0);
                Require(Control<Grid>(mini, "MiniRoot").Opacity == 0 && pixel[3] > 200,
                    "Preparing the destination exposed the live board or captured its temporary zero opacity.");
                double nativeWidth = mini.Width, nativeHeight = mini.Height;
                var fromPixels = (Rect)Field(mini, "modeBoardFromPixels");
                var toPixels = (Rect)Field(mini, "modeBoardToPixels");
                double lastWidth = fromPixels.Width, lastHeight = fromPixels.Height;
                foreach (double progress in new[] { 0.0, .1, .3, .6, .9 })
                {
                    Call(mini, "AdvanceCalendarModeTransition", progress); LayoutCalendarMode(mini);
                    var now = Control<Image>(mini, "CalendarModeSnapshot"); Call(mini, "SavePosition");
                    var drawn = now.RenderTransform.TransformBounds(new Rect(0, 0, now.Width, now.Height));
                    Require(mini.Width == nativeWidth && mini.Height == nativeHeight && now.Opacity == 1,
                        "An animation frame resized the native window or faded the board to the desktop.");
                    Require(drawn.Width >= lastWidth && drawn.Width <= toPixels.Width && drawn.Height >= lastHeight && drawn.Height <= toPixels.Height,
                        "Mode transition temporarily shrinks or overshoots the calendar.");
                    Require(data.MiniMonthBoard == null && data.MiniBoard.Width == from.Width &&
                        (double)Field(mini, "characterBaseHeight") == originalPetSize, "An intermediate frame was saved or resized the pets.");
                    lastWidth = drawn.Width; lastHeight = drawn.Height;
                }
                // Toggling back during the animation completes its saved target, then restores the original mode.
                mini.SetMonthViewOpen(false); LayoutCalendarMode(mini);
                Require(CalendarBoard(mini).Width == from.Width && CalendarBoard(mini).Height == from.Height && data.MiniMonthBoard.Width == target.Width &&
                    Field(mini, "modeTransitionTo") == null && Control<Image>(mini, "CalendarModeSnapshot").Source == null &&
                    !(bool)Field(mini, "changingCalendarMode") && Control<Grid>(mini, "MiniRoot").Opacity == .6,
                    "Interrupted mode changes leave a snapshot, handler, wrong saved size or wrong opacity behind.");
                // The temporary window owns the canvas in live use. Closing must detach it before restoring its parent.
                var outer = Control<Grid>(mini, "OuterRoot");
                var overlay = Control<Canvas>(mini, "CalendarModeOverlay");
                outer.Children.Remove(overlay);
                var host = new Grid(); host.Children.Add(overlay);
                var overlayWindow = new Window { Content = host };
                SetField(mini, "modeTransitionWindow", overlayWindow);
                var fade = (System.Threading.Tasks.Task<bool>)typeof(MiniWindow).GetMethod("FadeModeCoverAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .Invoke(mini, new object[] { 0.0, 1.0, 80 });
                Call(mini, "CloseCalendarModeOverlay");
                Require(overlay.Parent == outer && Field(mini, "modeTransitionWindow") == null && !overlayWindow.IsVisible,
                    "Closing the temporary presentation window left a second visual parent or a visible window behind.");
                Require(fade.IsCompleted && !fade.Result && Field(mini, "modeCoverFade") == null,
                    "Closing during the handoff left an unfinished opacity animation task.");
            }
            finally { mini.Close(); }
        }

        private static void CalendarModePixelHandoff()
        {
            var data = new AppData { MiniCharacterVisible = false, MiniPlayerVisible = false,
                Appearance = new AppearanceSettings { Opacity = .55 },
                MiniMonthBoard = new MonitorStateData { Left = 100, Top = 60, Width = 1101.83, Height = 754.08 },
                MiniBoard = new MonitorStateData { Left = 400, Top = 600, Width = 793.88, Height = 315.05 } };
            var mini = new MiniWindow(data, () => true, () => { });
            SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1080)));
            try
            {
                foreach (bool month in new[] { false, true })
                {
                    mini.SetMonthViewOpen(month); LayoutCalendarMode(mini);
                    var content = Control<Grid>(mini, "CalendarBoardContent");
                    // A shown HWND has an integral device-pixel origin. Match that here before comparing raster output.
                    var dpi = VisualTreeHelper.GetDpi(content);
                    mini.Left = Math.Round(mini.Left * dpi.DpiScaleX) / dpi.DpiScaleX;
                    mini.Top = Math.Round(mini.Top * dpi.DpiScaleY) / dpi.DpiScaleY;
                    var photo = CalendarValue<System.Windows.Media.Imaging.BitmapSource>(mini, "CaptureCalendarBoardImage");
                    var reference = new System.Windows.Media.Imaging.RenderTargetBitmap(photo.PixelWidth, photo.PixelHeight,
                        photo.DpiX, photo.DpiY, PixelFormats.Pbgra32);
                    reference.Render(content);
                    var expected = new byte[photo.PixelWidth * photo.PixelHeight * 4];
                    var actual = new byte[expected.Length];
                    reference.CopyPixels(expected, photo.PixelWidth * 4, 0); photo.CopyPixels(actual, photo.PixelWidth * 4, 0);
                    int different = expected.Zip(actual, (a, b) => a != b ? 1 : 0).Sum();
                    Require(different < expected.Length / 1000, "Snapshot displaced or reconstructed the live board's pixels (month=" + month + "): " + different);
                }
                mini.SetMonthViewOpen(false); LayoutCalendarMode(mini);
                var from = CalendarBoard(mini);
                var target = new MonitorStateData { Left = 100, Top = 60, Width = 1101.83, Height = 754.08 };
                data.MiniMonthView = true; Call(mini, "ApplyCalendarModeVisibility", true);
                SetField(mini, "changingCalendarMode", true); mini.Refresh();
                Call(mini, "StartCalendarModeTransition", from, target, new DrawingImage());
                var image = Control<Image>(mini, "CalendarModeNext");
                var start = (Rect)Field(mini, "modeBoardFromPixels"); var end = (Rect)Field(mini, "modeBoardToPixels");
                var envelope = (Rect)Field(mini, "modeOverlayPixels");
                Rect Drawn() => image.RenderTransform.TransformBounds(new Rect(0, 0, image.Width, image.Height));
                Call(mini, "AdvanceCalendarModeTransition", .1);
                var early = Drawn();
                double earlyTravel = Math.Abs(early.Top + envelope.Top - start.Top) / Math.Abs(end.Top - start.Top);
                Require(earlyTravel < .04, "Window movement starts with a sudden jump instead of easing in.");
                Call(mini, "AdvanceCalendarModeTransition", .9);
                var late = Drawn();
                double remaining = Math.Abs(late.Top + envelope.Top - end.Top) / Math.Abs(end.Top - start.Top);
                Require(remaining < .04 && Math.Abs(earlyTravel - remaining) < .00001, "Position and size do not settle symmetrically.");
                double sizeRemaining = (end.Width - late.Width) / (end.Width - start.Width);
                Require(Math.Abs(sizeRemaining - remaining) < .00001, "Window position and size use different motion timing.");
                Call(mini, "PaintCalendarModeTransition", 1.0);
                var final = Drawn(); final.Offset(envelope.Left, envelope.Top);
                Require(final == end && image.Width == end.Width && image.Height == end.Height,
                    "Final snapshot does not line up with the destination's actual rendered pixels.");
                Call(mini, "FinishCalendarModeTransition");
                var progressProperty = (DependencyProperty)typeof(MiniWindow).GetField("ModeTransitionProgressProperty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
                Require((double)mini.GetValue(progressProperty) == 1,
                    "Detaching the animation resets its position to the starting frame.");
                Require(Control<Grid>(mini, "MiniRoot").IsHitTestVisible && Control<Grid>(mini, "MiniRoot").Opacity == .55,
                    "Mode completion did not restore input or the user's opacity.");
            }
            finally { mini.Close(); }
        }
    }
}
