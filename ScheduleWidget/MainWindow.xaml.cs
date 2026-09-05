using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ScheduleWidget
{
    public partial class MainWindow : Window
    {
        private AppData appData;
        private readonly IAppDataStore dataStore = new DataManager();
        private readonly StartupService startupService = new StartupService();
        private readonly TrayService trayService = new TrayService();

        private DispatcherTimer dayChangeTimer;
        private DispatcherTimer stateSaveTimer;
        private DispatcherTimer displayRefreshTimer;

        private bool _isRestoringState;
        private bool _saveErrorShown;

        public MainWindow()
        {
            InitializeComponent();

            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            ModeToggle.IsChecked = false;
            this.ResizeMode = ResizeMode.NoResize;

            InitDateSelectors();
            startupService.EnableStartup();
            trayService.Initialize(this);

            SetTimerForMidnight();
            InitializeStateSaveTimer();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e) => e.Cancel = true;

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

            if (displayRefreshTimer != null)
                displayRefreshTimer.Stop();

            if (stateSaveTimer != null)
            {
                stateSaveTimer.Stop();
                if (appData != null)
                {
                    UpdateWindowStateData();
                    SaveDataSafely(false);
                }
            }
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            QueueDisplayRefresh();
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(QueueDisplayRefresh));
        }

        private void QueueDisplayRefresh()
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            if (displayRefreshTimer == null)
            {
                displayRefreshTimer = new DispatcherTimer
                {
                    // 모니터 구성과 DPI 변경 이벤트가 모두 끝난 뒤 한 번만 보정합니다.
                    Interval = TimeSpan.FromMilliseconds(350)
                };
                displayRefreshTimer.Tick += (s, e) =>
                {
                    displayRefreshTimer.Stop();
                    RefreshDesktopPlacement();
                };
            }

            displayRefreshTimer.Stop();
            displayRefreshTimer.Start();
        }

        private void RefreshDesktopPlacement()
        {
            if (!IsLoaded) return;

            _isRestoringState = true;
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    NativeMethods.SetToDesktop(hwnd);

                EnsureVisibleOnScreen();
            }
            finally { _isRestoringState = false; }

            SaveCurrentState();
        }

        private void EnsureVisibleOnScreen()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.Rect nativeWorkArea;
            System.Windows.Rect workArea;
            if (hwnd != IntPtr.Zero && NativeMethods.TryGetWorkArea(hwnd, out nativeWorkArea))
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
                double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
                workArea = new System.Windows.Rect(
                    nativeWorkArea.Left / scaleX,
                    nativeWorkArea.Top / scaleY,
                    (nativeWorkArea.Right - nativeWorkArea.Left) / scaleX,
                    (nativeWorkArea.Bottom - nativeWorkArea.Top) / scaleY);
            }
            else
            {
                // 화면 전환 중 Win32 모니터 정보가 잠시 unavailable하면 WPF 기본 영역을 사용합니다.
                workArea = SystemParameters.WorkArea;
            }

            if (workArea.Width <= 0 || workArea.Height <= 0)
                return;

            // 모니터보다 큰 저장 크기는 그대로 복원하지 않고 작업 영역에 맞춥니다.
            if (double.IsNaN(Width) || double.IsInfinity(Width) || Width <= 0)
                Width = Math.Min(300, workArea.Width);
            if (double.IsNaN(Height) || double.IsInfinity(Height) || Height <= 0)
                Height = Math.Min(400, workArea.Height);

            Width = Math.Min(Width, workArea.Width);
            Height = Math.Min(Height, workArea.Height);

            if (double.IsNaN(Left) || double.IsInfinity(Left))
                Left = workArea.Left + (workArea.Width - Width) / 2;
            if (double.IsNaN(Top) || double.IsInfinity(Top))
                Top = workArea.Top + (workArea.Height - Height) / 2;

            double maxLeft = workArea.Right - Width;
            double maxTop = workArea.Bottom - Height;
            Left = Math.Max(workArea.Left, Math.Min(Left, maxLeft));
            Top = Math.Max(workArea.Top, Math.Min(Top, maxTop));
        }

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
            try
            {
                DataLoadResult loadResult = dataStore.LoadData();
                appData = loadResult.Data;

                if (!string.IsNullOrWhiteSpace(loadResult.WarningMessage))
                {
                    System.Windows.MessageBox.Show(
                        this,
                        loadResult.WarningMessage,
                        "일정 데이터 복구",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (DataStorageException ex)
            {
                appData = new AppData();
                System.Windows.MessageBox.Show(
                    this,
                    ex.Message + Environment.NewLine + "이번 실행에서는 변경 사항이 저장되지 않을 수 있습니다.",
                    "일정 데이터 오류",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            if (appData.WindowState != null && appData.WindowState.Width > 0)
            {
                _isRestoringState = true;
                try
                {
                    this.Width = appData.WindowState.Width;
                    this.Height = appData.WindowState.Height;
                    this.Left = appData.WindowState.Left;
                    this.Top = appData.WindowState.Top;

                    EnsureVisibleOnScreen();
                }
                finally { _isRestoringState = false; }
            }

            this.LocationChanged += (s, ev) => { if (!_isRestoringState) SaveCurrentState(); };
            this.SizeChanged += (s, ev) => { if (!_isRestoringState) SaveCurrentState(); };

            ApplyAppearance(appData.Appearance);
            RefreshScheduleList();
        }

        private void SaveCurrentState()
        {
            if (appData == null || _isRestoringState) return;

            UpdateWindowStateData();
            stateSaveTimer.Stop();
            stateSaveTimer.Start();
        }

        private void InitializeStateSaveTimer()
        {
            stateSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            stateSaveTimer.Tick += (s, e) =>
            {
                stateSaveTimer.Stop();
                SaveDataSafely();
            };
        }

        private void UpdateWindowStateData()
        {
            appData.WindowState.Left = this.Left;
            appData.WindowState.Top = this.Top;
            appData.WindowState.Width = this.Width;
            appData.WindowState.Height = this.Height;
        }

        private bool SaveDataSafely(bool showError = true)
        {
            try
            {
                dataStore.SaveData(appData);
                _saveErrorShown = false;
                return true;
            }
            catch (DataStorageException ex)
            {
                if (showError && !_saveErrorShown)
                {
                    _saveErrorShown = true;
                    System.Windows.MessageBox.Show(
                        this,
                        ex.Message + Environment.NewLine + "변경 내용은 현재 실행 중에만 유지됩니다.",
                        "일정 데이터 저장 오류",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                return false;
            }
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

            DateTime selectedDate;
            if (!TryGetSelectedDate(out selectedDate))
            {
                System.Windows.MessageBox.Show(
                    this,
                    "유효한 날짜를 입력해 주세요.",
                    "날짜 확인",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string dateStr = selectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            appData.Schedules.Add(new ScheduleItem { Title = TitleInput.Text.Trim(), Period = dateStr });

            SaveDataSafely();
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

                SetYearSelection(selected.Year);

                MonthCombo.SelectedItem = selected.Month;

                UpdateDays(selected.Year, selected.Month);
                DayCombo.SelectedItem = selected.Day;
            }
        }

        private void ResetDateToToday()
        {
            DateTime today = DateTime.Now;

            SetYearSelection(today.Year);
            MonthCombo.SelectedItem = today.Month;

            UpdateDays(today.Year, today.Month);
            DayCombo.SelectedItem = today.Day;

            CalendarPicker.SelectedDate = today;
        }

        private void EditSchedule_Click(object sender, RoutedEventArgs e)
        {
            ScheduleItem item = GetScheduleItemFromContextMenu(sender);
            if (item != null)
            {
                int index = appData.Schedules.FindIndex(s => s.Title == item.Title && s.Period == item.Period);
                if (index < 0) return;

                var win = new EditScheduleWindow(item);
                win.Owner = System.Windows.Application.Current.MainWindow;

                if (win.ShowDialog() == true)
                {
                    appData.Schedules[index].Title = win.ResultTitle;
                    appData.Schedules[index].Period = win.ResultPeriod;
                    SaveDataSafely();
                    RefreshScheduleList();
                }
            }
        }

        private void RemoveSchedule_Click(object sender, RoutedEventArgs e)
        {
            ScheduleItem item = GetScheduleItemFromContextMenu(sender);
            if (item != null)
            {
                appData.Schedules.Remove(item);
                SaveDataSafely();
                RefreshScheduleList();
            }
        }

        private static ScheduleItem GetScheduleItemFromContextMenu(object sender)
        {
            var menuItem = sender as System.Windows.Controls.MenuItem;
            var contextMenu = menuItem?.Parent as System.Windows.Controls.ContextMenu;
            var placementTarget = contextMenu?.PlacementTarget as FrameworkElement;

            if (placementTarget?.DataContext is ScheduleItem item)
                return item;

            // 명시적인 ContextMenu 바인딩이 적용되지 않는 상황에서도 기존 동작을 유지합니다.
            return menuItem?.DataContext as ScheduleItem;
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

            YearCombo.SelectionChanged += (s, e) => UpdateDaysForCurrentSelection();
            YearCombo.LostFocus += (s, e) => NormalizeYearInput();
            MonthCombo.SelectionChanged += (s, e) => UpdateDaysForCurrentSelection();
        }

        private void UpdateDays(int year, int month)
        {
            DayCombo.Items.Clear();
            int days = DateTime.DaysInMonth(year, month);
            for (int d = 1; d <= days; d++) DayCombo.Items.Add(d);
            DayCombo.SelectedIndex = 0;
        }

        private void SetYearSelection(int year)
        {
            if (!YearCombo.Items.Contains(year))
                YearCombo.Items.Add(year);

            YearCombo.SelectedItem = year;
            YearCombo.Text = year.ToString(CultureInfo.InvariantCulture);
        }

        private void NormalizeYearInput()
        {
            int year;
            if (TryGetSelectedYear(out year))
                SetYearSelection(year);

            UpdateDaysForCurrentSelection();
        }

        private void UpdateDaysForCurrentSelection()
        {
            int year;
            if (TryGetSelectedYear(out year) && MonthCombo.SelectedItem is int month)
                UpdateDays(year, month);
        }

        private bool TryGetSelectedYear(out int year)
        {
            string yearText = YearCombo.Text;
            if (string.IsNullOrWhiteSpace(yearText) && YearCombo.SelectedItem != null)
                yearText = YearCombo.SelectedItem.ToString();

            return int.TryParse(
                       yearText,
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out year)
                   && year >= DateTime.MinValue.Year
                   && year <= DateTime.MaxValue.Year;
        }

        private bool TryGetSelectedDate(out DateTime selectedDate)
        {
            selectedDate = default(DateTime);

            int year;
            if (!TryGetSelectedYear(out year) ||
                !(MonthCombo.SelectedItem is int month) ||
                !(DayCombo.SelectedItem is int day))
                return false;

            try
            {
                selectedDate = new DateTime(year, month, day);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
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
                SaveDataSafely();
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
