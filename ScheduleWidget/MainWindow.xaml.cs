using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Forms;
using System.Windows.Threading; 

namespace ScheduleWidget
{
    public partial class MainWindow : Window
    {
        private AppData appData;
        private readonly DataManager dataManager = new DataManager();
        private readonly TrayService trayService = new TrayService();

        private DispatcherTimer dayChangeTimer;

        public MainWindow()
        {
            InitializeComponent();

            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;

            ModeToggle.IsChecked = false;
            this.ResizeMode = ResizeMode.NoResize;

            InitDateSelectors();
            dataManager.EnableStartup();
            trayService.Initialize(this);

            SetTimerForMidnight();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e) => e.Cancel = true;

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // 바탕화면 고정
            NativeMethods.SetToDesktop(hwnd);

            // Alt+Tab 숨기기
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOOLWINDOW);
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            appData = dataManager.LoadData();

            // 위치 복원
            if (appData.WindowState != null && appData.WindowState.Width > 0)
            {
                this.Width = appData.WindowState.Width;
                this.Height = appData.WindowState.Height;
                this.Left = appData.WindowState.Left;
                this.Top = appData.WindowState.Top;
            }

            this.LocationChanged += (s, ev) => SaveCurrentState();
            this.SizeChanged += (s, ev) => SaveCurrentState();
            RefreshScheduleList();
        }

        private void SaveCurrentState()
        {
            appData.WindowState.Left = this.Left;
            appData.WindowState.Top = this.Top;
            appData.WindowState.Width = this.Width;
            appData.WindowState.Height = this.Height;
            dataManager.SaveData(appData);
        }

        private void RefreshScheduleList()
        {
            var sorted = new List<ScheduleItem>(appData.Schedules);
            sorted.Sort((a, b) =>
            {
                bool aPast = a.RemainingDays < 0;
                bool bPast = b.RemainingDays < 0;
                if (aPast && !bPast) return -1;
                if (!aPast && bPast) return 1;
                return a.RemainingDays.CompareTo(b.RemainingDays);
            });

            ScheduleList.ItemsSource = null;
            ScheduleList.ItemsSource = sorted;
        }

        // 엔터키를 눌렀을 때 일정 추가 함수
        private void TitleInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
                AddButton_Click(this, new RoutedEventArgs());
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TitleInput.Text)) return;

            string dateStr = $"{(int)YearCombo.SelectedItem}-{(int)MonthCombo.SelectedItem:D2}-{(int)DayCombo.SelectedItem:D2}";
            appData.Schedules.Add(new ScheduleItem { Title = TitleInput.Text.Trim(), Period = dateStr });

            dataManager.SaveData(appData);
            RefreshScheduleList();

            TitleInput.Text = "";
            ResetDateToToday();

            TitleInput.Focus();
        }

        private void CalendarPicker_SelectedDateChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (CalendarPicker.SelectedDate.HasValue)
            {
                DateTime selected = CalendarPicker.SelectedDate.Value;

                if (YearCombo.Items.Contains(selected.Year))
                    YearCombo.SelectedItem = selected.Year;

                MonthCombo.SelectedItem = selected.Month;

                UpdateDays(selected.Year, selected.Month);
                DayCombo.SelectedItem = selected.Day;
            }
        }

        private void ResetDateToToday()
        {
            DateTime today = DateTime.Now;

            YearCombo.SelectedItem = today.Year;
            MonthCombo.SelectedItem = today.Month;

            // 월이 바뀌면 일수도 달라지므로 UpdateDays 호출 후 일 설정
            UpdateDays(today.Year, today.Month);
            DayCombo.SelectedItem = today.Day;

            CalendarPicker.SelectedDate = today;
        }

        private void RemoveSchedule_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.MenuItem mi && mi.DataContext is ScheduleItem item)
            {
                appData.Schedules.Remove(item);
                dataManager.SaveData(appData);
                RefreshScheduleList();
            }
        }

        private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ModeToggle.IsChecked == true && e.LeftButton == MouseButtonState.Pressed) this.DragMove();
        }

        private void ModeToggle_Checked(object sender, RoutedEventArgs e) => this.ResizeMode = ResizeMode.CanResizeWithGrip;
        private void ModeToggle_Unchecked(object sender, RoutedEventArgs e) => this.ResizeMode = ResizeMode.NoResize;

        private void InitDateSelectors()
        {
            DateTime today = DateTime.Now;
            for (int y = today.Year - 5; y <= today.Year + 5; y++) YearCombo.Items.Add(y);
            for (int m = 1; m <= 12; m++) MonthCombo.Items.Add(m);

            YearCombo.SelectedItem = today.Year;
            MonthCombo.SelectedItem = today.Month;
            UpdateDays(today.Year, today.Month);
            DayCombo.SelectedItem = today.Day;

            YearCombo.SelectionChanged += (s, e) => UpdateDays((int)YearCombo.SelectedItem, (int)MonthCombo.SelectedItem);
            MonthCombo.SelectionChanged += (s, e) => UpdateDays((int)YearCombo.SelectedItem, (int)MonthCombo.SelectedItem);
        }

        private void UpdateDays(int year, int month)
        {
            DayCombo.Items.Clear();
            int days = DateTime.DaysInMonth(year, month);
            for (int d = 1; d <= days; d++) DayCombo.Items.Add(d);
            DayCombo.SelectedIndex = 0;
        }


        // 자정까지 남은 시간 계산 후 갱신해주는 함수
        private void SetTimerForMidnight()
        {
            dayChangeTimer = new DispatcherTimer();

            DateTime now = DateTime.Now;
            DateTime tomorrow = now.Date.AddDays(1);
            TimeSpan timeUntilMidnight = tomorrow - now;

            // 1. 첫 번째 인터벌: 현재부터 자정까지 남은 시간
            dayChangeTimer.Interval = timeUntilMidnight;

            dayChangeTimer.Tick += (s, e) =>
            {
                // 자정이 되면 리스트 갱신
                RefreshScheduleList();

                // 2. 이후 인터벌: 자정부터는 24시간마다 한 번씩 실행
                if (dayChangeTimer.Interval != TimeSpan.FromHours(24))
                {
                    dayChangeTimer.Interval = TimeSpan.FromHours(24);
                }
            };

            dayChangeTimer.Start();
        }
    }
}
