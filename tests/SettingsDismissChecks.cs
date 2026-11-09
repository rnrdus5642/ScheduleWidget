using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static void SettingsDismissalAudit()
        {
            Run("Settings retain child controls and owned pickers but dismiss for the calendar and other windows", () =>
            {
                using (var windows = new SettingsFocusWindows())
                {
                    var calendar = windows.Create();
                    var settings = windows.Create(calendar);
                    var control = windows.Create(settings, child: true);
                    var popup = windows.Create(settings);
                    var picker = windows.Create(popup);
                    var pickerControl = windows.Create(picker, child: true);
                    var unrelated = windows.Create();
                    var sibling = windows.Create(calendar);
                    foreach (var related in new[] { settings, control, popup, picker, pickerControl })
                        Require(!SettingsWindow.ShouldDismissForForeground(settings, related), "A settings control, dropdown or nested picker closed its owner.");
                    foreach (var outside in new[] { calendar, unrelated, sibling })
                        Require(SettingsWindow.ShouldDismissForForeground(settings, outside), "An outside window in the same app was mistaken for a settings dialog.");
                }
            });

            Run("Modal dialogs and transient foreground loss do not tear down settings", () =>
            {
                using (var windows = new SettingsFocusWindows())
                {
                    var settings = windows.Create();
                    var outside = windows.Create();
                    Require(!SettingsWindow.ShouldDismissForForeground(settings, IntPtr.Zero), "A transient activation gap dismissed settings.");
                    SettingsFocusWindows.EnableWindow(settings, false);
                    Require(!SettingsWindow.ShouldDismissForForeground(settings, outside), "A modal dialog's disabled owner was dismissed.");
                    SettingsFocusWindows.EnableWindow(settings, true);
                    Require(SettingsWindow.ShouldDismissForForeground(settings, outside), "Settings never resumed dismissal after the modal dialog ended.");
                    Require(!SettingsWindow.ShouldDismissForForeground(IntPtr.Zero, outside), "A missing settings window was accepted.");
                }
            });

            Run("Settings activation cancels delayed dismissal and closing stops future checks", () =>
            {
                var settings = new SettingsWindow();
                var timer = (DispatcherTimer)Field(settings, "dismissTimer");
                bool outside = false;
                int requests = 0;
                SetField(settings, "canDismissOverride", (Func<bool>)(() => outside));
                var frame = new DispatcherFrame();
                var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                deadline.Tick += (s, e) => frame.Continue = false;
                settings.CloseRequested += () => { requests++; settings.Close(); frame.Continue = false; };
                try
                {
                    Call(settings, "Settings_Deactivated", settings, EventArgs.Empty);
                    Call(settings, "RecheckDismissal", null, EventArgs.Empty);
                    Require(requests == 0 && timer.IsEnabled, "Settings closed while a related popup held focus.");
                    outside = true;
                    Call(settings, "Settings_Activated", settings, EventArgs.Empty);
                    Call(settings, "RecheckDismissal", null, EventArgs.Empty);
                    Require(requests == 0 && !timer.IsEnabled, "Returning to settings did not cancel the queued dismissal.");
                    Call(settings, "Settings_Deactivated", settings, EventArgs.Empty);
                    Require(requests == 0, "Settings closed inside the deactivation callback.");
                    deadline.Start();
                    Dispatcher.PushFrame(frame); // Real delayed callback, no visible windows or foreground changes.
                    Require(requests == 1 && !timer.IsEnabled, "Outside focus did not dismiss exactly once and release its timer.");
                    Call(settings, "Settings_Deactivated", settings, EventArgs.Empty);
                    Call(settings, "RecheckDismissal", null, EventArgs.Empty);
                    Require(requests == 1 && !timer.IsEnabled, "A late activation event revived a closed settings session.");
                    Require(!settings.IsVisible, "The isolated settings check displayed a window.");
                }
                finally { deadline.Stop(); settings.Close(); }
            });
        }

        private sealed class SettingsFocusWindows : IDisposable
        {
            private readonly List<IntPtr> windows = new List<IntPtr>();
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string title, uint style,
                int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
            [DllImport("user32.dll")] internal static extern bool EnableWindow(IntPtr window, bool enabled);
            [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);

            internal IntPtr Create(IntPtr owner = default(IntPtr), bool child = false)
            {
                var window = CreateWindowEx(0, "STATIC", "Isolated settings focus check", child ? 0x40000000u : 0x80000000u,
                    -32000, -32000, 80, 60, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Require(window != IntPtr.Zero, "Could not create a hidden focus-check window.");
                windows.Add(window);
                return window;
            }

            public void Dispose()
            {
                for (int i = windows.Count - 1; i >= 0; i--) DestroyWindow(windows[i]);
            }
        }
    }
}
