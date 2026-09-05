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
        [DllImport("user32.dll", SetLastError = true)] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
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

        private delegate bool EnumWindowsProc(IntPtr topLevelWindow, IntPtr lParam);

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
