using System;
using System.Collections.Generic;
using System.Linq;

namespace ScheduleWidget
{
    // One segment per calendar row, rather than one duplicate card per date. No UI or persisted state.
    internal static class CalendarRangeLayout
    {
        internal sealed class Segment
        {
            public ScheduleItem Item;
            public int Row, Column, Span, Lane;
            public DateTime First, Last;
            public bool ContinuesBefore, ContinuesAfter;
            public bool Covers(int column) => column >= Column && column < Column + Span;
        }

        internal static List<Segment> Build(IEnumerable<ScheduleItem> items, DateTime first, int days, int columns)
        {
            if (columns < 1 || columns > 7) throw new ArgumentOutOfRangeException(nameof(columns));
            first = first.Date;
            days = Math.Min(days, (DateTime.MaxValue.Date - first).Days + 1);
            var segments = new List<Segment>();
            if (days <= 0) return segments;
            var last = first.AddDays(days - 1);
            foreach (var item in items.Where(s => s.IsMultiDay && s.Overlaps(first, last)))
            {
                int start = Math.Max(0, (item.StartDate.Value - first).Days);
                int end = Math.Min(days - 1, (item.EndDate.Value - first).Days);
                for (int offset = start; offset <= end;)
                {
                    int row = offset / columns, rowEnd = Math.Min(end, (row + 1) * columns - 1);
                    var from = first.AddDays(offset); var to = first.AddDays(rowEnd);
                    segments.Add(new Segment { Item = item, Row = row, Column = offset % columns, Span = rowEnd - offset + 1,
                        First = from, Last = to, ContinuesBefore = item.StartDate.Value < from, ContinuesAfter = item.EndDate.Value > to });
                    offset = rowEnd + 1;
                }
            }
            foreach (var row in segments.GroupBy(s => s.Row))
            {
                var occupied = new List<int>();
                foreach (var segment in row.OrderBy(s => s.Item.IsCompleted).ThenBy(s => s.Column).ThenByDescending(s => s.Span)
                    .ThenBy(s => s.Item.StartDate).ThenBy(s => s.Item.Time, StringComparer.Ordinal).ThenBy(s => s.Item.Id))
                {
                    int mask = ((1 << segment.Span) - 1) << segment.Column;
                    int lane = occupied.FindIndex(used => (used & mask) == 0);
                    if (lane < 0) { lane = occupied.Count; occupied.Add(0); }
                    occupied[lane] |= mask;
                    segment.Lane = lane;
                }
            }
            return segments.OrderBy(s => s.Row).ThenBy(s => s.Lane).ThenBy(s => s.Column).ToList();
        }
    }
}
