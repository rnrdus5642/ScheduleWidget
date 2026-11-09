using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ScheduleWidget
{
    internal static class CalendarPlacementPolicy
    {
        internal static CalendarMonitorLayout Defaults(string name) => new CalendarMonitorLayout { Name = name };

        internal static MonitorStateData Resolve(CalendarPlacement preferred, Rect area, bool month)
        {
            preferred = preferred ?? new CalendarPlacement { Height = month ? 780 : 312 };
            double width = Math.Min(area.Width, Math.Max(Math.Min(220, area.Width), preferred.Width));
            double height = Math.Min(area.Height, Math.Max(Math.Min(month ? 600 : 190, area.Height), preferred.Height));
            return new MonitorStateData { Width = width, Height = height,
                Left = area.Left + Unit(preferred.Horizontal) * Math.Max(0, area.Width - width),
                Top = area.Top + Unit(preferred.Vertical) * Math.Max(0, area.Height - height) };
        }

        internal static CalendarPlacement Capture(MonitorStateData board, Rect area) => new CalendarPlacement
        {
            Width = board.Width, Height = board.Height,
            Horizontal = Fraction(board.Left - area.Left, area.Width - board.Width),
            Vertical = Fraction(board.Top - area.Top, area.Height - board.Height)
        };

        internal static DisplayMonitor Select(IList<DisplayMonitor> monitors, string preferred, string current = null) =>
            monitors.FirstOrDefault(m => Same(m.Id, preferred)) ?? monitors.FirstOrDefault(m => Same(m.Id, current)) ??
            monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault();

        internal static DisplayMonitor Nearest(IList<DisplayMonitor> monitors, Rect pixels) => monitors
            .OrderByDescending(m => Intersection(m.WorkPixels, pixels)).ThenBy(m => Distance(m.WorkPixels, pixels)).FirstOrDefault();

        private static double Intersection(Rect a, Rect b)
        {
            var both = Rect.Intersect(a, b);
            return both.IsEmpty ? 0 : both.Width * both.Height;
        }
        private static double Distance(Rect a, Rect b)
        {
            double x = b.Left + b.Width / 2, y = b.Top + b.Height / 2;
            double dx = Math.Max(0, Math.Max(a.Left - x, x - a.Right)), dy = Math.Max(0, Math.Max(a.Top - y, y - a.Bottom));
            return dx * dx + dy * dy;
        }

        internal static bool Same(string a, string b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static double Fraction(double position, double room) => room > .001 ? Unit(position / room) : .5;
        private static double Unit(double value) => double.IsNaN(value) || double.IsInfinity(value) ? .5 : Math.Max(0, Math.Min(1, value));

        internal static void Normalize(AppData data)
        {
            var source = data.MiniMonitorLayouts ?? new Dictionary<string, CalendarMonitorLayout>();
            var kept = source.Where(p => !string.IsNullOrWhiteSpace(p.Key) && p.Key.Length <= 1024 &&
                !p.Key.Any(char.IsControl) && p.Value != null)
                .OrderByDescending(p => Same(p.Key, data.MiniPreferredMonitorId)).ThenByDescending(p => p.Value.ChangedUtc).Take(64);
            var normalized = new Dictionary<string, CalendarMonitorLayout>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in kept)
            {
                var layout = pair.Value;
                layout.Compact = NormalizePlacement(layout.Compact, false);
                layout.Month = NormalizePlacement(layout.Month, true);
                layout.Month.Width = layout.Compact.Width;
                layout.Month.Horizontal = layout.Compact.Horizontal;
                if (layout.Name != null && layout.Name.Length > 200) layout.Name = layout.Name.Substring(0, 200);
                normalized[pair.Key] = layout;
            }
            data.MiniMonitorLayouts = normalized;
            if (string.IsNullOrWhiteSpace(data.MiniPreferredMonitorId) || !normalized.ContainsKey(data.MiniPreferredMonitorId))
                data.MiniPreferredMonitorId = null;
        }

        private static CalendarPlacement NormalizePlacement(CalendarPlacement value, bool month)
        {
            value = value ?? new CalendarPlacement { Height = month ? 780 : 312 };
            if (!ValidSize(value.Width)) value.Width = 792;
            if (!ValidSize(value.Height)) value.Height = month ? 780 : 312;
            value.Horizontal = Unit(value.Horizontal); value.Vertical = Unit(value.Vertical);
            return value;
        }
        private static bool ValidSize(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value > 0 && value <= 32768;
    }
}
