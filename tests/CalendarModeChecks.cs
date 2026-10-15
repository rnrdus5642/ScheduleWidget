using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static T CalendarValue<T>(MiniWindow window, string method) =>
            (T)typeof(MiniWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
        private static MonitorStateData CalendarBoard(MiniWindow window) => CalendarValue<MonitorStateData>(window, "CaptureCalendarBoard");

        private static void LayoutCalendarMode(MiniWindow window)
        {
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(window.Width, window.Height));
            content.Arrange(new Rect(0, 0, window.Width, window.Height)); content.UpdateLayout();
        }

        private static void CalendarModeGeometry()
        {
            var data = new AppData { MiniCharacterVisible = false, MiniPlayerVisible = false,
                MiniBoard = new MonitorStateData { Left = 120, Top = 90, Width = 792, Height = 312 } };
            var store = Store("calendar-modes");
            var mini = new MiniWindow(data, () => { store.SaveData(data); return true; }, () => { });
            SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1040)));
            try
            {
                var compact = CalendarBoard(mini);
                mini.SetMonthViewOpen(true); LayoutCalendarMode(mini);
                var expanded = CalendarBoard(mini);
                Require(mini.IsMonthViewOpen && data.MiniMonthView && expanded.Width == compact.Width && expanded.Height >= 780,
                    "Monthly mode did not keep the width while increasing height.");
                Require(expanded.Left == compact.Left, "Opening the month moved a horizontal edge.");
                Require(expanded.Left >= 0 && expanded.Top >= 0 && expanded.Left + expanded.Width <= 1920 && expanded.Top + expanded.Height <= 1040,
                    "Expanded calendar went off screen.");
                mini.Width = 1208; mini.Height = 808; mini.Left = 296; mini.Top = 76;
                LayoutCalendarMode(mini); Call(mini, "SavePosition");
                var customMonth = CalendarBoard(mini);
                mini.ShowRangeFrom(new DateTime(2027, 6, 1));
                mini.SetMonthViewOpen(false);
                var restored = CalendarBoard(mini);
                Require(restored.Width == customMonth.Width && restored.Height == compact.Height && restored.Left == customMonth.Left && restored.Top >= customMonth.Top,
                    "Returning to basic mode changed the shared width or lost its compact height.");
                var movedCompact = restored;
                mini.SetMonthViewOpen(true);
                restored = CalendarBoard(mini);
                Require(restored.Width == customMonth.Width && restored.Height == customMonth.Height &&
                    Math.Abs(restored.Left + restored.Width / 2 - movedCompact.Left - movedCompact.Width / 2) < .01 &&
                    (DateTime)Field(mini, "displayedMonth") == new DateTime(2027, 6, 1), "Monthly size, anchored position or navigated month was forgotten.");
                Call(mini, "FlushSave");
                var loaded = store.LoadData().Data;
                Require(loaded.MiniMonthView && loaded.MiniBoard.Width == customMonth.Width && loaded.MiniMonthBoard.Width == customMonth.Width &&
                    loaded.MiniBoard.Height == compact.Height, "Shared width or independent heights were not saved.");
                var reopened = new MiniWindow(loaded, () => true, () => { });
                try
                {
                    Require(reopened.IsMonthViewOpen && CalendarBoard(reopened).Width >= Math.Min(760, SystemParameters.WorkArea.Width), "Last mode was not restored on restart.");
                    reopened.SetMonthViewOpen(false);
                    Require(CalendarBoard(reopened).Width == customMonth.Width && CalendarBoard(reopened).Height == compact.Height, "Restart lost the shared width or compact height.");
                }
                finally { reopened.Close(); }
            }
            finally { mini.Close(); }

            foreach (Rect area in new[] { new Rect(-1920, -100, 1920, 1040), new Rect(0, 0, 640, 480) })
            {
                var desired = new MonitorStateData { Left = 9000, Top = 9000, Width = 3000, Height = 3000 };
                var fitted = (MonitorStateData)typeof(MiniWindow).GetMethod("FitCalendarBoard", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { desired, area, true });
                Require(fitted.Width <= area.Width && fitted.Height <= area.Height && fitted.Left >= area.Left && fitted.Top >= area.Top &&
                    fitted.Left + fitted.Width <= area.Right && fitted.Top + fitted.Height <= area.Bottom, "Mode fitting failed on a small or secondary monitor.");
            }
        }

        private static void CalendarAnchoredExpansion()
        {
            MonitorStateData Place(MonitorStateData current, MonitorStateData desired, Rect area, bool month, double anchor) =>
                (MonitorStateData)typeof(MiniWindow).GetMethod("AnchorCalendarBoard", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { current, desired, area, month, anchor });
            foreach (var area in new[] { new Rect(0, 0, 1920, 1040), new Rect(-1920, -100, 1920, 1040), new Rect(0, 0, 640, 480) })
            {
                double width = Math.Min(800, area.Width * .6), height = Math.Min(300, area.Height * .5);
                foreach (double x in new[] { area.Left, area.Left + area.Width / 2 - width / 2, area.Right - width })
                foreach (double y in new[] { area.Top, area.Top + area.Height / 2 - height / 2, area.Bottom - height })
                {
                    var start = new MonitorStateData { Left = x, Top = y, Width = width, Height = height };
                    var expanded = Place(start, new MonitorStateData { Left = 9000, Top = -9000, Width = 1120, Height = 780 }, area, true, .5);
                    Require(expanded.Left == start.Left && expanded.Width == start.Width,
                        "A mode switch moved the left or right edge.");
                    Require(expanded.Width >= width && expanded.Height >= height && expanded.Left >= area.Left - .001 && expanded.Top >= area.Top - .001 &&
                        expanded.Left + expanded.Width <= area.Right + .001 && expanded.Top + expanded.Height <= area.Bottom + .001,
                        "The expanded board shrank or left the active work area.");
                    if (y == area.Top) Require(expanded.Top == y, "No room above: the top edge must remain fixed.");
                    if (y + height == area.Bottom) Require(Math.Abs(expanded.Top + expanded.Height - area.Bottom) < .001, "No room below: the bottom edge must remain fixed.");
                    double anchor = (start.Top - expanded.Top) / (expanded.Height - start.Height);
                    var restored = Place(expanded, start, area, false, anchor);
                    Require(Math.Abs(restored.Left - start.Left) < .001 && Math.Abs(restored.Top - start.Top) < .001 &&
                        Math.Abs(restored.Width - start.Width) < .001 && Math.Abs(restored.Height - start.Height) < .001,
                        "A normal expand/collapse cycle moved its original anchor.");
                }
            }

            var desktop = new Rect(0, 0, 1920, 1040);
            var preferred = new MonitorStateData { Width = 1120, Height = 780 };
            var nearTop = new MonitorStateData { Left = 500, Top = 4, Width = 800, Height = 300 };
            var nearBottom = new MonitorStateData { Left = 500, Top = 736, Width = 800, Height = 300 };
            Require(Place(nearTop, preferred, desktop, true, .5).Top == 4,
                "Downward growth consumed the user's small top margin.");
            var bottomResult = Place(nearBottom, preferred, desktop, true, .5);
            Require(bottomResult.Top + bottomResult.Height == 1036,
                "Upward growth consumed the user's small taskbar margin.");

            var data = new AppData { MiniCharacterVisible = false, MiniPlayerVisible = false,
                MiniBoard = new MonitorStateData { Left = 500, Top = 740, Width = 800, Height = 300 },
                MiniMonthBoard = new MonitorStateData { Left = -5000, Top = -5000, Width = 1120, Height = 780 } };
            var mini = new MiniWindow(data, () => true, () => { });
            SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1040)));
            try
            {
                mini.SetMonthViewOpen(true); LayoutCalendarMode(mini);
                var enlarged = CalendarBoard(mini);
                Require(enlarged.Top == 260 && enlarged.Left == 500 && enlarged.Width == 800,
                    "Opening used a stale width/location instead of keeping both horizontal edges fixed.");
                // A restart can recover the anchor from the contained compact rectangle without a new data format.
                SetField(mini, "calendarVerticalAnchor", null);
                mini.SetMonthViewOpen(false); LayoutCalendarMode(mini);
                Require(CalendarBoard(mini).Top == 740 && CalendarBoard(mini).Left == 500, "Restart-style anchor inference lost the bottom edge.");
                mini.Left += 100; mini.Top -= 200; LayoutCalendarMode(mini);
                var moved = CalendarBoard(mini);
                mini.SetMonthViewOpen(true); LayoutCalendarMode(mini);
                Require(Math.Abs(CalendarBoard(mini).Left + CalendarBoard(mini).Width / 2 - moved.Left - moved.Width / 2) < .001,
                    "Moving the basic calendar was ignored on the next expansion.");
            }
            finally { mini.Close(); }

            var narrow = new MiniWindow(new AppData { MiniCharacterVisible = false, MiniPlayerVisible = false, MiniMonthView = true,
                MiniBoard = new MonitorStateData { Left = 300, Top = 100, Width = 420, Height = 300 },
                MiniMonthBoard = new MonitorStateData { Left = 200, Top = 100, Width = 1120, Height = 780 } }, () => true, () => { });
            try
            {
                Require(CalendarBoard(narrow).Width == 420 && CalendarBoard(narrow).Left == 300,
                    "Restart retained the old horizontal expansion or imposed a wider monthly minimum.");
                narrow.SetMonthViewOpen(false);
                Require(CalendarBoard(narrow).Width == 420 && CalendarBoard(narrow).Left == 300,
                    "Closing a narrow month moved its horizontal edges.");
            }
            finally { narrow.Close(); }
        }

        private static void MonthScheduleVisibility()
        {
            var day = new DateTime(2026, 10, 8);
            var data = new AppData { MiniCharacterVisible = false, MiniPlayerVisible = false };
            for (int i = 0; i < 9; i++) data.Schedules.Add(new ScheduleItem { Title = "Visible task " + i,
                Period = "2026-10-08", Time = (8 + i).ToString("00") + ":00", Color = i == 0 ? "#103050" : null });
            data.Schedules.Add(new ScheduleItem { Title = "Continuing event", Period = "2026-09-01", EndPeriod = "2026-10-09" });
            var mini = new MiniWindow(data, () => true, () => { });
            SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1040)));
            try
            {
                mini.SetMonthViewOpen(true); mini.ShowRangeFrom(day); LayoutCalendarMode(mini);
                Require(!double.IsNaN(mini.Left) && !double.IsNaN(mini.Top), "First-run mode change left the native window position automatic.");
                var grid = Control<ItemsControl>(mini, "MonthDays");
                MiniWindow.MonthDayCell Cell() => grid.Items.Cast<MiniWindow.MonthDayCell>().Single(c => c.Date == day);
                var cell = Cell();
                Require(cell.Count == 10 && cell.VisibleEvents.Count >= 1 && cell.HasOverflow && cell.OverflowCount + cell.VisibleEvents.Count + cell.VisibleRangeCount == 10,
                    "Busy days omit the visible schedule list or the overflow count.");
                Require(cell.VisibleRangeCount == 1 && cell.VisibleEvents[0].Time == "08:00" && cell.VisibleEvents.All(e => !e.Item.IsMultiDay),
                    "Spanning events were duplicated in the date cell or single-day times were lost.");
                var custom = cell.VisibleEvents.Single(e => e.Item == data.Schedules[0]);
                Require(custom.Ink == FeatureRules.ReadableText("#103050") && custom.Background == "#103050", "Custom schedule color is not readable.");
                Require(VisualDescendants<TextBlock>(grid).Any(t => t.Name == "MonthEventTitle" && t.Text == "Visible task 0" && t.ActualWidth > 0),
                    "Event titles were not rendered in the month cells.");
                Call(mini, "PrepareDayDetails", day);
                Require(Control<ItemsControl>(mini, "DaySchedules").Items.Count == 10, "Date details cannot reach every overflow event.");
                int previousVisibleCount = cell.VisibleEvents.Count;
                mini.ApplyAppearance(new AppearanceSettings { TitleFontSize = 24 }); LayoutCalendarMode(mini);
                Require(Cell().VisibleEvents.Count >= 1 && Cell().VisibleEvents.Count < previousVisibleCount &&
                    VisualDescendants<TextBlock>(grid).Where(t => t.Name == "MonthEventTitle").All(t => t.FontSize == 24), "Large font did not reduce density or apply to month titles.");
                data.Schedules[0].Title = "Changed in sync"; mini.Refresh(); LayoutCalendarMode(mini);
                Require(Cell().Events.Any(e => e.Title == "Changed in sync"), "Schedule changes did not reach the month.");
                mini.ApplyAppearance(data.Appearance); mini.ShowRangeFrom(new DateTime(2026, 3, 1)); LayoutCalendarMode(mini);
                Require(grid.Items.Count == 42, "Six-week month lost its last week.");
            }
            finally { mini.Close(); }
        }

        private static void CalendarModePetScale()
        {
            var data = new AppData { MiniCharacterVisible = true, MiniCharacterScale = 140, MiniPlayerVisible = false,
                MiniBoard = new MonitorStateData { Left = 300, Top = 100, Width = 792, Height = 312 } };
            var mini = new MiniWindow(data, () => true, () => { });
            SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1040)));
            try
            {
                double basic = CalendarValue<double>(mini, "PetSizeBasis");
                mini.SetMonthViewOpen(true);
                Require(CalendarValue<double>(mini, "PetSizeBasis") == basic && data.MiniCharacterScale == 140, "Expanding the month unexpectedly enlarged the pets.");
                mini.Height += 100; mini.UpdateCharacterSize();
                Require(CalendarValue<double>(mini, "PetSizeBasis") == basic, "Resizing the month changed the basic-mode pet scale.");
                mini.SetMonthViewOpen(false);
                Require(CalendarValue<double>(mini, "PetSizeBasis") == basic && Math.Abs(CalendarBoard(mini).Height - 312) < 1,
                    "Pet padding prevented restoring the compact board.");
            }
            finally { mini.Close(); }
        }
    }
}
