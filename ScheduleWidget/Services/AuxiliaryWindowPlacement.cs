using System;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ScheduleWidget
{
    // Transient editors use the monitor they are on, independent of the calendar's saved monitor profiles.
    // Temporary fitting preserves their requested size so a larger work area can restore it.
    internal sealed class AuxiliaryWindowPlacement
    {
        private readonly Window window;
        private readonly DispatcherTimer timer;
        private readonly double minimumWidth, minimumHeight;
        private double preferredWidth, preferredHeight;
        private bool applying, placed, closed, moving;
        private HwndSource source;

        internal AuxiliaryWindowPlacement(Window window)
        {
            this.window = window;
            preferredWidth = window.Width; preferredHeight = window.Height;
            minimumWidth = window.MinWidth; minimumHeight = window.MinHeight;
            timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (s, e) => { timer.Stop(); if (!moving) Fit(!placed); };
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.SourceInitialized += SourceInitialized;
            window.Loaded += (s, e) => Fit(!placed);
            window.LocationChanged += (s, e) => Queue();
            window.StateChanged += (s, e) => Queue();
            window.Closed += (s, e) =>
            {
                closed = true; timer.Stop();
                source?.RemoveHook(WindowMessage);
                source = null;
            };
        }

        private void SourceInitialized(object sender, EventArgs e)
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source?.AddHook(WindowMessage);
            Fit(true);
        }

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x0231) moving = true; // WM_ENTERSIZEMOVE
            else if (message == 0x0232) // WM_EXITSIZEMOVE
            {
                moving = false;
                // Moving alone must not turn a temporary small-screen fit into the preferred size.
                if (resizedByUser) { preferredWidth = window.Width; preferredHeight = window.Height; }
                resizedByUser = false;
                Queue();
            }
            else if (message == 0x0214) resizedByUser = true; // WM_SIZING
            else if (message == 0x007E || message == 0x001F) // Display changes or WM_CANCELMODE can interrupt native resizing.
            {
                moving = resizedByUser = false;
                Queue();
            }
            else if (message == 0x02E0 || message == 0x001A) Queue(); // DPI/work area
            return IntPtr.Zero;
        }

        private bool resizedByUser;
        private void Queue(int delayMs = 200)
        {
            if (closed || applying || source == null) return;
            timer.Interval = TimeSpan.FromMilliseconds(delayMs);
            timer.Stop(); timer.Start();
        }

        private void Fit(bool initial)
        {
            if (closed || applying || window.WindowState == WindowState.Minimized) return;
            var monitors = DisplayMonitors.Read();
            if (monitors.Count == 0) { Queue(1000); return; } // Display enumeration may be empty briefly during reconnect.
            Window anchorWindow = initial && window.Owner != null ? window.Owner : window;
            Rect pixels = Rect.Empty;
            var handle = new WindowInteropHelper(anchorWindow).Handle;
            if (handle != IntPtr.Zero && NativeMethods.TryGetWindowRect(handle, out NativeMethods.Rect rect))
                pixels = new Rect(rect.Left, rect.Top, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
            var monitor = pixels.IsEmpty ? monitors.FirstOrDefault(m => m.Primary) ?? monitors[0] : CalendarPlacementPolicy.Nearest(monitors, pixels);
            Rect anchor = pixels.IsEmpty ? monitor.WorkArea : new Rect(pixels.X / monitor.ScaleX, pixels.Y / monitor.ScaleY,
                pixels.Width / monitor.ScaleX, pixels.Height / monitor.ScaleY);
            Apply(monitor.WorkArea, anchor, initial);
        }

        internal static Rect FitWithin(Rect area, Rect anchor, Size requested, bool center)
        {
            double inset = Math.Min(16, Math.Min(area.Width, area.Height) / 4);
            double width = Math.Min(requested.Width, Math.Max(1, area.Width - 2 * inset));
            double height = Math.Min(requested.Height, Math.Max(1, area.Height - 2 * inset));
            if (anchor.IsEmpty || !area.IntersectsWith(anchor)) { anchor = area; center = true; }
            double left = center ? anchor.Left + (anchor.Width - width) / 2 : anchor.Left;
            double top = center ? anchor.Top + (anchor.Height - height) / 2 : anchor.Top;
            return new Rect(Math.Max(area.Left + inset, Math.Min(left, area.Right - width - inset)),
                Math.Max(area.Top + inset, Math.Min(top, area.Bottom - height - inset)), width, height);
        }

        internal void Apply(Rect area, Rect anchor, bool center)
        {
            if (closed || area.IsEmpty || area.Width <= 0 || area.Height <= 0) return;
            Rect bounds = FitWithin(area, anchor, new Size(preferredWidth, preferredHeight), center);
            applying = true;
            try
            {
                window.MinWidth = Math.Min(minimumWidth, bounds.Width);
                window.MinHeight = Math.Min(minimumHeight, bounds.Height);
                if (window.WindowState == WindowState.Normal)
                {
                    window.Width = bounds.Width; window.Height = bounds.Height;
                    window.Left = bounds.Left; window.Top = bounds.Top;
                }
                placed = true;
            }
            finally { applying = false; }
        }
    }
}
