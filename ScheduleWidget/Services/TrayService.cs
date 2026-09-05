using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScheduleWidget
{
    public class TrayService : IDisposable
    {
        private NotifyIcon trayIcon;

        // 시스템 아이콘을 추출하기 위한 WinAPI 선언
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern int ExtractIconEx(string stFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, int nIcons);

        public void Initialize(MainWindow window)
        {
            trayIcon = new NotifyIcon
            {
                // shell32.dll의 20번 아이콘(메모지와 펜) 추출 시도
                Icon = GetShell32Icon(20),
                Visible = true,
                Text = "일정 위젯"
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("열기", null, (s, e) => window.Show());

            menu.Items.Add("위치 초기화", null, (s, e) =>
            {
                window.ResetPositionToPrimaryMonitorCenter();
            });

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add("종료", null, (s, e) => {
                Dispose();
                System.Windows.Application.Current.Shutdown();
            });

            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += (s, e) => window.Show();
        }

        private Icon GetShell32Icon(int index)
        {
            try
            {
                IntPtr[] largeIcons = new IntPtr[1];
                IntPtr[] smallIcons = new IntPtr[1];

                // shell32.dll에서 아이콘 추출
                ExtractIconEx("shell32.dll", index, largeIcons, smallIcons, 1);

                if (smallIcons[0] != IntPtr.Zero)
                {
                    // 추출된 핸들로부터 아이콘 객체 생성
                    return Icon.FromHandle(smallIcons[0]);
                }
            }
            catch
            {
                // 실패 시 기본 애플리케이션 아이콘 반환
            }
            return SystemIcons.Application;
        }

        public void Dispose()
        {
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
        }
    }
}
