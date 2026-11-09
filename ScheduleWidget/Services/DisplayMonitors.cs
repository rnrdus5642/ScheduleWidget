using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace ScheduleWidget
{
    internal sealed class DisplayMonitor
    {
        internal string Id, Name, DeviceName;
        internal Rect BoundsPixels, WorkPixels;
        internal double ScaleX = 1, ScaleY = 1;
        internal bool Primary;
        internal Rect WorkArea => new Rect(WorkPixels.X / ScaleX, WorkPixels.Y / ScaleY,
            WorkPixels.Width / ScaleX, WorkPixels.Height / ScaleY);
        internal string Signature => Id + "|" + BoundsPixels.ToString(CultureInfo.InvariantCulture) + "|" +
            WorkPixels.ToString(CultureInfo.InvariantCulture) + "|" + ScaleX.ToString("R", CultureInfo.InvariantCulture) + "|" +
            ScaleY.ToString("R", CultureInfo.InvariantCulture) + "|" + Primary;
    }

    internal static class DisplayMonitors
    {
        private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            internal int Size;
            internal NativeRect Bounds, Work;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            internal int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceString;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceKey;
        }
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string device, uint number, ref DisplayDevice info, uint flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int kind, out uint x, out uint y);

        internal static List<DisplayMonitor> Read()
        {
            var result = new List<DisplayMonitor>();
            bool complete = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, dc, rect, parameter) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (!GetMonitorInfo(handle, ref info) || info.Work.Right <= info.Work.Left || info.Work.Bottom <= info.Work.Top) return true;
                string id = null, name = info.DeviceName;
                // EDD_GET_DEVICE_INTERFACE_NAME returns the monitor interface path, not the changeable DISPLAY1/2 slot.
                for (uint index = 0; index < 16; index++)
                {
                    var device = new DisplayDevice { Size = Marshal.SizeOf(typeof(DisplayDevice)) };
                    if (!EnumDisplayDevices(info.DeviceName, index, ref device, 1)) break;
                    if ((device.Flags & 1) == 0 || string.IsNullOrWhiteSpace(device.DeviceId)) continue;
                    id = "monitor:" + device.DeviceId.Trim().ToUpperInvariant();
                    name = string.IsNullOrWhiteSpace(device.DeviceString) ? name : device.DeviceString.Trim();
                    break;
                }
                // Some virtual/remote drivers expose no monitor interface. Keep a distinct, best-effort display slot.
                if (id == null) id = "display:" + (info.DeviceName ?? "").ToUpperInvariant();
                if (result.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))) id += "|" + info.DeviceName;
                double scaleX = 1, scaleY = 1;
                try
                {
                    if (GetDpiForMonitor(handle, 0, out uint x, out uint y) == 0 && x > 0 && y > 0)
                    { scaleX = x / 96.0; scaleY = y / 96.0; }
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
                result.Add(new DisplayMonitor { Id = id, Name = name, DeviceName = info.DeviceName,
                    BoundsPixels = RectOf(info.Bounds), WorkPixels = RectOf(info.Work), ScaleX = scaleX, ScaleY = scaleY, Primary = (info.Flags & 1) != 0 });
                return true;
            }, IntPtr.Zero);
            // An incomplete enumeration must not be interpreted as a monitor being unplugged.
            return complete ? result.OrderByDescending(m => m.Primary).ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<DisplayMonitor>();
        }

        private static Rect RectOf(NativeRect rect) => new Rect(rect.Left, rect.Top,
            Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
    }
}
