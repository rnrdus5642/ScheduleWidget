using System;
using System.Runtime.InteropServices;

namespace ScheduleWidget
{
    internal static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll", SetLastError = true)] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", SetLastError = true)] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;

        public static void SetToDesktop(IntPtr hwnd)
        {
            IntPtr hProgman = FindWindow("Progman", null);
            IntPtr hShellDefView = FindWindowEx(hProgman, IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr hDesktop = FindWindowEx(hShellDefView, IntPtr.Zero, "SysListView32", null);

            if (hDesktop != IntPtr.Zero)
                SetParent(hwnd, hDesktop);
        }
    }
}