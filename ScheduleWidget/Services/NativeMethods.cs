using System;
using System.Runtime.InteropServices;

namespace ScheduleWidget
{
    internal static class NativeMethods
    {
        private const uint DesktopHostMessage = 0x052C;
        private const uint SmtoAbortIfHung = 0x0002;
        private const uint MonitorDefaultToNearest = 0x00000002;

        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_NOREPEAT = 0x4000;
        [DllImport("user32.dll")] public static extern IntPtr GetFocus();
        [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
        // PW_RENDERFULLCONTENT (2): the window's own content (DWM), even while other windows cover it.
        [DllImport("user32.dll", SetLastError = true)] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        /// <summary>
        /// True while DWM hides the window without it being "hidden" (another virtual desktop, a suspended app): its content
        /// is not composed then, so reading its pixels tells nothing.
        /// </summary>
        public static bool IsCloaked(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            try { return DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0; }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        /// <summary>The window's size in device pixels (this app is per-monitor DPI aware).</summary>
        public static bool TryGetWindowSize(IntPtr hwnd, out int width, out int height)
        {
            width = height = 0;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out Rect rect)) return false;
            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
            return true;
        }
        [DllImport("user32.dll", SetLastError = true)] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr value);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeout,
            out IntPtr result);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo monitorInfo);

        public const int GWL_EXSTYLE = -20;
        private const int GWL_HWNDPARENT = -8;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const int SW_SHOWNOACTIVATE = 4;

        public static bool SetToDesktop(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;

            IntPtr hProgman = FindWindow("Progman", null);
            if (hProgman == IntPtr.Zero) return false;

            // 바탕화면 아이콘 목록(SysListView32)이 아니라 아이콘 뒤의 WorkerW를 찾습니다.
            IntPtr result;
            SendMessageTimeout(
                hProgman,
                DesktopHostMessage,
                IntPtr.Zero,
                IntPtr.Zero,
                SmtoAbortIfHung,
                1000,
                out result);

            // Windows 구성에 따라 실제 바탕화면 WorkerW가 Progman의 자식으로
            // 만들어지는 경우가 있습니다. 먼저 이 호스트를 확인해야 숨겨진
            // 작은 WorkerW(IME/셸 보조 창)를 잘못 선택하지 않습니다.
            IntPtr desktopHost = FindWindowEx(
                hProgman,
                IntPtr.Zero,
                "WorkerW",
                null);
            if (!IsUsableDesktopHost(desktopHost))
                desktopHost = IntPtr.Zero;

            EnumWindows((topLevelWindow, lParam) =>
            {
                if (desktopHost != IntPtr.Zero)
                    return false;

                IntPtr shellView = FindWindowEx(
                    topLevelWindow,
                    IntPtr.Zero,
                    "SHELLDLL_DefView",
                    null);

                if (shellView != IntPtr.Zero)
                {
                    IntPtr childWorker = FindWindowEx(
                        topLevelWindow,
                        IntPtr.Zero,
                        "WorkerW",
                        null);
                    if (IsUsableDesktopHost(childWorker))
                    {
                        desktopHost = childWorker;
                        return false;
                    }

                    IntPtr siblingWorker = FindWindowEx(
                        IntPtr.Zero,
                        topLevelWindow,
                        "WorkerW",
                        null);
                    if (IsUsableDesktopHost(siblingWorker))
                    {
                        desktopHost = siblingWorker;
                        return false;
                    }
                }

                return true;
            }, IntPtr.Zero);

            if (desktopHost == IntPtr.Zero)
            {
                // WorkerW가 없는 Windows 구성에서는 아이콘 뷰를 fallback으로 사용합니다.
                // Progman 자체는 아이콘 뷰보다 뒤에 있어 자식 창이 보이지 않을 수 있습니다.
                desktopHost = FindWindowEx(
                    hProgman,
                    IntPtr.Zero,
                    "SHELLDLL_DefView",
                    null);
            }

            if (desktopHost == IntPtr.Zero)
                desktopHost = hProgman;

            if (!IsWindow(desktopHost)) return false;

            // WPF의 layered window를 WorkerW의 자식(WS_CHILD)으로 만들면
            // DWM 합성에서 투명 표면이 그려지지 않을 수 있습니다. 창은
            // popup 상태로 유지하고 바탕화면 호스트를 owner로 지정합니다.
            // 그러면 일반 프로그램 위로 올라가지 않으면서 Win+D에도 남습니다.
            SetWindowOwner(hwnd, desktopHost);
            SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
            return IsWindow(desktopHost);
        }

        /// <summary>
        /// Widget stacking. onTop = true: always above other windows (topmost).
        /// onTop = false (default): an ordinary window — it comes to the front when clicked and goes behind
        /// when another app is clicked, like any other app.
        /// </summary>
        public static void SetWidgetStacking(System.Windows.Window window, bool onTop)
        {
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            window.Topmost = onTop;
            SetWindowPos(handle, onTop ? HWND_TOPMOST_PTR : HWND_NOTOPMOST_PTR, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        /// <summary>
        /// Brings a non-pinned widget window to the top of the normal (non-topmost) windows without activating it.
        /// Needed because the widgets are owned by the desktop (so Win+D keeps them), and Windows does not raise a
        /// desktop-owned window on its own when it is clicked. Briefly entering and leaving the topmost band puts it
        /// above every ordinary app; clicking another app afterwards covers it again as usual. No-op when pinned topmost.
        /// </summary>
        public static void RaiseAboveOtherApps(System.Windows.Window window)
        {
            if (window == null || window.Topmost) return;
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            SetWindowPos(handle, HWND_TOPMOST_PTR, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetWindowPos(handle, HWND_NOTOPMOST_PTR, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private static readonly IntPtr HWND_TOPMOST_PTR = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST_PTR = new IntPtr(-2);

        private static IntPtr SetWindowOwner(IntPtr hwnd, IntPtr owner)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hwnd, GWL_HWNDPARENT, owner)
                : SetWindowLongPtr32(hwnd, GWL_HWNDPARENT, owner);
        }

        private static bool IsUsableDesktopHost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
                return false;

            Rect rect;
            if (!GetWindowRect(hwnd, out rect))
                return false;

            // 셸이 만드는 10~100px 크기의 보조 WorkerW는 바탕화면 호스트가
            // 아니므로 제외합니다. 숨김 상태인 정상 WorkerW도 허용합니다.
            return rect.Right - rect.Left >= 200 && rect.Bottom - rect.Top >= 200;
        }

        public static bool TryGetWorkArea(IntPtr hwnd, out Rect workArea)
        {
            workArea = default(Rect);
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;

            IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return false;

            var monitorInfo = new MonitorInfo
            {
                Size = Marshal.SizeOf(typeof(MonitorInfo))
            };

            if (!GetMonitorInfo(monitor, ref monitorInfo)) return false;

            workArea = new Rect
            {
                Left = monitorInfo.Work.Left,
                Top = monitorInfo.Work.Top,
                Right = monitorInfo.Work.Right,
                Bottom = monitorInfo.Work.Bottom
            };
            return true;
        }

        /// <summary>The window's rectangle in device pixels (where Windows really has it, whatever WPF's Left / Top say).</summary>
        public static bool TryGetWindowRect(IntPtr hwnd, out Rect rect)
        {
            rect = default(Rect);
            return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out rect);
        }

        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        /// <summary>
        /// The work areas (device pixels) of every monitor attached now; a monitor that reports no size (present but
        /// disconnected) is left out.
        /// </summary>
        public static System.Collections.Generic.List<Rect> GetWorkAreas()
        {
            var areas = new System.Collections.Generic.List<Rect>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, hdc, rect, data) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
                if (GetMonitorInfo(monitor, ref info) && info.Monitor.Right > info.Monitor.Left && info.Monitor.Bottom > info.Monitor.Top &&
                    info.Work.Right > info.Work.Left && info.Work.Bottom > info.Work.Top)
                    areas.Add(new Rect { Left = info.Work.Left, Top = info.Work.Top, Right = info.Work.Right, Bottom = info.Work.Bottom });
                return true;
            }, IntPtr.Zero);
            return areas;
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        /// <summary>The mouse pointer in device pixels.</summary>
        public static System.Drawing.Point CursorPosition()
        {
            return GetCursorPos(out NativePoint point) ? new System.Drawing.Point(point.X, point.Y) : System.Windows.Forms.Control.MousePosition;
        }

        /// <summary>
        /// The work area (device pixels) and scale (1 = 96 DPI) of the monitor at this point (the nearest one when the point
        /// is on none). The scale is 0 when the monitor's DPI could not be read.
        /// </summary>
        public static bool TryGetWorkAreaAt(System.Drawing.Point at, out Rect workArea, out double scale)
        {
            workArea = default(Rect);
            scale = 0;
            IntPtr monitor = MonitorFromPoint(new NativePoint { X = at.X, Y = at.Y }, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return false;
            var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfo(monitor, ref info)) return false;
            workArea = new Rect { Left = info.Work.Left, Top = info.Work.Top, Right = info.Work.Right, Bottom = info.Work.Bottom };
            try { if (GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out uint dpiY) == 0 && dpiX > 0) scale = dpiX / 96.0; }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return true;
        }

        private delegate bool EnumWindowsProc(IntPtr topLevelWindow, IntPtr lParam);

        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int size);

        private const int WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;

        /// <summary>
        /// Makes the browser process's own see-through top-level windows whose title contains <paramref name="titlePart"/>
        /// click-through (WS_EX_TRANSPARENT on top of their WS_EX_LAYERED). A WebView2 composition view keeps such a window
        /// over the view's area; when Windows raised it above the window showing the view (the desktop activated, …) it took
        /// every click there. Returns how many windows it changed.
        /// </summary>
        public static int MakeBrowserWindowsClickThrough(uint browserProcessId, string titlePart)
        {
            if (browserProcessId == 0 || string.IsNullOrEmpty(titlePart)) return 0;
            int changed = 0;
            EnumWindows((window, lParam) =>
            {
                if (GetWindowThreadProcessId(window, out uint pid) == 0 || pid != browserProcessId) return true;
                var name = new System.Text.StringBuilder(64);
                GetClassName(window, name, name.Capacity);
                if (name.ToString() != "Chrome_WidgetWin_1") return true;
                var title = new System.Text.StringBuilder(512);
                GetWindowText(window, title, title.Capacity);
                if (title.ToString().IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) < 0) return true;
                int style = GetWindowLong(window, GWL_EXSTYLE);
                if ((style & WS_EX_LAYERED) == 0 || (style & WS_EX_TRANSPARENT) != 0) return true;
                SetWindowLong(window, GWL_EXSTYLE, style | WS_EX_TRANSPARENT);
                changed++;
                return true;
            }, IntPtr.Zero);
            return changed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
