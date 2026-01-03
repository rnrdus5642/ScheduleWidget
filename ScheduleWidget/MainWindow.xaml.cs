using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Newtonsoft.Json;
using Microsoft.Win32;
using System.Windows.Forms; // NotifyIcon, ContextMenuStrip
using System.Drawing;       // Icon

namespace ScheduleWidget
{
    public partial class MainWindow : Window
    {
        // 윈도우 핸들 찾기
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        // 하위 윈도우 핸들 찾기
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);

        // 부모 윈도우 설정
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        // 윈도우의 확장 스타일을 변경하는 함수
        [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        //GetWindowLong으로 가져온 창 스타일을 변경하는 함수 
        [DllImport("user32.dll", SetLastError = true)] static extern int GetWindowLong(IntPtr hWnd, int nIndex);  

        const int GWL_EXSTYLE = -20;                      // 창의 확장 스타일 값을 가져오거나 설정
        const int WS_EX_TOOLWINDOW = 0x00000080;          // ALT+TAB에 표시되지 않게 하는 플래그

        private string jsonPath;                          // Json 파일 경로
        private AppData appData = new AppData();          // 저장할 정보 데이터 객체
                                                          
        private const string AppName = "ScheduleWidget";  // 앱 이름
        private NotifyIcon trayIcon;                      // 시스템 트레이 아이콘

        private Screen currentScreen;

        // 초기화
        public MainWindow()
        {
            InitializeComponent(); // WPF 창 초기화

            // 일정 데이터 경로 불러오기
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            jsonPath = Path.Combine(exeDir, "schedules.json");


            SourceInitialized += MainWindow_SourceInitialized; // WinAPI를 사용할 수 있게 된 순간 호출
            Loaded += MainWindow_Loaded; // 창이 화면에 보이기 직전에 호출

            ModeToggle.IsChecked = false; // 창 변환 토글 스위치 비활성화
            this.ResizeMode = ResizeMode.NoResize; // 사용자가 창 크기를 변경하지 못하도록 막음

            InitDateSelectors();  // 날짜 선택 UI 초기화
            EnableStartup();      // 윈도우 시작 시 자동 실행 설정
            InitTrayIcon();       // 트레이 아이콘 생성 및 이벤트 등록
        }

        // 사용자가 창을 닫으려고 시도할 때 호출
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true; // 닫기 방지
        }

