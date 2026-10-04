using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        private int _inlineCharacterScaleDraft = 100;
        private bool _inlineCharacterScaleTouched; // the size slider was moved in this settings session
        // 미니 창에 캐릭터 표시 was clicked in this settings session. Untouched, 저장 / preview leave the pets' visibility
        // alone (it may have been changed meanwhile in 캐릭터 설정 — the panel's value would be stale).
        private bool _inlineCharacterVisibleTouched;

        private Guid? _inlineEditId;
        private Guid? _pendingRemovalId;
        private bool _inlineEditDateSelectorsInitialized;
        private bool _inlineEditLoading;

        private string _pendingMonitorRestoreId;

        // The card list is built only while this window can be seen: a change made while it is hidden (mini mode, tray)
        // marks it dirty and it is rebuilt when the window shows again. The signature skips rebuilding identical cards.
        private bool _scheduleListDirty;
        private string _scheduleListSignature;
        private string _themedPreset;       // the theme the windows were last recolored for
        private bool _themeApplied;
        private DateTime _addFormDate;       // the date the add form was last set to (today at that time)
        private bool _startingHidden;        // started in mini mode: this window stays invisible until it is hidden
        private bool _reattachWhenShown;     // the desktop host changed while this window was hidden
        // schedules.json could not be read at start (in use by another program, or unreachable): nothing is ever saved in
        // this run, so no empty data can replace the real file; the app closes.
        private bool _dataUnavailable;
        internal Action<string> loadFailedOverride = null; // checks: stands in for that message and the exit (both need the real app)

        /// <summary>The TODO window has finished loading (the app's error handler keeps the app running from then on).</summary>
        public bool StartupCompleted { get; private set; }

        public MainWindow()
        {
            InitializeComponent();
            Opacity = 0; // This window bootstraps the app; only the calendar is shown at startup.
            // The 종료 날짜 pickers (여러 날) open the same themed calendar as the add form's date button.
            AddEndPicker.Resources = CalendarPicker.Resources;
            InlineEditEndPicker.Resources = CalendarPicker.Resources;

            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;
            // Desktop-owned (survives Win+D) windows are not raised by Windows on click; raise like a normal app.
            PreviewMouseDown += (s, e) => NativeMethods.RaiseAboveOtherApps(this);
            Activated += (s, e) => NativeMethods.RaiseAboveOtherApps(this);
            Closed += MainWindow_Closed;
            WatchCodexPets();
            IsVisibleChanged += (s, e) =>
            {
                if (!IsVisible) return;
                if (_scheduleListDirty) RefreshScheduleList();
                if (_reattachWhenShown)
                {
                    _reattachWhenShown = false;
                    Dispatcher.BeginInvoke(new Action(ReattachToDesktop), DispatcherPriority.Background);
                }
            };

            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.TimeChanged += OnSystemTimeChanged; // clock or time zone changed: "today" may be another day

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

        // The TODO window is never closed on its own (종료 ends the app). Alt+F4 or another close request hides it to the
        // tray like Esc; deferred, so a close that can't be refused (app shutdown, Windows sign-out) just goes ahead.
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            if (!closingApp) Dispatcher.BeginInvoke(new Action(() => { if (!closingApp) HideScheduleList(); }));
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            closingApp = true;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.TimeChanged -= OnSystemTimeChanged;
            reminderTimer?.Stop();
            StopUpdateNotice();
            miniWindow?.Close();
            musicWindow?.Close();
            trayService.Dispose();

            if (dayChangeTimer != null)
                dayChangeTimer.Stop();

            if (displayRefreshTimer != null)
                displayRefreshTimer.Stop();
            explorerRestartTimer?.Stop();

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
                // SetToDesktop also shows the window, so a hidden one (mini mode, tray) is re-attached when it shows again.
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero && IsVisible)
                    NativeMethods.SetToDesktop(hwnd);
                else if (hwnd != IntPtr.Zero)
                    _reattachWhenShown = true;

                EnsureVisibleOnScreen();
            }
            finally { _isRestoringState = false; }

            SaveCurrentState();
        }

        // Explorer restarted (crash or update): its desktop window, which owns the widgets so they survive Win+D, is new.
        // Hand the visible widgets to it again; a hidden one is re-attached when it is shown.
        private static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        private DispatcherTimer explorerRestartTimer;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string message);

        private void OnExplorerRestarted()
        {
            if (explorerRestartTimer == null)
            {
                // The new desktop window appears a moment after the taskbar does.
                explorerRestartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                explorerRestartTimer.Tick += (s, e) => { explorerRestartTimer.Stop(); ReattachToDesktop(); };
            }
            explorerRestartTimer.Stop();
            explorerRestartTimer.Start();
        }

        private void ReattachToDesktop()
        {
            if (closingApp || !IsLoaded || appData == null) return;
            if (IsVisible)
            {
                RefreshDesktopPlacement();
                NativeMethods.SetWidgetStacking(this, appData.AlwaysOnTop);
            }
            else _reattachWhenShown = true;
            // The mini window (not its pets beside this window: those are owned by this window); hidden, on its next show.
            miniWindow?.ReattachToDesktop();
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

            // Reset the primary calendar and keep an open list next to it.
            if (appData != null)
            {
                ShowMiniWindow();
                if (miniWindow == null) return;
                miniWindow.CenterOnPrimaryScreen(); // and saves that place (window and calendar board)
                BringToFront(miniWindow);
                if (IsVisible && !_startingHidden) ShowFullWindow();
                return;
            }

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
            HwndSource.FromHwnd(hwnd)?.AddHook(BringToFrontHotKeyHook);
            Closed += (s, args) => UnregisterBringToFrontHotKey();
        }

        // 단축키 (default Ctrl+G, set in 설정) anywhere: bring the calendar and any open schedule list above every
        // window once. It is not pinned: the 모든 창 위에 표시 setting is kept, so clicking another app covers it again.
        private const int BringToFrontHotKeyId = 0x5347;
        private bool _bringToFrontHotKey;

        private void UnregisterBringToFrontHotKey()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (_bringToFrontHotKey && hwnd != IntPtr.Zero) NativeMethods.UnregisterHotKey(hwnd, BringToFrontHotKeyId);
            _bringToFrontHotKey = false;
        }

        /// <summary>Registers the saved shortcut (or none when turned off). False when another app already owns it.</summary>
        private bool ApplyBringToFrontHotKey()
        {
            UnregisterBringToFrontHotKey();
            var hwnd = new WindowInteropHelper(this).Handle;
            if (appData == null || !appData.BringToFrontHotKeyEnabled || hwnd == IntPtr.Zero) return true;
            var gesture = HotKeyGesture.ParseOrDefault(appData.BringToFrontHotKey);
            _bringToFrontHotKey = NativeMethods.RegisterHotKey(hwnd, BringToFrontHotKeyId,
                gesture.NativeModifiers | NativeMethods.MOD_NOREPEAT, gesture.VirtualKey);
            return _bringToFrontHotKey;
        }

        private IntPtr BringToFrontHotKeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == BringToFrontHotKeyId)
            {
                handled = true;
                if (appData != null && !closingApp) OpenLastWindow();
            }
            else if (msg == TaskbarCreatedMessage && TaskbarCreatedMessage != 0)
                OnExplorerRestarted(); // not handled: other listeners (the tray icon) need it too
            return IntPtr.Zero;
        }

        // Reads schedules.json (a recovery from the backup is reported). False when it could not be read at all — another
        // program has it open, or the folder can't be reached: running on with empty data would write that over the real
        // file at the next save (a window move is enough), so the app says so, logs it and closes without saving.
        internal bool LoadAppData()
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
                return true;
            }
            catch (DataStorageException ex)
            {
                _dataUnavailable = true;
                appData = null;
                ErrorLog.Write("load", ex);
                string message = ex.Message + Environment.NewLine + Environment.NewLine + "앱을 종료합니다. 일정 파일은 바꾸지 않았습니다.";
                if (loadFailedOverride != null) { loadFailedOverride(message); return false; }
                System.Windows.MessageBox.Show(
                    this,
                    message,
                    "일정 데이터 오류",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                ExitApplication();
                return false;
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (StartupCompleted) return; // Reopening the list must not reload data or restart application services.
            if (!LoadAppData()) return; // could not read the schedules: the app closes without saving anything

            // The calendar is always primary, including when loading an older file that selected the TODO window.
            _startingHidden = true;
            Opacity = 0;
            string loadedPlacement = PlacementSignature();

            ApplyStartupPreferenceOnLoad();
            ApplyBringToFrontHotKey(); // quietly off when another app owns the shortcut; 설정 shows it
            StartGoogleCalendar();
            StartUpdateNoticeChecks(); // 업데이트 알림: only with an update repository set in code

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

            this.LocationChanged += (s, ev) => { if (!_isRestoringState) SaveCurrentState(); FollowPetCompanion(); };
            this.SizeChanged += (s, ev) => { if (!_isRestoringState) SaveCurrentState(); FollowPetCompanion(); };
            this.IsVisibleChanged += (s, ev) => UpdatePetCompanion(); // the pets beside this window come and go with it
            UpdatePetCompanion();

            ApplyAppearance(appData.Appearance);
            RefreshScheduleList();
            InitializeReminders();

            // WPF가 먼저 표면을 렌더링한 다음 바탕화면 호스트에 연결합니다.
            // SourceInitialized 단계에서 바로 연결하면 layered window가
            // 셸 합성 화면에 그려지지 않는 경우가 있습니다.
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                NativeMethods.SetToDesktop(hwnd);
            NativeMethods.SetWidgetStacking(this, appData.AlwaysOnTop); // behind other apps unless 모든 창 위에 표시
            EnsureVisibleOnScreen();

            // Save the window's place only when starting up changed it (a restored or moved-on-screen window).
            UpdateWindowStateData();
            if (PlacementSignature() != loadedPlacement)
                SaveCurrentState();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    ShowMiniWindow();
                    Hide();
                }
                finally
                {
                    // If calendar startup failed, leave a usable list as a fallback.
                    _startingHidden = false;
                    Opacity = 1;
                    if (IsVisible) { RefreshScheduleList(); UpdatePetCompanion(); }
                }
            }), DispatcherPriority.Loaded);

            // 제목이 "YouTube · 영상ID"로 남아 있는 곡은 실제 영상 제목으로 채웁니다(인터넷 필요, 백그라운드).
            Dispatcher.BeginInvoke(new Action(async () => await FillYouTubeTitlesAsync()), DispatcherPriority.ApplicationIdle);
            StartupCompleted = true;
        }

        // Where this window is saved to be (its place and size, per monitor), to tell whether starting up moved it.
        private string PlacementSignature()
        {
            var state = appData?.WindowState;
            if (state == null) return string.Empty;
            var text = new System.Text.StringBuilder();
            text.Append(state.MonitorId).Append('|').Append(state.Left).Append(',').Append(state.Top).Append(',')
                .Append(state.Width).Append(',').Append(state.Height);
            if (state.MonitorStates != null)
                foreach (var pair in state.MonitorStates)
                    if (pair.Value != null)
                        text.Append('|').Append(pair.Key).Append(':').Append(pair.Value.Left).Append(',').Append(pair.Value.Top)
                            .Append(',').Append(pair.Value.Width).Append(',').Append(pair.Value.Height);
            return text.ToString();
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
            DpiScale before = VisualTreeHelper.GetDpi(this);
            PlaceOnMonitor(screen, savedState, restoreSavedPosition);
            // Mixed DPI: landing on a monitor with other scaling switches this window to that scaling on the way (it is
            // rescaled, and Left/Top now count in the new scale). Place it once more so it ends up where it was meant to.
            DpiScale after = VisualTreeHelper.GetDpi(this);
            if (after.DpiScaleX != before.DpiScaleX || after.DpiScaleY != before.DpiScaleY)
                PlaceOnMonitor(screen, savedState, restoreSavedPosition);
        }

        private void PlaceOnMonitor(
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

        // In this window's current scale, which is what Left/Top are read in right now (per-monitor DPI). After a move to a
        // monitor with other scaling the scale changes; ApplyWindowPositionForMonitor then places the window again.
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
            if (_dataUnavailable || appData == null) return false; // the data could not be read at start: never write over it
            try
            {
                dataStore.SaveData(appData);
                _saveErrorShown = false;
                NoteSchedulesSaved();
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
            if (appData == null) { _scheduleListDirty = true; return; }
            miniWindow?.Refresh();
            // Hidden (mini mode, tray): nothing to see, so build the cards when the window shows again (IsVisibleChanged).
            if (!IsVisible || _startingHidden) { _scheduleListDirty = true; return; }

            // Past to future (the day count), then by time (untimed last, like the mini calendar), then by title.
            // A stable sort, so cards with the same keys keep their order between refreshes. A 여러 날 schedule going on today
            // counts as today (D-day), earlier first days first; one that is over counts from its last day.
            var sorted = appData.Schedules
                .Select(item => new { Item = item, Days = item.RemainingDays })
                .OrderBy(entry => entry.Days)
                .ThenBy(entry => entry.Item.StartDate ?? DateTime.MaxValue)
                .ThenBy(entry => entry.Item.Time ?? "99:99", StringComparer.Ordinal)
                .ThenBy(entry => entry.Item.Title ?? string.Empty, StringComparer.CurrentCulture)
                .Select(entry => entry.Item)
                .ToList();

            // Same cards as shown (same items, same text, same day): nothing to rebuild.
            string signature = ScheduleListSignature(sorted);
            if (!_scheduleListDirty && signature == _scheduleListSignature &&
                ScheduleList.ItemsSource is List<ScheduleItem> shown && shown.SequenceEqual(sorted))
                return;

            // Keep the scroll position: a refresh (a sync, a color change …) must not jump back to the top.
            var scroll = ScheduleList.Template?.FindName("ScheduleScroll", ScheduleList) as System.Windows.Controls.ScrollViewer;
            double offset = scroll?.VerticalOffset ?? 0;
            ScheduleList.ItemsSource = sorted;
            if (scroll != null && offset > 0) scroll.ScrollToVerticalOffset(offset);
            _scheduleListSignature = signature;
            _scheduleListDirty = false;
        }

        private static string ScheduleListSignature(List<ScheduleItem> items)
        {
            var text = new System.Text.StringBuilder(DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            foreach (ScheduleItem item in items)
                text.Append('\n').Append(item.Id).Append('|').Append(item.Title).Append('|').Append(item.Period).Append('~').Append(item.EndPeriod).Append('|')
                    .Append(item.Time).Append('|').Append(item.IsCompleted ? '1' : '0').Append('|').Append(item.Color);
            return text.ToString();
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

            string normalizedTime = null;
            if (AddTimeToggle.IsChecked == true &&
                !FeatureRules.TryScheduleTime(AddTimeInput.Text, out normalizedTime))
            {
                System.Windows.MessageBox.Show(
                    this,
                    "유효한 시간을 입력해 주세요. (HH:mm)",
                    "시간 확인",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!TryRangeEnd(AddRangeToggle, AddEndPicker, selectedDate, out string endPeriod))
                return;

            string dateStr = selectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            appData.Schedules.Add(new ScheduleItem
            {
                Title = TitleInput.Text.Trim(),
                Period = dateStr,
                EndPeriod = endPeriod,
                Time = normalizedTime
            });

            SaveDataSafely();
            RefreshScheduleList();

            TitleInput.Text = "";
            ResetDateToToday();
            AddTimeToggle.IsChecked = false;
            AddTimeInput.Text = "09:00";
            AddTimeInput.IsEnabled = false;
            AddRangeToggle.IsChecked = false;
            AddEndPicker.SelectedDate = null;

            TitleInput.Focus();
        }

        private void AddTimeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (AddTimeToggle == null || AddTimeInput == null) return;
            AddTimeInput.IsEnabled = AddTimeToggle.IsChecked == true;
        }

        // ---- 여러 날 (10/8 ~ 10/10): the add form's and the edit panel's 종료 날짜 ----

        private void AddRangeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (AddRangeToggle == null || AddEndRow == null) return;
            bool on = AddRangeToggle.IsChecked == true;
            AddEndRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            // Turned on: the day after the chosen start, to change from there.
            if (on && !AddEndPicker.SelectedDate.HasValue && TryGetSelectedDate(out DateTime start) && start < DateTime.MaxValue.Date)
            {
                AddEndPicker.SelectedDate = start.AddDays(1);
                AddEndPicker.DisplayDate = start.AddDays(1);
            }
            ShowEndText(AddEndText, AddEndPicker.SelectedDate);
        }

        private void AddEndPicker_SelectedDateChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
            ShowEndText(AddEndText, AddEndPicker.SelectedDate);

        private void InlineEditRangeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (InlineEditRangeToggle == null || InlineEditEndBox == null) return;
            bool on = InlineEditRangeToggle.IsChecked == true;
            InlineEditEndBox.IsEnabled = on;
            if (on && !_inlineEditLoading && !InlineEditEndPicker.SelectedDate.HasValue && TryGetInlineEditDate(out DateTime start) && start < DateTime.MaxValue.Date)
            {
                InlineEditEndPicker.SelectedDate = start.AddDays(1);
                InlineEditEndPicker.DisplayDate = start.AddDays(1);
            }
            ShowEndText(InlineEditEndText, on ? InlineEditEndPicker.SelectedDate : null);
        }

        private void InlineEditEndPicker_SelectedDateChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
            ShowEndText(InlineEditEndText, InlineEditRangeToggle.IsChecked == true ? InlineEditEndPicker.SelectedDate : null);

        private static void ShowEndText(System.Windows.Controls.TextBlock text, DateTime? end)
        {
            if (text == null) return;
            text.Text = end.HasValue ? end.Value.ToString("yyyy. M. d. (ddd)", CultureInfo.GetCultureInfo("ko-KR")) + "까지" : "종료 날짜";
        }

        /// <summary>
        /// The EndPeriod a form saves: null with 여러 날 off (or the same day picked), the range's last day otherwise; false
        /// (after saying why) when the 종료 날짜 is missing, before the start or too far.
        /// </summary>
        private bool TryRangeEnd(System.Windows.Controls.CheckBox toggle, System.Windows.Controls.DatePicker picker, DateTime start, out string endPeriod)
        {
            endPeriod = null;
            if (toggle.IsChecked != true) return true;
            string error;
            if (!picker.SelectedDate.HasValue) error = "종료 날짜를 선택해 주세요.";
            else if (ScheduleItem.TryRange(start, picker.SelectedDate.Value, out endPeriod, out error)) return true;
            if (rangeMessageOverride != null) rangeMessageOverride(error);
            else System.Windows.MessageBox.Show(this, error, "날짜 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // Checks only: receives the 종료 날짜 message instead of a message box.
        internal static Action<string> rangeMessageOverride = null;

        private bool _syncingAddDate; // the combos and the calendar are updating each other

        private void CalendarPicker_SelectedDateChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_syncingAddDate || !CalendarPicker.SelectedDate.HasValue) return;
            DateTime selected = CalendarPicker.SelectedDate.Value;
            _syncingAddDate = true;
            try
            {
                SetYearSelection(selected.Year);

                MonthCombo.SelectedItem = selected.Month;

                UpdateDays(selected.Year, selected.Month);
                DayCombo.SelectedItem = selected.Day;
            }
            finally { _syncingAddDate = false; }
        }

        // A date picked in the combos: the calendar button opens on that date too.
        private void SyncCalendarPickerToCombos()
        {
            if (_syncingAddDate || !TryGetSelectedDate(out DateTime date) || CalendarPicker.SelectedDate == date) return;
            _syncingAddDate = true;
            try
            {
                CalendarPicker.SelectedDate = date;
                CalendarPicker.DisplayDate = date;
            }
            finally { _syncingAddDate = false; }
        }

        private void ResetDateToToday()
        {
            DateTime today = DateTime.Today;

            SetYearSelection(today.Year);
            MonthCombo.SelectedItem = today.Month;

            UpdateDays(today.Year, today.Month);
            DayCombo.SelectedItem = today.Day;

            CalendarPicker.SelectedDate = today;
            _addFormDate = today;
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

            // Settings open in this window give way; settings in their own window (from the mini window) stay open.
            if (InlineSettingsPanel.Visibility == Visibility.Visible && settingsHost == null)
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
            InlineEditTimeToggle.IsChecked = item.Time != null;
            InlineEditTimeInput.Text = item.Time ?? "09:00";
            InlineEditTimeInput.IsEnabled = item.Time != null;
            // 여러 날: its last day; a one-day schedule starts with the toggle off (and the day after as the picker's default).
            InlineEditEndPicker.SelectedDate = item.EndDate;
            InlineEditEndPicker.DisplayDate = item.EndDate ?? (date < DateTime.MaxValue.Date ? date.AddDays(1) : date);
            InlineEditRangeToggle.IsChecked = item.IsMultiDay;
            InlineEditEndBox.IsEnabled = item.IsMultiDay;
            ShowEndText(InlineEditEndText, item.EndDate);
            _inlineEditLoading = false;

            InlineEditPanel.Visibility = Visibility.Visible;
            InlineEditTitleInput.Focus();
            InlineEditTitleInput.SelectAll();
        }

        private void InlineEditTimeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (InlineEditTimeToggle == null || InlineEditTimeInput == null) return;
            InlineEditTimeInput.IsEnabled = InlineEditTimeToggle.IsChecked == true;
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

            string normalizedTime = null;
            if (InlineEditTimeToggle.IsChecked == true &&
                !FeatureRules.TryScheduleTime(InlineEditTimeInput.Text, out normalizedTime))
            {
                System.Windows.MessageBox.Show(
                    this,
                    "유효한 시간을 입력해 주세요. (HH:mm)",
                    "시간 확인",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!TryRangeEnd(InlineEditRangeToggle, InlineEditEndPicker, selectedDate, out string endPeriod))
                return;

            ScheduleItem item = appData.Schedules.Find(s => s.Id == _inlineEditId.Value);
            if (item == null)
            {
                CloseInlineEdit();
                return;
            }

            item.Title = title;
            item.Period = selectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            item.EndPeriod = endPeriod;
            item.Time = normalizedTime;

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
                RemoveConfirmMessage.Text = RemoveConfirmText(item);
                RemoveConfirmPanel.Visibility = Visibility.Visible;
            }
        }

        // A card linked to Google Calendar says what deleting it does there, for that event: one this app made (no guests)
        // goes from Google too; one made in Google (or with other people invited) stays there. Same words as the mini window.
        private string RemoveConfirmText(ScheduleItem item)
        {
            string text = $"‘{item.Title}’ 일정을 삭제할까요?";
            string google = MiniWindow.GoogleDeleteNote(appData?.GoogleCalendar, item);
            if (google != null) text += Environment.NewLine + google;
            return text;
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
            DateTime today = DateTime.Today;
            for (int y = today.Year - 5; y <= today.Year + 5; y++) YearCombo.Items.Add(y);
            for (int m = 1; m <= 12; m++) MonthCombo.Items.Add(m);

            YearCombo.SelectedItem = today.Year;
            MonthCombo.SelectedItem = today.Month;
            UpdateDays(today.Year, today.Month);
            DayCombo.SelectedItem = today.Day;
            CalendarPicker.SelectedDate = today;
            _addFormDate = today;

            YearCombo.SelectionChanged += (s, e) => UpdateDaysForCurrentSelection();
            YearCombo.LostFocus += (s, e) => NormalizeYearInput();
            MonthCombo.SelectionChanged += (s, e) => UpdateDaysForCurrentSelection();
            DayCombo.SelectionChanged += (s, e) => SyncCalendarPickerToCombos();
        }

        // Another month (or year) keeps the chosen day, as far as the month has it (31 → 30 in April), like the edit form.
        private void UpdateDays(int year, int month)
        {
            int previousDay = DayCombo.SelectedItem is int selectedDay ? selectedDay : 1;
            DayCombo.Items.Clear();
            int days = DateTime.DaysInMonth(year, month);
            for (int d = 1; d <= days; d++) DayCombo.Items.Add(d);
            DayCombo.SelectedItem = Math.Min(previousDay, days);
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
                OnDayMaybeChanged();

                SetNextMidnightInterval();
            };

            SetNextMidnightInterval();
            dayChangeTimer.Start();
        }

        // Midnight, or the clock / time zone was changed: D-days and "today" follow.
        private void OnDayMaybeChanged()
        {
            if (appData == null) return;
            // The add form still shows the day it was set to and nothing is typed yet: move it on to the new today, so a
            // task added in the morning isn't dated yesterday.
            if (_addFormDate != default(DateTime) && _addFormDate != DateTime.Today && string.IsNullOrWhiteSpace(TitleInput.Text) &&
                TryGetSelectedDate(out DateTime shown) && shown == _addFormDate)
                ResetDateToToday();
            RefreshScheduleList();
        }

        private void OnSystemTimeChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (closingApp || dayChangeTimer == null) return;
                dayChangeTimer.Stop();
                SetNextMidnightInterval(); // the next midnight moved with the clock
                dayChangeTimer.Start();
                OnDayMaybeChanged();
            }));
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
                // Open in its own window (from the mini window): bring that forward instead of throwing its changes away.
                if (settingsHost != null) BringToFront(settingsHost);
                else CloseInlineSettings(false);
                return;
            }

            // 설정을 여는 동안에는 위젯 이동 모드를 잠시 끕니다.
            ModeToggle.IsChecked = false;

            ResetMiniPreviewOwnership();

            _inlineSettingsOriginal = CloneAppearance(appData.Appearance);
            _inlineSettingsDraft = CloneAppearance(appData.Appearance);
            _inlineStartupDraft = appData.StartupEnabled;

            DisarmSettingsReset();
            _inlinePetSpotsReset = false;
            _inlineSettingsLoading = true;
            InlineOpacitySlider.Value = _inlineSettingsDraft.Opacity * 100;
            InlineTitleFontSizeSlider.Value = _inlineSettingsDraft.TitleFontSize;
            InlineDDayFontSizeSlider.Value = _inlineSettingsDraft.DDayFontSize;
            InlinePresetCombo.SelectedIndex = FindPresetIndex(_inlineSettingsDraft.ThemePreset);
            InlineStartupToggle.IsChecked = _inlineStartupDraft;
            _inlineCharacterScaleDraft = appData.MiniCharacterScale;
            InlineCharacterScaleSlider.Value = _inlineCharacterScaleDraft;
            _inlineCharacterScaleTouched = false;
            _inlineCharacterVisibleTouched = false;
            InlineCharacterSideCombo.SelectedIndex = string.Equals(appData.MiniCharacterSide, "Right", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            InlineCharacterVerticalSlider.Value = appData.MiniCharacterVertical;
            InlineCharacterGapSlider.Value = appData.MiniCharacterGap;
            UpdateCharacterPlacementLabels();
            InlineCharacterVisibleToggle.IsChecked = appData.MiniCharacterVisible;
            InlineAlwaysOnTopToggle.IsChecked = appData.AlwaysOnTop;
            InlineBlockDDaySwitch.IsChecked = appData.MiniBlockDDayVisible;
            InlineFlipEffectCombo.SelectedIndex = FlipEffectIndex(appData.MiniFlipEffect);
            LoadInlineHotKey(appData.BringToFrontHotKeyEnabled, appData.BringToFrontHotKey);
            UpdateGoogleUi();
            LoadUpdateUi();
            UpdateInlineSettingsLabels();
            _inlineSettingsLoading = false;

            InlineSettingsPanel.Visibility = Visibility.Visible;
        }

        // 넘김 애니메이션 (the mini calendar's ‹ / › page turn): the saved value ↔ the combo's item, each item's Tag being its
        // value — 1–6 = 효과 1–6 (1/3/5 위로 넘기기, 2/4/6 모서리 넘기기; 3/4 show a third of the back above the rings, 5/6 none),
        // 0 = 애니메이션 없음. An unknown value shows as 효과 1.
        private int FlipEffectIndex(int effect)
        {
            for (int i = 0; i < InlineFlipEffectCombo.Items.Count; i++)
                if (InlineFlipEffectCombo.Items[i] is System.Windows.Controls.ComboBoxItem item && item.Tag as string == effect.ToString(CultureInfo.InvariantCulture)) return i;
            return 0;
        }

        private int DraftFlipEffect =>
            InlineFlipEffectCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
            int.TryParse(item.Tag as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int effect) ? effect : 1;

        private static int FindPresetIndex(string preset)
        {
            switch (preset)
            {
                case "Dark": return 1;
                case "Blue": return 2;
                case "Pink": return 3;
                case "Modern": return 4;
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

        private void InlineCharacterScaleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_inlineSettingsLoading || _inlineSettingsDraft == null)
                return;

            if (previewScaleConflict)
                RebaseConflictedMiniScales();
            _inlineCharacterScaleDraft = (int)Math.Round(InlineCharacterScaleSlider.Value);
            _inlineCharacterScaleTouched = true;
            UpdateInlineSettingsLabels();
            PreviewMiniSettings();
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
            if (InlineCharacterScaleText != null)
                InlineCharacterScaleText.Text = $"{(int)InlineCharacterScaleSlider.Value}%";
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
            if (_inlineCharacterScaleTouched && !previewScaleConflict)
            {
                // 미니 창 캐릭터 크기 = every pet the same size (otherwise each pet keeps its own).
                appData.MiniCharacterScale = Math.Max(50, Math.Min(300, _inlineCharacterScaleDraft));
                if (appData.MiniExtraCharacters != null) foreach (var slot in appData.MiniExtraCharacters) slot.Scale = null;
            }
            appData.AlwaysOnTop = InlineAlwaysOnTopToggle.IsChecked == true;
            bool blockDDay = InlineBlockDDaySwitch.IsChecked == true;
            if (appData.MiniBlockDDayVisible != blockDDay) { appData.MiniBlockDDayVisible = blockDDay; miniWindow?.Refresh(); }
            appData.MiniFlipEffect = DraftFlipEffect; // the mini window reads it when a flip starts: nothing to refresh
            appData.BringToFrontHotKeyEnabled = InlineHotKeyToggle.IsChecked == true;
            appData.BringToFrontHotKey = _inlineHotKeyDraft.ToString();
            if (!ApplyBringToFrontHotKey())
                System.Windows.MessageBox.Show(this,
                    $"{appData.BringToFrontHotKey}는 다른 프로그램이 이미 쓰고 있어 등록하지 못했습니다. 설정에서 다른 단축키로 바꿔 주세요.",
                    "단축키 설정", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (_inlinePetSpotsReset)
            {
                // 초기화 then 저장: dragged pets go back to their default spots.
                if (miniWindow != null)
                {
                    if (!previewSpotsConflict) miniWindow.RestorePetSpots(new List<MiniPetSpot>());
                }
                else
                {
                    if (!previewSpotsConflict)
                    {
                        appData.MiniPetSpots?.Clear();
                        // The characters on the calendar forget their remembered spots too (a second copy's "#2" as well; others keep theirs).
                        MiniWindow.RememberMiniSpots(appData);
                    }
                }
                _inlinePetSpotsReset = false;
            }
            ApplyCharacterPlacementSetting();
            NativeMethods.SetWidgetStacking(this, appData.AlwaysOnTop);
            if (miniWindow != null) NativeMethods.SetWidgetStacking(miniWindow, appData.AlwaysOnTop);
            bool showCharacter = InlineCharacterVisibleToggle.IsChecked == true;
            miniWindow?.UpdateCharacterSize();
            if (!_inlineCharacterVisibleTouched || previewVisibleConflict) { } // not clicked here: keep whatever the pets show now
            else if (miniWindow != null)
                miniWindow.SetCharacterVisible(showCharacter);
            else if (showCharacter != appData.MiniCharacterVisible)
            {
                // The calendar board keeps its saved place; the window is fitted around it (and the pets) when it opens.
                appData.MiniBoard = MiniWindow.SavedBoard(appData);
                appData.MiniCharacterVisible = showCharacter;
            }
            petCompanion?.UpdateCharacterSize(); // sizes / placement changed in the panel
            petCompanion?.UpdateCharacterVisibility();
            ApplyAppearance(appData.Appearance);
            SaveDataSafely();
            CloseInlineSettings(true);
        }

        // ---- 초기화 (settings panel): press twice; the draft goes back to the first-run values ----
        private bool _inlineResetArmed;
        private bool _inlinePetSpotsReset; // 초기화 also puts dragged pets back to their default spots (on 저장)

        private void DisarmSettingsReset()
        {
            _inlineResetArmed = false;
            if (InlineSettingsResetButton != null) InlineSettingsResetButton.Content = "초기화";
        }

        private void InlineSettingsResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (_inlineSettingsDraft == null) return;
            if (!_inlineResetArmed)
            {
                _inlineResetArmed = true;
                InlineSettingsResetButton.Content = "정말 초기화";
                return;
            }
            DisarmSettingsReset();

            // Reset reclaims externally changed fields but keeps the opening baseline for fields this panel previewed itself.
            if (previewScaleConflict) RebaseConflictedMiniScales();
            if (previewVisibleConflict) previewOriginalVisible = appData.MiniCharacterVisible;
            if (previewSideConflict) previewOriginalSide = appData.MiniCharacterSide;
            if (previewVerticalConflict) previewOriginalVertical = appData.MiniCharacterVertical;
            if (previewGapConflict) previewOriginalGap = appData.MiniCharacterGap;
            if (previewSpotsConflict) previewOriginalSpots = (appData.MiniPetSpots ?? new List<MiniPetSpot>()).Select(MiniWindow.CopySpot).ToList();
            previewScaleConflict = previewVisibleConflict = previewSideConflict = previewVerticalConflict = previewGapConflict = previewSpotsConflict = false;

            // Appearance: the Light theme with its colors, full opacity, default font sizes.
            var defaults = new AppearanceSettings();
            defaults.CopyColorsFrom(AppearanceSettings.Presets["Light"]);
            defaults.ThemePreset = "Light";
            _inlineSettingsDraft = CloneAppearance(defaults);

            _inlineSettingsLoading = true;
            InlineOpacitySlider.Value = _inlineSettingsDraft.Opacity * 100;
            InlineTitleFontSizeSlider.Value = _inlineSettingsDraft.TitleFontSize;
            InlineDDayFontSizeSlider.Value = _inlineSettingsDraft.DDayFontSize;
            InlinePresetCombo.SelectedIndex = FindPresetIndex("Light");
            // Mini window: every pet 100 %, left of the calendar at the bottom, 8 px gap, shown; not always on top.
            _inlineCharacterScaleDraft = 100;
            InlineCharacterScaleSlider.Value = 100;
            _inlineCharacterScaleTouched = true; // applies to every pet
            InlineCharacterSideCombo.SelectedIndex = 0;
            InlineCharacterVerticalSlider.Value = 100;
            InlineCharacterGapSlider.Value = 8;
            InlineCharacterVisibleToggle.IsChecked = true;
            _inlineCharacterVisibleTouched = true; // 초기화 shows the pets again
            InlineAlwaysOnTopToggle.IsChecked = false;
            InlineBlockDDaySwitch.IsChecked = true;
            InlineFlipEffectCombo.SelectedIndex = FlipEffectIndex(1); // 효과 1 · 위로 넘기기
            LoadInlineHotKey(true, HotKeyGesture.Default);
            // Windows 시작 (a registry setting of the system) is not changed.
            _inlineSettingsLoading = false;
            _inlinePetSpotsReset = true;
            _inlineCharacterSideTouched = _inlineCharacterVerticalTouched = _inlineCharacterGapTouched = true;

            UpdateInlineSettingsLabels();
            UpdateCharacterPlacementLabels();
            ApplyAppearance(_inlineSettingsDraft); // preview; 취소 restores _inlineSettingsOriginal
            PreviewMiniSettings();
            // Settings opened from the mini window preview live: show the pets at their default spots too (취소 puts them back).
            if (settingsHost != null && miniWindow != null) miniWindow.RestorePetSpots(new List<MiniPetSpot>());
        }

        // ---- 단축키로 맨 앞에 띄우기: on/off and the shortcut itself (applied on 저장) ----
        private HotKeyGesture _inlineHotKeyDraft = HotKeyGesture.ParseOrDefault(null);

        private void LoadInlineHotKey(bool enabled, string hotKey)
        {
            _inlineHotKeyDraft = HotKeyGesture.ParseOrDefault(hotKey);
            InlineHotKeyToggle.IsChecked = enabled;
            bool taken = enabled && !_bringToFrontHotKey && appData != null && appData.BringToFrontHotKeyEnabled &&
                         _inlineHotKeyDraft.ToString() == HotKeyGesture.ParseOrDefault(appData.BringToFrontHotKey).ToString();
            InlineHotKeyStatus.Text = taken
                ? $"{_inlineHotKeyDraft}는 다른 프로그램이 쓰고 있어 동작하지 않습니다. 칸을 눌러 다른 단축키로 바꿔 주세요."
                : "칸을 누른 뒤 새 단축키를 누르세요. 저장하면 적용됩니다.";
            UpdateInlineHotKeyUi();
        }

        private void UpdateInlineHotKeyUi()
        {
            bool enabled = InlineHotKeyToggle.IsChecked == true;
            InlineHotKeyBox.Text = _inlineHotKeyDraft.ToString();
            InlineHotKeyBox.IsEnabled = enabled;
            InlineHotKeyDefaultButton.IsEnabled = enabled && _inlineHotKeyDraft.ToString() != HotKeyGesture.Default;
        }

        private void InlineHotKeyToggle_Click(object sender, RoutedEventArgs e) => UpdateInlineHotKeyUi();

        private void InlineHotKeyDefault_Click(object sender, RoutedEventArgs e)
        {
            _inlineHotKeyDraft = HotKeyGesture.ParseOrDefault(HotKeyGesture.Default);
            InlineHotKeyStatus.Text = "기본값 Ctrl+G로 돌렸습니다. 저장하면 적용됩니다.";
            UpdateInlineHotKeyUi();
        }

        // While the box has focus the saved shortcut is released, so pressing it (e.g. Ctrl+G again) can be typed here.
        private void InlineHotKeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            UnregisterBringToFrontHotKey();
            InlineHotKeyStatus.Text = "새 단축키를 누르세요 (F1~F24는 단독 가능, 그 외는 Ctrl·Alt·Win 중 하나 포함). Esc로 취소합니다.";
        }

        private void InlineHotKeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            ApplyBringToFrontHotKey(); // the saved one again until 저장
        }

        private void InlineHotKeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape || key == Key.Tab)
            {
                e.Handled = key == Key.Escape; // Tab still moves focus on
                if (key == Key.Escape) Keyboard.ClearFocus();
                InlineHotKeyStatus.Text = "칸을 누른 뒤 새 단축키를 누르세요. 저장하면 적용됩니다.";
                return;
            }
            var gesture = HotKeyGesture.FromKeyPress(Keyboard.Modifiers, key);
            if (gesture == null)
            {
                if (key != Key.LeftCtrl && key != Key.RightCtrl && key != Key.LeftAlt && key != Key.RightAlt &&
                    key != Key.LeftShift && key != Key.RightShift && key != Key.LWin && key != Key.RWin)
                    InlineHotKeyStatus.Text = "F1~F24는 단독으로, 그 외 키는 Ctrl·Alt·Win 중 하나와 함께 눌러 주세요.";
                return;
            }
            _inlineHotKeyDraft = gesture;
            InlineHotKeyStatus.Text = $"{gesture}로 바꿉니다. 저장하면 적용됩니다.";
            UpdateInlineHotKeyUi();
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
            DisarmSettingsReset();
            if (!commit && _inlineSettingsOriginal != null)
                ApplyAppearance(_inlineSettingsOriginal);

            InlineSettingsPanel.Visibility = Visibility.Collapsed;
            _inlineSettingsOriginal = null;
            _inlineSettingsDraft = null;
            _inlineStartupDraft = false;
            ReturnSettingsPanel(commit);
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
            SetBrush(res, "AccentBrush", settings.AccentColor);
            SetBrush(res, "ControlHoverBrush", settings.ControlHoverColor);
            SetBrush(res, "TodayBrush", settings.TodayColor);
            SetBrush(res, "FutureBrush", settings.FutureColor);
            SetBrush(res, "PastBrush", settings.PastColor);

            res["TitleFontSize"] = settings.TitleFontSize;
            res["DDayFontSize"] = settings.DDayFontSize;

            MainBorder.Opacity = settings.Opacity;
            // The other windows only need recoloring when the theme itself changes, not on every opacity / font / color tick
            // (the mini window and the pets build themselves in the current theme when they open).
            if (_themeApplied && settings.ThemePreset == _themedPreset) return;
            _themeApplied = true;
            _themedPreset = settings.ThemePreset;
            miniWindow?.ApplyTheme(settings.ThemePreset); // live preview on the mini window too
            petCompanion?.ApplyTheme(settings.ThemePreset);
            AuxTheme.SetTheme(settings.ThemePreset); // 설정, 캐릭터 설정·선택, 연락 · 알림, 음악 follow the theme too
            AuxTheme.ApplyTo(InlineSettingsPanel);    // the panel merges AuxiliaryStyles itself, so it needs its own override
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
