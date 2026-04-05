using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Forms;
using System.Windows.Media;
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

            NativeMethods.SetToDesktop(hwnd);

            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOOLWINDOW);
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            appData = dataManager.LoadData();

            if (appData.WindowState != null && appData.WindowState.Width > 0)
            {
                this.Width = appData.WindowState.Width;
                this.Height = appData.WindowState.Height;
                this.Left = appData.WindowState.Left;
                this.Top = appData.WindowState.Top;
            }

            this.LocationChanged += (s, ev) => SaveCurrentState();
            this.SizeChanged += (s, ev) => SaveCurrentState();

            ApplyAppearance(appData.Appearance);
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

        private void SetTimerForMidnight()
        {
            dayChangeTimer = new DispatcherTimer();

            DateTime now = DateTime.Now;
            DateTime tomorrow = now.Date.AddDays(1);
            TimeSpan timeUntilMidnight = tomorrow - now;

            dayChangeTimer.Interval = timeUntilMidnight;

            dayChangeTimer.Tick += (s, e) =>
            {
                RefreshScheduleList();

                if (dayChangeTimer.Interval != TimeSpan.FromHours(24))
                {
                    dayChangeTimer.Interval = TimeSpan.FromHours(24);
                }
            };

            dayChangeTimer.Start();
        }

        // ── 설정 ──

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var win = new SettingsWindow(appData.Appearance, ApplyAppearance);
            win.Owner = System.Windows.Application.Current.MainWindow;

            if (win.ShowDialog() == true)
            {
                appData.Appearance = win.Result;
                dataManager.SaveData(appData);
            }
        }

        private void ApplyAppearance(AppearanceSettings settings)
        {
            var res = System.Windows.Application.Current.Resources;

            SetBrush(res, "TopBarBrush", settings.TopBarColor);
            SetBrush(res, "BackgroundBrush", settings.BackgroundColor);
            SetBrush(res, "CardBrush", settings.CardColor);
            SetBrush(res, "CardBorderBrush", settings.CardBorderColor);
            SetBrush(res, "BottomBarBrush", settings.BottomBarColor);
            SetBrush(res, "TextBrush", settings.TextColor);
            SetBrush(res, "SubTextBrush", settings.SubTextColor);
            SetBrush(res, "BorderBrush", settings.BorderColor);

            res["TitleFontSize"] = settings.TitleFontSize;
            res["DDayFontSize"] = settings.DDayFontSize;

            MainBorder.Opacity = settings.Opacity;
        }

        private void SetBrush(ResourceDictionary res, string key, string color)
        {
            try
            {
                res[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            }
            catch { }
        }
    }
}
