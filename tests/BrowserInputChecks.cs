using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static void BrowserInputSurfaces()
        {
            var native = typeof(App).Assembly.GetType("ScheduleWidget.NativeMethods", true);
            var repair = native.GetMethod("MakePetBrowserWindowClickThrough", BindingFlags.NonPublic | BindingFlags.Static);
            uint process = (uint)Process.GetCurrentProcess().Id;
            const string petPage = "character.html?mini";
            using (var surface = new BrowserSurfaceFixture())
            {
                IntPtr blank = surface.Create("about:blank");
                bool Repair(IntPtr window, uint pid) => (bool)repair.Invoke(null, new object[] { window, pid, petPage });
                Require(Repair(blank, process), "An invisible about:blank browser surface can still intercept desktop icons.");
                Require(BrowserSurfaceFixture.Style(blank) == (BrowserSurfaceFixture.SurfaceStyle | 0x20),
                    "Repair changed native styles other than click-through.");
                Require(!Repair(blank, process), "An already protected surface was rewritten.");

                void Rejected(string title, byte alpha, int style, uint pid, string reason)
                {
                    BrowserSurfaceFixture.Reset(blank, title, alpha, style);
                    Require(!Repair(blank, pid) && BrowserSurfaceFixture.Style(blank) == style, reason);
                }
                Rejected("about:blank", 1, BrowserSurfaceFixture.SurfaceStyle, process, "A visible blank page lost input.");
                Rejected("about:blank", 255, BrowserSurfaceFixture.SurfaceStyle, process, "An opaque blank page lost input.");
                Rejected("schedulewidget.example/character.html?gallery", 0, BrowserSurfaceFixture.SurfaceStyle, process,
                    "The interactive character gallery lost input.");
                Rejected("about:blank", 0, BrowserSurfaceFixture.SurfaceStyle & ~0x08000000, process,
                    "An activatable browser window lost input.");
                Rejected("about:blank", 0, BrowserSurfaceFixture.SurfaceStyle & ~0x80000, process,
                    "A non-layered browser window lost input.");
                Rejected("about:blank", 0, BrowserSurfaceFixture.SurfaceStyle, uint.MaxValue,
                    "A window outside the requested browser process was changed.");
                IntPtr otherClass = surface.Create("about:blank", "STATIC");
                Require(!Repair(otherClass, process), "An unrelated native window class was changed.");

                BrowserSurfaceFixture.Reset(blank, "schedulewidget.example/" + petPage, 0, BrowserSurfaceFixture.SurfaceStyle);
                Require(Repair(blank, process), "The named pet surface no longer receives click-through protection.");
            }
        }

        private static void BrowserInputGuardRecovery()
        {
            uint process = (uint)Process.GetCurrentProcess().Id;
            var guardType = typeof(App).Assembly.GetType("ScheduleWidget.BrowserWindowInputGuard", true);
            var constructor = guardType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(uint), typeof(string), typeof(Dispatcher), typeof(Action<int>) }, null);
            using (var surface = new BrowserSurfaceFixture())
            {
                IntPtr first = surface.Create("about:blank");
                IntPtr gallery = surface.Create("schedulewidget.example/character.html?gallery");
                int repaired = 0;
                var guard = (IDisposable)constructor.Invoke(new object[] { process, "character.html?mini",
                    Dispatcher.CurrentDispatcher, (Action<int>)(count => repaired += count) });
                try
                {
                    Require((BrowserSurfaceFixture.Style(first) & 0x20) != 0, "The guard missed a pre-existing blank surface.");
                    Require(BrowserSurfaceFixture.Style(gallery) == BrowserSurfaceFixture.SurfaceStyle, "The guard changed the gallery.");
                    IntPtr replacement = surface.Create("about:blank");
                    BrowserSurfaceFixture.Reset(first, "about:blank", 0, BrowserSurfaceFixture.SurfaceStyle);
                    Call(guard, "Verify");
                    Require((BrowserSurfaceFixture.Style(first) & 0x20) != 0 && (BrowserSurfaceFixture.Style(replacement) & 0x20) != 0,
                        "A replacement surface or a runtime style reset lost click-through protection.");
                    Require(repaired == 3, "The guard did not report the three repaired surfaces.");
                    guard.Dispose();
                    BrowserSurfaceFixture.Reset(first, "about:blank", 0, BrowserSurfaceFixture.SurfaceStyle);
                    Call(guard, "Verify");
                    Require(BrowserSurfaceFixture.Style(first) == BrowserSurfaceFixture.SurfaceStyle, "A disposed guard continued changing windows.");
                }
                finally { guard.Dispose(); }
            }
        }

        // Native windows belong only to this test process. They are never shown and remain off screen;
        // no real browser, desktop window, foreground state, or user input is changed.
        private sealed class BrowserSurfaceFixture : IDisposable
        {
            internal const int SurfaceStyle = 0x08080080; // NOACTIVATE | LAYERED | TOOLWINDOW
            private const string ClassName = "Chrome_WidgetWin_1";
            private static readonly WindowProc Procedure = DefWindowProc;
            private readonly List<IntPtr> windows = new List<IntPtr>();
            private readonly IntPtr instance = GetModuleHandle(null);

            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            private delegate IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct WindowClass
            {
                public uint Style;
                public IntPtr Procedure;
                public int ClassExtra, WindowExtra;
                public IntPtr Instance, Icon, Cursor, Background;
                public string Menu, Name;
            }
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass value);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string title, uint style,
                int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
            [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
            [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
            [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, int value);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr window, string value);
            [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);

            public BrowserSurfaceFixture()
            {
                var windowClass = new WindowClass { Name = ClassName, Instance = instance,
                    Procedure = Marshal.GetFunctionPointerForDelegate(Procedure) };
                Require(RegisterClass(ref windowClass) != 0, "Could not register the isolated browser-window fixture.");
            }

            internal IntPtr Create(string title, string className = ClassName)
            {
                var window = CreateWindowEx(SurfaceStyle, className, title, 0x80000000, -32000, -32000, 120, 90,
                    IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                Require(window != IntPtr.Zero, "Could not create the isolated browser-window fixture.");
                windows.Add(window);
                Require(SetLayeredWindowAttributes(window, 0, 0, 2), "Could not initialize the transparent browser surface.");
                return window;
            }

            internal static int Style(IntPtr window) => GetWindowLong(window, -20);
            internal static void Reset(IntPtr window, string title, byte alpha, int style)
            {
                SetWindowText(window, title);
                SetWindowLong(window, -20, style);
                if ((style & 0x80000) != 0)
                    Require(SetLayeredWindowAttributes(window, 0, alpha, 2), "Could not change the fixture's opacity.");
            }

            public void Dispose()
            {
                foreach (IntPtr window in windows) DestroyWindow(window);
                UnregisterClass(ClassName, instance);
            }
        }
    }
}
