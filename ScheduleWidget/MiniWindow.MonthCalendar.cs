using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ScheduleWidget
{
    public partial class MiniWindow
    {
        public bool IsMonthViewOpen => MonthCalendarPanel.Visibility == Visibility.Visible;
        private DateTime displayedMonth = MonthStart(DateTime.Today);
        private DateTime monthToday = DateTime.Today;

        private static DateTime MonthStart(DateTime date) => new DateTime(date.Year, date.Month, 1);

        public void SetMonthViewOpen(bool open)
        {
            if (closed || companion) return;
            FinishCalendarModeTransition();
            if (open == IsMonthViewOpen) { if (open) RefreshMonthCalendar(); return; }
            FinishWeekFlip();
            CloseUpcomingPreview();
            CloseDayPopup();
            BlockPopup.IsOpen = RangePopup.IsOpen = false;
            EndPetPlacement();
            var previous = CaptureCalendarBoard();
            if (IsMonthViewOpen) data.MiniMonthBoard = previous;
            else data.MiniBoard = previous;
            var desired = open ? data.MiniMonthBoard : data.MiniBoard;
            var area = CalendarWorkArea(previous);
            if (!ValidCalendarBoard(desired)) desired = open ? NewMonthBoard(previous) : previous;
            double anchor = open ? .5 : CollapseCalendarAnchor(previous, desired, area);
            var target = AnchorCalendarBoard(previous, desired, area, open, anchor);
            if (open)
            {
                double extraHeight = target.Height - previous.Height;
                calendarVerticalAnchor = extraHeight > .001 ? Clamp((previous.Top - target.Top) / extraHeight, 0, 1) : .5;
            }
            var snapshot = CaptureModeSnapshot();
            if (open && !monthWasOpened)
            {
                // A week straddling two months opens the current month when today belongs to that week.
                int todayOffset = (DateTime.Today - weekStart).Days;
                displayedMonth = MonthStart(todayOffset >= 0 && todayOffset < DayCount ? DateTime.Today : weekStart);
                monthToday = DateTime.Today;
                monthWasOpened = true;
            }
            changingCalendarMode = true;
            if (snapshot != null)
            {
                BeginCalendarModeTransition(open, previous, target, snapshot);
                return;
            }
            try
            {
                data.MiniMonthView = open;
                ApplyCalendarModeVisibility(open);
                ResizeCalendarBoard(target, updateLayout: false);
                Refresh();
                UpdateLayout();
            }
            finally { if (modeTransitionTo == null) changingCalendarMode = false; }
            if (modeTransitionTo == null) SavePosition();
        }

        private void ToggleMonthView_Click(object sender, RoutedEventArgs e)
        {
            if (RecentlyDragged) return;
            SetMonthViewOpen(!(modeTransitionTo != null ? modeTransitionOpen : IsMonthViewOpen));
            e.Handled = true;
        }

        private void MoveMonth(int direction)
        {
            CloseDayPopup();
            if ((direction < 0 && displayedMonth > DateTime.MinValue.Date) ||
                (direction > 0 && displayedMonth < new DateTime(9999, 12, 1)))
                displayedMonth = displayedMonth.AddMonths(Math.Sign(direction));
            Refresh();
        }

        private void RefreshMonthCalendar()
        {
            if (!IsMonthViewOpen) return;
            UpdateMonthCalendarLayout();
            // Follow midnight only when the user has not navigated away from the current month.
            if (monthToday != DateTime.Today && displayedMonth == MonthStart(monthToday))
                displayedMonth = MonthStart(DateTime.Today);
            monthToday = DateTime.Today;
            MonthLabel.Text = displayedMonth.ToString("yyyy년 M월", CultureInfo.GetCultureInfo("ko-KR"));
            System.Windows.Automation.AutomationProperties.SetName(PreviousWeek, "이전 달");
            System.Windows.Automation.AutomationProperties.SetName(NextWeek, "다음 달");
            PreviousWeek.IsEnabled = displayedMonth > DateTime.MinValue.Date;
            NextWeek.IsEnabled = displayedMonth < new DateTime(9999, 12, 1);
            TodayButton.IsEnabled = displayedMonth != MonthStart(DateTime.Today);

            int leading = ((int)displayedMonth.DayOfWeek + 6) % 7;
            int days = DateTime.DaysInMonth(displayedMonth.Year, displayedMonth.Month);
            int cellCount = ((leading + days + 6) / 7) * 7;
            var first = displayedMonth.AddDays(-leading);
            var last = first.AddDays(Math.Min(cellCount - 1, (DateTime.MaxValue.Date - first).Days));
            var byDay = data.Schedules.Where(s => !s.IsMultiDay && s.Period != null).ToLookup(s => s.Period, StringComparer.Ordinal);
            var ranges = data.Schedules.Where(s => s.IsMultiDay && s.Overlaps(first, last)).ToList();
            monthRangeSegments = CalendarRangeLayout.Build(ranges, first, cellCount, 7);
            var cells = new List<MonthDayCell>(cellCount);
            for (int i = 0; i < cellCount; i++)
            {
                if (i > (DateTime.MaxValue.Date - first).Days) { cells.Add(new MonthDayCell()); continue; }
                DateTime date = first.AddDays(i);
                var tasks = DayTasks(byDay[date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)], ranges, date);
                var colors = BlockColors(tasks);
                string holiday = KoreanHolidays.NameOf(date);
                cells.Add(new MonthDayCell
                {
                    Date = date, IsCurrentMonth = date.Month == displayedMonth.Month && date.Year == displayedMonth.Year,
                    IsToday = date == DateTime.Today, Count = tasks.Count,
                    Events = tasks.Select(item => new MonthScheduleBlock
                    {
                        Item = item, Date = date, Accent = colors[item],
                        Time = item.IsMultiDay && item.StartDate != date ? "↳" : item.Time,
                        Background = FeatureRules.IsColor(item.Color) ? item.Color : theme.Header,
                        Ink = FeatureRules.IsColor(item.Color) ? FeatureRules.ReadableText(item.Color) : AppearanceColor(scheduleAppearance.TextColor, theme.Ink)
                    }).ToList(),
                    Ink = holiday != null || date.DayOfWeek == DayOfWeek.Sunday ? theme.Holiday :
                        date.DayOfWeek == DayOfWeek.Saturday ? theme.Saturday : AppearanceColor(scheduleAppearance.TextColor, theme.Ink),
                    Hint = date.ToString("yyyy년 M월 d일 (ddd)", CultureInfo.GetCultureInfo("ko-KR")) +
                        (holiday == null ? "" : " · " + holiday) + "\n" +
                        (tasks.Count == 0 ? "일정 없음 · 클릭하여 추가" : string.Join("\n", tasks.Select(s =>
                            (s.IsCompleted ? "✓ " : "• ") + (string.IsNullOrWhiteSpace(s.Time) ? "" : s.Time + " ") + s.Title)))
                });
            }
            MonthDays.ItemsSource = cells;
            UpdateMonthCalendarLayout();
        }

        private void CalendarSheet_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateMonthCalendarLayout();

        private void UpdateMonthCalendarLayout()
        {
            if (CalendarSheet == null) return;
            double font = FiniteRange(scheduleAppearance.TitleFontSize, 10, 24, 14);
            double rowHeight = Math.Ceiling(font * 1.3) + 5;
            Resources["MonthEventRowHeight"] = rowHeight;
            if (MonthDays == null || MonthDays.Items.Count == 0) return;
            int weeks = Math.Max(1, MonthDays.Items.Count / 7);
            double height = MonthDays.ActualHeight > 0 ? MonthDays.ActualHeight : Math.Max(0, BoardHeight - 110);
            // Date heading, padding and the overflow link retain their own space at every font size.
            int capacity = Math.Max(0, Math.Min(6, (int)Math.Floor((height / weeks - 49) / (rowHeight + 2))));
            UpdateMonthRangeLayout(capacity, rowHeight, height, font);
        }

        public sealed class MonthScheduleBlock
        {
            public ScheduleItem Item { get; set; }
            public DateTime Date { get; set; }
            public string Title => Item.Title;
            public bool IsCompleted => Item.IsCompleted;
            public string Time { get; set; }
            public bool HasTime => !string.IsNullOrWhiteSpace(Time);
            public string Background { get; set; }
            public string Accent { get; set; }
            public string Ink { get; set; }
            public string Hint => Item.Title + "\n" + Item.DateString + " · " + Item.DDay;
        }

        public sealed class MonthDayCell : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler PropertyChanged;
            public DateTime? Date { get; set; }
            public string Day => Date?.Day.ToString(CultureInfo.InvariantCulture) ?? "";
            public bool IsValid => Date.HasValue;
            public bool IsCurrentMonth { get; set; }
            public bool IsToday { get; set; }
            public int Count { get; set; }
            public string CountLabel => Count > 99 ? "99+" : Count.ToString(CultureInfo.InvariantCulture);
            public bool HasSchedules => Count > 0;
            public string Ink { get; set; }
            public string Hint { get; set; }
            public List<MonthScheduleBlock> Events { get; set; } = new List<MonthScheduleBlock>();
            public List<MonthScheduleBlock> VisibleEvents { get; private set; } = new List<MonthScheduleBlock>();
            public int VisibleRangeCount { get; private set; }
            public Thickness RangeInset { get; private set; }
            public int OverflowCount => Events.Count - VisibleEvents.Count - VisibleRangeCount;
            public bool HasOverflow => OverflowCount > 0;
            public string OverflowLabel => "+" + OverflowCount + "개 더 보기";
            private int visibleLimit = -1;

            internal void SetVisibleLimit(int count, int reserved, int rangeCount, double pitch)
            {
                count = Math.Max(0, count - reserved);
                var inset = new Thickness(0, reserved * pitch, 0, 0);
                if (visibleLimit == count && VisibleRangeCount == rangeCount && RangeInset == inset) return;
                visibleLimit = count;
                VisibleRangeCount = rangeCount;
                RangeInset = inset;
                VisibleEvents = Events.Where(e => !e.Item.IsMultiDay).Take(count).ToList();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            }
        }
    }
}
