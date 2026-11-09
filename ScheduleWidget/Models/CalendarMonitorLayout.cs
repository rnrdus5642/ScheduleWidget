using System;

namespace ScheduleWidget
{
    // Preferred size in DIPs and position within the monitor's available space. Screen fitting never writes here.
    public sealed class CalendarPlacement
    {
        public double Width { get; set; } = 792;
        public double Height { get; set; } = 312;
        public double Horizontal { get; set; } = 0.5;
        public double Vertical { get; set; } = 0.5;
    }

    public sealed class CalendarMonitorLayout
    {
        public string Name { get; set; }
        public CalendarPlacement Compact { get; set; } = new CalendarPlacement();
        public CalendarPlacement Month { get; set; } = new CalendarPlacement { Height = 780 };
        public DateTime ChangedUtc { get; set; }
    }
}