        // 창이 실제로 닫힌 이후 호출
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
        }

        //바탕화면을 부모로 지정
        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            foreach (var screen in Screen.AllScreens)
            {
                Console.WriteLine($"Device Name: {screen.DeviceName}");
                Console.WriteLine($"  Primary: {screen.Primary}");
                Console.WriteLine($"  Bounds: {screen.Bounds}");
                Console.WriteLine($"  Working Area: {screen.WorkingArea}");
                Console.WriteLine();
            }

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // 메인 모니터 작업 영역 가져오기
            var mainScreen = Screen.PrimaryScreen;
            var bounds = mainScreen.WorkingArea;

            // 창 위치를 메인 모니터 안으로 보정
            this.Left = bounds.Left;
            this.Top = bounds.Top;

            // 부모를 메인 모니터 바탕화면 SysListView32로 설정
            IntPtr hProgman = FindWindow("Progman", null);
            IntPtr hShellDefView = FindWindowEx(hProgman, IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr hDesktop = FindWindowEx(hShellDefView, IntPtr.Zero, "SysListView32", null);

            if (hDesktop != IntPtr.Zero)
                SetParent(hwnd, hDesktop);

            // Alt+Tab에서 숨기기
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
        }

        //창이 화면에 보이기 직전에 호출
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadAppData(); // Json데이터 불러오기

            HideFromAltTab(); // Alt+Tab에서 숨기기

            // 창 이동/크기 변경시 데이터 저장
            this.LocationChanged += (s, ev) => SaveAppData();
            this.SizeChanged += (s, ev) => SaveAppData();
        }

        //Alt+Tab에서 숨기기
        private void HideFromAltTab()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
        }
    
        // 트레이 아이콘
        private void InitTrayIcon()
        {
            trayIcon = new NotifyIcon();                   // 트레이 아이콘 객체 생성        
            trayIcon.Icon = SystemIcons.Application;       // 기본 아이콘 설정
            trayIcon.Visible = true;                       // 트레이에 실제 표시되도록 설정
            trayIcon.Text = "일정 위젯";                    // 마우스 오버 시 툴팁으로 표시될 텍스트

            var menu = new ContextMenuStrip();             // 아이콘 우클릭시 뜨는 메뉴
            menu.Items.Add("열기", null, (s, e) =>         
            {                                              
                this.Show();                               // 창 표시
            });                                            
            menu.Items.Add("종료", null, (s, e) => 
            {                                              
                trayIcon.Visible = false;                  // 아이콘을 트레이에서 제거
                trayIcon.Dispose();                        // 리소스 해제

                System.Windows.Application.Current.Shutdown(); // 프로그램 완전 종료
            });
            trayIcon.ContextMenuStrip = menu; // 메뉴를 트레이 아이콘에 연결

            // 사용자가 트레이 아이콘을 더블클릭시
            trayIcon.DoubleClick += (s, e) =>
            {
                this.Show();
            };
        }

        // 재부팅시 자동 시작 등록
        private void EnableStartup()
        {
            string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                key?.SetValue(AppName, exePath);
            }
        }

        // 재부팅시 자동 시작 등록 취소
        private void DisableStartup()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key?.GetValue(AppName) != null)
                {
                    key.DeleteValue(AppName, false); // 등록된 자동시작 값 삭제
                }
            }
        }

        // JSON 로드 / 저장
        private void LoadAppData()
        {
            if (File.Exists(jsonPath))
            {
                string json = File.ReadAllText(jsonPath);
                appData = JsonConvert.DeserializeObject<AppData>(json) ?? new AppData();
            }

            // 창 위치 복원
            if (appData.WindowState != null)
            {
                if (appData.WindowState.Width > 0) this.Width = appData.WindowState.Width;
                if (appData.WindowState.Height > 0) this.Height = appData.WindowState.Height;
                if (appData.WindowState.Left >= 0 && appData.WindowState.Top >= 0)
                {
                    this.Left = appData.WindowState.Left;
                    this.Top = appData.WindowState.Top;
                }
            }

            RefreshScheduleList();
        }

        private void SaveAppData()
        {
            // 현재 창 위치 저장
            appData.WindowState = new WindowStateData
            {
                Left = this.Left,
                Top = this.Top,
                Width = this.Width,
                Height = this.Height
            };

            string json = JsonConvert.SerializeObject(appData, Formatting.Indented);
            File.WriteAllText(jsonPath, json);
        }

        // 일정 추가 관련
        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TitleInput.Text))
                return;

            int year = (int)YearCombo.SelectedItem;
            int month = (int)MonthCombo.SelectedItem;
            int day = (int)DayCombo.SelectedItem;
            string dateStr = $"{year}-{month:D2}-{day:D2}";

            var newItem = new ScheduleItem
            {
                Title = TitleInput.Text.Trim(),
                Period = dateStr
            };

            appData.Schedules.Add(newItem);
            SaveAppData();
            RefreshScheduleList();

            TitleInput.Text = "";
        }

        // D-day기준으로 일정 정렬하기
        private void RefreshScheduleList()
        {
            var sorted = new List<ScheduleItem>(appData.Schedules);

            sorted.Sort((a, b) =>
            {
                bool aPast = a.RemainingDays < 0;
                bool bPast = b.RemainingDays < 0;

                if (aPast && !bPast) return -1;
                if (!aPast && bPast) return 1;

                if (aPast && bPast)
                    return a.RemainingDays.CompareTo(b.RemainingDays);

                return a.RemainingDays.CompareTo(b.RemainingDays);
            });

            ScheduleList.ItemsSource = null;
            ScheduleList.ItemsSource = sorted;
        }
        
        // 일정 제거 관련
        private void RemoveSchedule_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.MenuItem menuItem &&
                menuItem.DataContext is ScheduleItem item)
            {
                appData.Schedules.Remove(item);
                SaveAppData();
                RefreshScheduleList();
            }
        }

        // 창 드래그
        private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ModeToggle.IsChecked == true && e.LeftButton == MouseButtonState.Pressed)
                this.DragMove();
        }

        // 창 크기 조절 기능 켜기
        private void ModeToggle_Checked(object sender, RoutedEventArgs e)
        {
            this.ResizeMode = ResizeMode.CanResizeWithGrip;
        }

        // 창 크기 조절 기능 끄기
        private void ModeToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            this.ResizeMode = ResizeMode.NoResize;
        }

        // 날짜 선택 초기화
        private void InitDateSelectors()
        {
            DateTime today = DateTime.Now;

            for (int y = today.Year - 5; y <= today.Year + 5; y++)
                YearCombo.Items.Add(y);
            YearCombo.SelectedItem = today.Year;

            for (int m = 1; m <= 12; m++)
                MonthCombo.Items.Add(m);
            MonthCombo.SelectedItem = today.Month;

            UpdateDays(today.Year, today.Month);
            DayCombo.SelectedItem = today.Day;

            YearCombo.SelectionChanged += (s, e) =>
            {
                if (YearCombo.SelectedItem != null && MonthCombo.SelectedItem != null)
                    UpdateDays((int)YearCombo.SelectedItem, (int)MonthCombo.SelectedItem);
            };

            MonthCombo.SelectionChanged += (s, e) =>
            {
                if (YearCombo.SelectedItem != null && MonthCombo.SelectedItem != null)
                    UpdateDays((int)YearCombo.SelectedItem, (int)MonthCombo.SelectedItem);
            };
        }

        //날짜 업데이트
        private void UpdateDays(int year, int month)
        {
            DayCombo.Items.Clear();
            int days = DateTime.DaysInMonth(year, month);
            for (int d = 1; d <= days; d++)
                DayCombo.Items.Add(d);
            DayCombo.SelectedIndex = 0;
        }
    }
}
