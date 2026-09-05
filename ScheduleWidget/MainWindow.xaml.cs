using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using FormsScreen = System.Windows.Forms.Screen;

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
        private string _startupWarningMessage;

        private AppearanceSettings _inlineSettingsOriginal;
        private AppearanceSettings _inlineSettingsDraft;
        private bool _inlineSettingsLoading;
        private bool _inlineStartupDraft;

        private Guid? _inlineEditId;
        private Guid? _pendingRemovalId;
        private bool _inlineEditDateSelectorsInitialized;
        private bool _inlineEditLoading;

        private string _pendingMonitorRestoreId;

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

            trayService.Initialize(this);

            SetTimerForMidnight();
            InitializeStateSaveTimer();
        }

        private void WidgetContent_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (WidgetContent.ActualWidth <= 0 || WidgetContent.ActualHeight <= 0)
                return;

            // Border의 CornerRadius는 자식 컨트롤을 자동으로 자르지 않으므로
            // 실제 둥근 사각형 클립을 적용해 모서리의 배경 누수를 막습니다.
            const double radius = 19.0;
            WidgetContent.Clip = new RectangleGeometry(
                new Rect(0, 0, WidgetContent.ActualWidth, WidgetContent.ActualHeight),
                radius,
                radius);
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e) => e.Cancel = true;

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

            if (dayChangeTimer != null)
                dayChangeTimer.Stop();

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

            EnsureWindowSizeWithin(workArea);

            if (double.IsNaN(Left) || double.IsInfinity(Left))
                Left = workArea.Left + (workArea.Width - Width) / 2;
            if (double.IsNaN(Top) || double.IsInfinity(Top))
                Top = workArea.Top + (workArea.Height - Height) / 2;

            double maxLeft = workArea.Right - Width;
            double maxTop = workArea.Bottom - Height;
            Left = Math.Max(workArea.Left, Math.Min(Left, maxLeft));
            Top = Math.Max(workArea.Top, Math.Min(Top, maxTop));
        }

        private void EnsureWindowSizeWithin(System.Windows.Rect workArea)
        {
            // 모니터보다 큰 저장 크기는 그대로 복원하지 않고 작업 영역에 맞춥니다.
            if (double.IsNaN(Width) || double.IsInfinity(Width) || Width <= 0)
                Width = Math.Min(300, workArea.Width);
            if (double.IsNaN(Height) || double.IsInfinity(Height) || Height <= 0)
                Height = Math.Min(400, workArea.Height);

            Width = Math.Min(Width, workArea.Width);
            Height = Math.Min(Height, workArea.Height);
        }

        public void ResetPositionToPrimaryMonitorCenter()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(ResetPositionToPrimaryMonitorCenter));
                return;
            }

            FormsScreen primaryScreen = FormsScreen.PrimaryScreen;
            if (primaryScreen == null)
                return;

            _isRestoringState = true;
            try
            {
                ApplyWindowPositionForMonitor(primaryScreen, null, false);

                if (!IsVisible)
                    Show();
            }
            finally
            {
                _isRestoringState = false;
            }

            SaveCurrentState();
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

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

            ApplyStartupPreferenceOnLoad();

            if (!string.IsNullOrWhiteSpace(_startupWarningMessage))
            {
                string warningMessage = _startupWarningMessage;
                _startupWarningMessage = null;
                System.Windows.MessageBox.Show(
                    this,
                    warningMessage + Environment.NewLine + "앱은 계속 실행되지만 Windows 로그인 시 자동으로 시작되지 않을 수 있습니다.",
                    "자동 시작 설정",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            RestoreWindowPlacementOnStartup();

            this.LocationChanged += (s, ev) => { if (!_isRestoringState) SaveCurrentState(); };
            this.SizeChanged += (s, ev) => { if (!_isRestoringState) SaveCurrentState(); };

            ApplyAppearance(appData.Appearance);
            RefreshScheduleList();

            // WPF가 먼저 표면을 렌더링한 다음 바탕화면 호스트에 연결합니다.
            // SourceInitialized 단계에서 바로 연결하면 layered window가
            // 셸 합성 화면에 그려지지 않는 경우가 있습니다.
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                NativeMethods.SetToDesktop(hwnd);
            EnsureVisibleOnScreen();

            SaveCurrentState();
        }

        private void ApplyStartupPreferenceOnLoad()
        {
            if (appData == null)
                return;

            string startupError;
            bool applied = appData.StartupEnabled
                ? startupService.TryEnableStartup(out startupError)
                : startupService.TryDisableStartup(out startupError);

            if (!applied)
                _startupWarningMessage = startupError;
        }

        private void RestoreWindowPlacementOnStartup()
        {
            if (appData == null || appData.WindowState == null)
                return;

            string savedMonitorId = appData.WindowState.MonitorId;
            if (!string.IsNullOrWhiteSpace(savedMonitorId))
            {
                FormsScreen targetScreen = FindScreenById(savedMonitorId);
                if (targetScreen == null)
                    targetScreen = FormsScreen.PrimaryScreen;

                if (targetScreen != null)
                {
                    MonitorStateData savedState = null;
                    bool hasSavedState =
                        string.Equals(targetScreen.DeviceName, savedMonitorId, StringComparison.OrdinalIgnoreCase) &&
                        TryGetMonitorState(savedMonitorId, out savedState);

                    _isRestoringState = true;
                    try
                    {
                        ApplyWindowPositionForMonitor(targetScreen, savedState, hasSavedState);
                        appData.WindowState.MonitorId = targetScreen.DeviceName;
                    }
                    finally { _isRestoringState = false; }
                }

                return;
            }

            // 모니터별 위치가 없는 구버전 데이터는 기존 전역 위치를 한 번만 복원합니다.
            if (appData.WindowState.Width > 0)
            {
                _isRestoringState = true;
                try
                {
                    Width = appData.WindowState.Width;
                    Height = appData.WindowState.Height;
                    Left = appData.WindowState.Left;
                    Top = appData.WindowState.Top;
                    EnsureVisibleOnScreen();
                }
                finally { _isRestoringState = false; }
            }
            else
            {
                EnsureVisibleOnScreen();
            }
        }

        private void ApplyWindowPositionForMonitor(
            FormsScreen screen,
            MonitorStateData savedState,
            bool restoreSavedPosition)
        {
            System.Windows.Rect workArea = GetWorkAreaInDips(screen);
            if (workArea.Width <= 0 || workArea.Height <= 0)
                return;

            if (savedState != null)
            {
                if (IsFinite(savedState.Width) && savedState.Width > 0)
                    Width = savedState.Width;
                if (IsFinite(savedState.Height) && savedState.Height > 0)
                    Height = savedState.Height;
            }
            else if (appData != null && appData.WindowState != null)
            {
                if (IsFinite(appData.WindowState.Width) && appData.WindowState.Width > 0)
                    Width = appData.WindowState.Width;
                if (IsFinite(appData.WindowState.Height) && appData.WindowState.Height > 0)
                    Height = appData.WindowState.Height;
            }

            EnsureWindowSizeWithin(workArea);

            if (restoreSavedPosition && savedState != null &&
                IsFinite(savedState.Left) && IsFinite(savedState.Top))
            {
                Left = savedState.Left;
                Top = savedState.Top;
                ClampWindowToWorkArea(workArea);
            }
            else
            {
                Left = workArea.Left + (workArea.Width - Width) / 2;
                Top = workArea.Top + (workArea.Height - Height) / 2;
            }
        }

        private void ClampWindowToWorkArea(System.Windows.Rect workArea)
        {
            double maxLeft = Math.Max(workArea.Left, workArea.Right - Width);
            double maxTop = Math.Max(workArea.Top, workArea.Bottom - Height);
            Left = Math.Max(workArea.Left, Math.Min(Left, maxLeft));
            Top = Math.Max(workArea.Top, Math.Min(Top, maxTop));
        }

        private System.Windows.Rect GetWorkAreaInDips(FormsScreen screen)
        {
            if (screen == null)
                return new System.Windows.Rect();

            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
            return new System.Windows.Rect(
                screen.WorkingArea.Left / scaleX,
                screen.WorkingArea.Top / scaleY,
                screen.WorkingArea.Width / scaleX,
                screen.WorkingArea.Height / scaleY);
        }

        private bool TryGetMonitorState(string monitorId, out MonitorStateData state)
        {
            state = null;
            if (string.IsNullOrWhiteSpace(monitorId) ||
                appData == null ||
                appData.WindowState == null ||
                appData.WindowState.MonitorStates == null)
                return false;

            if (!appData.WindowState.MonitorStates.TryGetValue(monitorId, out state) || state == null)
                return false;

            if (!IsFinite(state.Left) || !IsFinite(state.Top))
            {
                state = null;
                return false;
            }

            return true;
        }

        private string GetCurrentMonitorId()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                FormsScreen screen = hwnd != IntPtr.Zero ? FormsScreen.FromHandle(hwnd) : null;
                return screen == null ? null : screen.DeviceName;
            }
            catch
            {
                return null;
            }
        }

        private static FormsScreen[] GetConnectedScreens()
        {
            FormsScreen[] screens = FormsScreen.AllScreens ?? new FormsScreen[0];
            Array.Sort(
                screens,
                (left, right) => StringComparer.OrdinalIgnoreCase.Compare(
                    left == null ? null : left.DeviceName,
                    right == null ? null : right.DeviceName));
            return screens;
        }

        private static FormsScreen FindScreenById(string monitorId)
        {
            if (string.IsNullOrWhiteSpace(monitorId))
                return null;

            foreach (FormsScreen screen in GetConnectedScreens())
            {
                if (screen != null &&
                    string.Equals(screen.DeviceName, monitorId, StringComparison.OrdinalIgnoreCase))
                    return screen;
            }

            return null;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
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

            string monitorId = !string.IsNullOrWhiteSpace(_pendingMonitorRestoreId)
                ? _pendingMonitorRestoreId
                : GetCurrentMonitorId();
            if (string.IsNullOrWhiteSpace(monitorId))
                return;

            appData.WindowState.MonitorId = monitorId;
            if (appData.WindowState.MonitorStates == null)
                appData.WindowState.MonitorStates = new Dictionary<string, MonitorStateData>();

            appData.WindowState.MonitorStates[monitorId] = new MonitorStateData
            {
                Left = this.Left,
                Top = this.Top,
                Width = this.Width,
                Height = this.Height
            };
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
                int index = appData.Schedules.FindIndex(s => s.Id == item.Id);
                if (index < 0) return;

                OpenInlineEdit(item);
            }
        }

        private void OpenInlineEdit(ScheduleItem item)
        {
            if (item == null)
                return;

            if (InlineSettingsPanel.Visibility == Visibility.Visible)
                CloseInlineSettings(false);

            if (RemoveConfirmPanel.Visibility == Visibility.Visible)
                CloseRemoveConfirmation();

            if (!_inlineEditDateSelectorsInitialized)
                InitInlineEditDateSelectors();

            DateTime date;
            if (!DateTime.TryParseExact(
                    item.Period,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out date))
            {
                date = DateTime.Today;
            }

            _inlineEditId = item.Id;
            _inlineEditLoading = true;
            InlineEditTitleInput.Text = item.Title ?? string.Empty;
            SetInlineEditYear(date.Year);
            InlineEditMonthCombo.SelectedItem = date.Month;
            UpdateInlineEditDays(date.Year, date.Month);
            InlineEditDayCombo.SelectedItem = date.Day;
            _inlineEditLoading = false;

            InlineEditPanel.Visibility = Visibility.Visible;
            InlineEditTitleInput.Focus();
            InlineEditTitleInput.SelectAll();
        }

        private void InitInlineEditDateSelectors()
        {
            DateTime today = DateTime.Today;

            for (int year = today.Year - 5; year <= today.Year + 5; year++)
                InlineEditYearCombo.Items.Add(year);
            for (int month = 1; month <= 12; month++)
                InlineEditMonthCombo.Items.Add(month);

            _inlineEditLoading = true;
            SetInlineEditYear(today.Year);
            InlineEditMonthCombo.SelectedItem = today.Month;
            UpdateInlineEditDays(today.Year, today.Month);
            InlineEditDayCombo.SelectedItem = today.Day;
            _inlineEditLoading = false;

            InlineEditYearCombo.SelectionChanged += (s, e) => UpdateInlineEditDaysForCurrentSelection();
            InlineEditYearCombo.LostFocus += (s, e) => NormalizeInlineEditYear();
            InlineEditMonthCombo.SelectionChanged += (s, e) => UpdateInlineEditDaysForCurrentSelection();

            _inlineEditDateSelectorsInitialized = true;
        }

        private void UpdateInlineEditDays(int year, int month)
        {
            int previousDay = InlineEditDayCombo.SelectedItem is int selectedDay ? selectedDay : 1;
            int daysInMonth;
            try
            {
                daysInMonth = DateTime.DaysInMonth(year, month);
            }
            catch (ArgumentOutOfRangeException)
            {
                return;
            }

            InlineEditDayCombo.Items.Clear();
            for (int dayIndex = 1; dayIndex <= daysInMonth; dayIndex++)
                InlineEditDayCombo.Items.Add(dayIndex);

            InlineEditDayCombo.SelectedItem = Math.Min(previousDay, daysInMonth);
        }

        private void SetInlineEditYear(int year)
        {
            if (!InlineEditYearCombo.Items.Contains(year))
                InlineEditYearCombo.Items.Add(year);

            InlineEditYearCombo.SelectedItem = year;
            InlineEditYearCombo.Text = year.ToString(CultureInfo.InvariantCulture);
        }

        private void NormalizeInlineEditYear()
        {
            int year;
            if (TryGetInlineEditYear(out year))
                SetInlineEditYear(year);

            UpdateInlineEditDaysForCurrentSelection();
        }

        private void UpdateInlineEditDaysForCurrentSelection()
        {
            if (_inlineEditLoading)
                return;

            int year;
            if (TryGetInlineEditYear(out year) && InlineEditMonthCombo.SelectedItem is int month)
                UpdateInlineEditDays(year, month);
        }

        private bool TryGetInlineEditYear(out int year)
        {
            string yearText = InlineEditYearCombo.Text;
            if (string.IsNullOrWhiteSpace(yearText) && InlineEditYearCombo.SelectedItem != null)
                yearText = InlineEditYearCombo.SelectedItem.ToString();

            return int.TryParse(
                       yearText,
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out year)
                   && year >= DateTime.MinValue.Year
                   && year <= DateTime.MaxValue.Year;
        }

        private bool TryGetInlineEditDate(out DateTime selectedDate)
        {
            selectedDate = default(DateTime);

            int year;
            if (!TryGetInlineEditYear(out year) ||
                !(InlineEditMonthCombo.SelectedItem is int month) ||
                !(InlineEditDayCombo.SelectedItem is int day))
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

        private void InlineEditSaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_inlineEditId.HasValue)
                return;

            string title = InlineEditTitleInput.Text == null
                ? string.Empty
                : InlineEditTitleInput.Text.Trim();
            if (title.Length == 0)
                return;

            DateTime selectedDate;
            if (!TryGetInlineEditDate(out selectedDate))
            {
                System.Windows.MessageBox.Show(
                    this,
                    "유효한 날짜를 입력해 주세요.",
                    "날짜 확인",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            ScheduleItem item = appData.Schedules.Find(s => s.Id == _inlineEditId.Value);
            if (item == null)
            {
                CloseInlineEdit();
                return;
            }

            item.Title = title;
            item.Period = selectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            SaveDataSafely();
            RefreshScheduleList();
            CloseInlineEdit();
        }

        private void InlineEditCancelButton_Click(object sender, RoutedEventArgs e)
        {
            CloseInlineEdit();
        }

        private void InlineEditCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseInlineEdit();
        }

        private void CloseInlineEdit()
        {
            InlineEditPanel.Visibility = Visibility.Collapsed;
            _inlineEditId = null;
        }

        private void RemoveSchedule_Click(object sender, RoutedEventArgs e)
        {
            ScheduleItem item = GetScheduleItemFromContextMenu(sender);
            if (item != null)
            {
                int index = appData.Schedules.FindIndex(s => s.Id == item.Id);
                if (index < 0) return;

                _pendingRemovalId = item.Id;
                RemoveConfirmMessage.Text = $"'{item.Title}' 일정을 제거할까요?";
                RemoveConfirmPanel.Visibility = Visibility.Visible;
            }
        }

        private void RemoveConfirmApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_pendingRemovalId.HasValue)
            {
                CloseRemoveConfirmation();
                return;
            }

            int index = appData.Schedules.FindIndex(s => s.Id == _pendingRemovalId.Value);
            if (index >= 0)
            {
                appData.Schedules.RemoveAt(index);
                SaveDataSafely();
                RefreshScheduleList();
            }

            CloseRemoveConfirmation();
        }

        private void RemoveConfirmCancelButton_Click(object sender, RoutedEventArgs e)
        {
            CloseRemoveConfirmation();
        }

        private void CloseRemoveConfirmation()
        {
            RemoveConfirmPanel.Visibility = Visibility.Collapsed;
            _pendingRemovalId = null;
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
            CalendarPicker.SelectedDate = today;

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

            dayChangeTimer.Tick += (s, e) =>
            {
                if (appData != null)
                    RefreshScheduleList();

                SetNextMidnightInterval();
            };

            SetNextMidnightInterval();
            dayChangeTimer.Start();
        }

        private void SetNextMidnightInterval()
        {
            DateTime localMidnight = DateTime.SpecifyKind(
                DateTime.Now.Date.AddDays(1),
                DateTimeKind.Unspecified);
            TimeSpan localOffset = TimeZoneInfo.Local.GetUtcOffset(localMidnight);
            DateTimeOffset nextMidnight = new DateTimeOffset(localMidnight, localOffset);
            TimeSpan timeUntilMidnight = nextMidnight - DateTimeOffset.Now;

            dayChangeTimer.Interval = timeUntilMidnight > TimeSpan.Zero
                ? timeUntilMidnight
                : TimeSpan.FromSeconds(1);
        }

        private void MonitorButton_Click(object sender, RoutedEventArgs e)
        {
            if (MonitorPanel.Visibility == Visibility.Visible)
            {
                CloseMonitorPanel();
                return;
            }

            ModeToggle.IsChecked = false;
            OpenMonitorPanel();
        }

        private void OpenMonitorPanel()
        {
            MonitorOptionsList.ItemsSource = null;
            MonitorOptionsList.ItemsSource = BuildMonitorOptions();
            MonitorPanel.Visibility = Visibility.Visible;
        }

        private List<MonitorOption> BuildMonitorOptions()
        {
            var options = new List<MonitorOption>();
            string currentMonitorId = GetCurrentMonitorId();
            int monitorNumber = 1;

            foreach (FormsScreen screen in GetConnectedScreens())
            {
                if (screen == null || string.IsNullOrWhiteSpace(screen.DeviceName))
                    continue;

                string detail = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} × {1}",
                    screen.Bounds.Width,
                    screen.Bounds.Height);
                if (screen.Primary)
                    detail += " · 주 모니터";

                bool isCurrent = string.Equals(
                    screen.DeviceName,
                    currentMonitorId,
                    StringComparison.OrdinalIgnoreCase);
                options.Add(new MonitorOption
                {
                    Id = screen.DeviceName,
                    DisplayName = "모니터 " + monitorNumber,
                    Detail = detail,
                    StatusText = isCurrent ? "현재" : string.Empty
                });
                monitorNumber++;
            }

            return options;
        }

        private void MonitorOptionButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as System.Windows.Controls.Button;
            string monitorId = button == null ? null : button.Tag as string;
            if (string.IsNullOrWhiteSpace(monitorId))
                return;

            if (MoveToMonitor(monitorId))
                CloseMonitorPanel();
            else
                OpenMonitorPanel();
        }

        private void MonitorPanelCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseMonitorPanel();
        }

        private void CloseMonitorPanel()
        {
            MonitorPanel.Visibility = Visibility.Collapsed;
            MonitorOptionsList.ItemsSource = null;
        }

        private bool MoveToMonitor(string monitorId)
        {
            if (appData == null || appData.WindowState == null)
                return false;

            FormsScreen targetScreen = FindScreenById(monitorId);
            if (targetScreen == null)
                return false;

            // 현재 모니터를 다시 선택한 경우에는 부모 창과 좌표를 건드리지
            // 않습니다. 바탕화면 호스트를 다시 연결하면 셸 구성에 따라
            // 위젯이 잠시 숨겨질 수 있으므로, 불필요한 재배치를 차단합니다.
            string currentMonitorId = GetCurrentMonitorId();
            if (string.Equals(currentMonitorId, targetScreen.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                (string.IsNullOrWhiteSpace(currentMonitorId) &&
                 string.Equals(appData.WindowState.MonitorId, targetScreen.DeviceName, StringComparison.OrdinalIgnoreCase)))
            {
                appData.WindowState.MonitorId = targetScreen.DeviceName;
                SaveCurrentState();
                return true;
            }

            // 이동하기 전에 현재 모니터 위치를 먼저 별도 슬롯에 보존합니다.
            UpdateWindowStateData();

            MonitorStateData savedState;
            bool hasSavedState = TryGetMonitorState(monitorId, out savedState);
            _pendingMonitorRestoreId = targetScreen.DeviceName;

            _isRestoringState = true;
            try
            {
                ApplyWindowPositionForMonitor(targetScreen, savedState, hasSavedState);
                appData.WindowState.MonitorId = targetScreen.DeviceName;
            }
            finally
            {
                _isRestoringState = false;
            }

            SaveCurrentState();

            // 모니터별 DPI가 다르면 WPF가 위치 단위를 다시 계산하므로,
            // 레이아웃이 안정된 뒤 한 번 더 현재 위치를 저장합니다.
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (string.Equals(
                            _pendingMonitorRestoreId,
                            targetScreen.DeviceName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _pendingMonitorRestoreId = null;
                        SaveCurrentState();
                    }
                }),
                DispatcherPriority.ApplicationIdle);

            return true;
        }

        // ── 설정 ──

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (appData == null)
                return;

            if (InlineSettingsPanel.Visibility == Visibility.Visible)
            {
                CloseInlineSettings(false);
                return;
            }

            // 설정을 여는 동안에는 위젯 이동 모드를 잠시 끕니다.
            ModeToggle.IsChecked = false;

            _inlineSettingsOriginal = CloneAppearance(appData.Appearance);
            _inlineSettingsDraft = CloneAppearance(appData.Appearance);
            _inlineStartupDraft = appData.StartupEnabled;

            _inlineSettingsLoading = true;
            InlineOpacitySlider.Value = _inlineSettingsDraft.Opacity * 100;
            InlineTitleFontSizeSlider.Value = _inlineSettingsDraft.TitleFontSize;
            InlineDDayFontSizeSlider.Value = _inlineSettingsDraft.DDayFontSize;
            InlinePresetCombo.SelectedIndex = FindPresetIndex(_inlineSettingsDraft.ThemePreset);
            InlineStartupToggle.IsChecked = _inlineStartupDraft;
            UpdateInlineSettingsLabels();
            _inlineSettingsLoading = false;

            InlineSettingsPanel.Visibility = Visibility.Visible;
        }

        private static int FindPresetIndex(string preset)
        {
            switch (preset)
            {
                case "Dark": return 1;
                case "Blue": return 2;
                case "Pink": return 3;
                default: return 0;
            }
        }

        private static AppearanceSettings CloneAppearance(AppearanceSettings source)
        {
            var copy = new AppearanceSettings
            {
                Opacity = source.Opacity,
                ThemePreset = source.ThemePreset,
                TitleFontSize = source.TitleFontSize,
                DDayFontSize = source.DDayFontSize
            };
            copy.CopyColorsFrom(source);
            return copy;
        }

        private void InlinePresetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_inlineSettingsLoading || _inlineSettingsDraft == null || InlinePresetCombo.SelectedItem == null)
                return;

            string preset = (InlinePresetCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content.ToString();
            if (preset != null && AppearanceSettings.Presets.ContainsKey(preset))
            {
                _inlineSettingsDraft.ThemePreset = preset;
                _inlineSettingsDraft.CopyColorsFrom(AppearanceSettings.Presets[preset]);
                ApplyAppearance(_inlineSettingsDraft);
            }
        }

        private void InlineOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_inlineSettingsLoading || _inlineSettingsDraft == null)
                return;

            _inlineSettingsDraft.Opacity = InlineOpacitySlider.Value / 100.0;
            UpdateInlineSettingsLabels();
            ApplyAppearance(_inlineSettingsDraft);
        }

        private void InlineFontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_inlineSettingsLoading || _inlineSettingsDraft == null)
                return;

            _inlineSettingsDraft.TitleFontSize = InlineTitleFontSizeSlider.Value;
            _inlineSettingsDraft.DDayFontSize = InlineDDayFontSizeSlider.Value;
            UpdateInlineSettingsLabels();
            ApplyAppearance(_inlineSettingsDraft);
        }

        private void InlineStartupToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (!_inlineSettingsLoading)
                _inlineStartupDraft = true;
        }

        private void InlineStartupToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!_inlineSettingsLoading)
                _inlineStartupDraft = false;
        }

        private void UpdateInlineSettingsLabels()
        {
            if (InlineOpacityValueText != null)
                InlineOpacityValueText.Text = $"{(int)InlineOpacitySlider.Value}%";
            if (InlineTitleFontSizeText != null)
                InlineTitleFontSizeText.Text = $"{(int)InlineTitleFontSizeSlider.Value}";
            if (InlineDDayFontSizeText != null)
                InlineDDayFontSizeText.Text = $"{(int)InlineDDayFontSizeSlider.Value}";
        }

        private void InlineSettingsApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_inlineSettingsDraft == null)
                return;

            string startupError;
            bool startupApplied = _inlineStartupDraft
                ? startupService.TryEnableStartup(out startupError)
                : startupService.TryDisableStartup(out startupError);
            if (!startupApplied)
            {
                System.Windows.MessageBox.Show(
                    this,
                    startupError,
                    "자동 시작 설정",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            appData.Appearance = CloneAppearance(_inlineSettingsDraft);
            appData.StartupEnabled = _inlineStartupDraft;
            ApplyAppearance(appData.Appearance);
            SaveDataSafely();
            CloseInlineSettings(true);
        }

        private void InlineSettingsCancelButton_Click(object sender, RoutedEventArgs e)
        {
            CloseInlineSettings(false);
        }

        private void InlineSettingsCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseInlineSettings(false);
        }

        private void CloseInlineSettings(bool commit)
        {
            if (!commit && _inlineSettingsOriginal != null)
                ApplyAppearance(_inlineSettingsOriginal);

            InlineSettingsPanel.Visibility = Visibility.Collapsed;
            _inlineSettingsOriginal = null;
            _inlineSettingsDraft = null;
            _inlineStartupDraft = false;
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
