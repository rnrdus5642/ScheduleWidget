using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ScheduleWidget
{
    public partial class MiniWindow
    {
        private List<CalendarRangeLayout.Segment> weekRangeSegments = new List<CalendarRangeLayout.Segment>();
        private List<CalendarRangeLayout.Segment> monthRangeSegments = new List<CalendarRangeLayout.Segment>();

        private void UpdateWeekRangeLayout()
        {
            if (WeekRanges == null || dayCells.Count == 0) return;
            bool hasRanges = weekRangeSegments.Count > 0;
            double heading = dayCells.Any(c => c.Holiday != null) ? 54 : 40;
            Resources["WeekDayHeadingHeight"] = hasRanges ? heading : double.NaN;
            const double pitch = 25, barHeight = 22;
            double top = heading + 11.5;
            double height = WeekDays.ActualHeight;
            // Leave space for a normal task and the overflow link; the day popup always contains every item.
            int capacity = Math.Max(0, (int)Math.Floor((height - top - 48) / pitch));
            var shown = weekRangeSegments.Where(s => s.Lane < capacity).ToList();
            for (int column = 0; column < dayCells.Count; column++)
            {
                var covering = shown.Where(s => s.Covers(column)).ToList();
                int reserved = covering.Count == 0 ? 0 : covering.Max(s => s.Lane) + 1;
                dayCells[column].RangeInset = new Thickness(0, 5 + reserved * pitch, 0, 0);
                dayCells[column].HiddenRangeCount = weekRangeSegments.Count(s => s.Covers(column)) - covering.Count;
            }
            double width = WeekDays.ActualWidth / dayCells.Count;
            WeekRanges.ItemsSource = shown.Select(s => MakeRangeBar(s, width, height, top, 6.5, pitch, barHeight, 11)).ToList();
        }

        private void UpdateMonthRangeLayout(int capacity, double rowHeight, double gridHeight, double font)
        {
            double pitch = rowHeight + 2;
            int weeks = Math.Max(1, MonthDays.Items.Count / 7);
            var shown = monthRangeSegments.Where(s => s.Lane < capacity).ToList();
            for (int index = 0; index < MonthDays.Items.Count; index++)
            {
                var covering = shown.Where(s => s.Row == index / 7 && s.Covers(index % 7)).ToList();
                int reserved = covering.Count == 0 ? 0 : covering.Max(s => s.Lane) + 1;
                ((MonthDayCell)MonthDays.Items[index]).SetVisibleLimit(capacity, reserved, covering.Count, pitch);
            }
            MonthRanges.ItemsSource = shown.Select(s => MakeRangeBar(s, MonthDays.ActualWidth / 7,
                gridHeight / weeks, 30.5, 8.5, pitch, rowHeight, font)).ToList();
        }

        private CalendarRangeBar MakeRangeBar(CalendarRangeLayout.Segment segment, double columnWidth, double rowHeight,
            double top, double inset, double pitch, double height, double font)
        {
            var item = segment.Item;
            string color = FeatureRules.IsColor(item.Color) ? item.Color : BlockPalette[(uint)item.Id.GetHashCode() % BlockPalette.Length];
            return new CalendarRangeBar
            {
                Item = item, Date = segment.First,
                Left = segment.Column * columnWidth + inset, Top = segment.Row * rowHeight + top + segment.Lane * pitch,
                Width = Math.Max(0, segment.Span * columnWidth - inset * 2), Height = height, FontSize = font,
                Background = color, Ink = FeatureRules.ReadableText(color),
                Corners = new CornerRadius(segment.ContinuesBefore ? 0 : 4, segment.ContinuesAfter ? 0 : 4,
                    segment.ContinuesAfter ? 0 : 4, segment.ContinuesBefore ? 0 : 4),
                Before = segment.ContinuesBefore ? "‹" : "", After = segment.ContinuesAfter ? "›" : "",
                DDay = !IsMonthViewOpen && data.MiniBlockDDayVisible && !item.IsCompleted ? item.DDay : ""
            };
        }

        private bool PrepareRangeEditor(CalendarRangeBar bar)
        {
            if (bar == null || !data.Schedules.Contains(bar.Item)) return false;
            PrepareDayDetails(bar.Date);
            EditMiniSchedule_Click(new Button { DataContext = bar.Item }, new RoutedEventArgs(Button.ClickEvent));
            return true;
        }

        private void RangeBar_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (RecentlyDragged || !(sender is FrameworkElement anchor) || !PrepareRangeEditor(anchor.DataContext as CalendarRangeBar)) return;
            OpenDayPopupAt(anchor);
            MiniTitleInput.Focus();
        }

        public sealed class CalendarRangeBar
        {
            public ScheduleItem Item { get; set; }
            public DateTime Date { get; set; }
            public string Title => (string.IsNullOrWhiteSpace(Item.Time) ? "" : Item.Time + "  ") + Item.Title;
            public bool IsCompleted => Item.IsCompleted;
            public string Hint => Item.Title + "\n" + Item.DateString + " · " + Item.DDay;
            public double Left { get; set; }
            public double Top { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public double FontSize { get; set; }
            public string Background { get; set; }
            public string Ink { get; set; }
            public CornerRadius Corners { get; set; }
            public string Before { get; set; }
            public string After { get; set; }
            public string DDay { get; set; }
        }
    }
}
