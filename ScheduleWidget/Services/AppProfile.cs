using System;
using System.IO;
using System.Linq;

namespace ScheduleWidget
{
    /// <summary>A separate, persistent data set for demonstrations; the same application and real Google APIs are used.</summary>
    internal static class AppProfile
    {
        internal const string DemoArgument = "--demo";
        internal static bool IsDemo { get; private set; }
        internal static string DataDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), IsDemo ? "ScheduleWidget.Demo" : "ScheduleWidget");

        // Run before WPF or any data-dependent service is initialized. Recovery arguments keep their original shape.
        internal static string[] Configure(string[] arguments)
        {
            arguments = arguments ?? Array.Empty<string>();
            IsDemo = arguments.Any(a => string.Equals(a, DemoArgument, StringComparison.OrdinalIgnoreCase));
            return arguments.Where(a => !string.Equals(a, DemoArgument, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        internal static void ApplyRuntimeSettings(AppData data)
        {
            if (!IsDemo || data == null) return;
            // Demonstrations must not change the normal Windows startup entry or own the user's global shortcut.
            data.StartupEnabled = false;
            data.BringToFrontHotKeyEnabled = false;
        }
    }
}
