using System;
using System.Windows;

namespace ScheduleWidget
{
    /// <summary>Positions the schedule list beside the calendar, in one shared coordinate system.</summary>
    public static class ScheduleListPlacement
    {
        public static Point Beside(Rect calendar, Size list, Rect workArea, double gap = 12)
        {
            double rightSpace = workArea.Right - calendar.Right - gap;
            double leftSpace = calendar.Left - workArea.Left - gap;
            bool useRight = rightSpace >= list.Width || (leftSpace < list.Width && rightSpace >= leftSpace);
            double left = useRight ? calendar.Right + gap : calendar.Left - gap - list.Width;

            // A crowded screen can require overlap; the list must still stay reachable on that monitor.
            return new Point(
                Math.Max(workArea.Left, Math.Min(left, Math.Max(workArea.Left, workArea.Right - list.Width))),
                Math.Max(workArea.Top, Math.Min(calendar.Top, Math.Max(workArea.Top, workArea.Bottom - list.Height))));
        }
    }
}
