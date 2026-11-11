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
            if (AppProfile.IsDemo)
            {
                Title += " · 시연";
                InlineStartupToggle.IsEnabled = InlineHotKeyToggle.IsEnabled = false;
                InlineStartupToggle.ToolTip = InlineHotKeyToggle.ToolTip = "시연용 실행은 일반 앱의 자동 시작과 단축키 설정에 영향을 주지 않습니다.";
            }
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
            PrepareForExit();
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

            // The full agenda is part of the calendar and moves with it.
            if (appData != null)
            {
                ShowMiniWindow();
                if (miniWindow == null) return;
                miniWindow.CenterOnPrimaryScreen(); // and saves that place (window and calendar board)
                BringToFront(miniWindow);
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
        // window, or returns it behind other apps when pressed again. The saved pin preference is retained.
        private const int BringToFrontHotKeyId = 0x5347;
        private bool _bringToFrontHotKey;
        private bool hotKeyRaised;
        private IntPtr hotKeyPreviousForeground;

        private void UnregisterBringToFrontHotKey()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (_bringToFrontHotKey && hwnd != IntPtr.Zero) NativeMethods.UnregisterHotKey(hwnd, BringToFrontHotKeyId);
            _bringToFrontHotKey = false;
            hotKeyRaised = false;
            hotKeyPreviousForeground = IntPtr.Zero;
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
                if (appData != null && !closingApp) ToggleWidgetFromHotKey();
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
                AppProfile.ApplyRuntimeSettings(appData);

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

            string monitorId = GetCurrentMonitorId();
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

        internal void SaveForRenderRecovery()
        {
            if (_dataUnavailable || appData == null) return;
            // Persist managed state only: no window positions, view refresh, dialogs, or composition calls here.
            try { dataStore.SaveData(appData); }
            catch (Exception error) when (!SystemMediaService.IsFatal(error)) { ErrorLog.Write("render-recovery-save", error); }
        }

        private bool SaveDataSafely(bool showError = true)
        {
            if (_dataUnavailable || appData == null) return false; // the data could not be read at start: never write over it
            try
            {
                dataStore.SaveData(appData);
                _saveErrorShown = false;
                GooglePreferencesSaved();
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
            SyncSettingsDayCount();
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

        private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            // Buttons keep their own clicks; every other part of the header moves this secondary window.
            for (var source = e.OriginalSource as DependencyObject; source != null && source != sender;
                source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
                if (source is System.Windows.Controls.Primitives.ButtonBase) return;
            e.Handled = true;
            try { DragMove(); }
            catch (InvalidOperationException) { } // the button may have been released before the native move loop starts
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

        // ── 설정 ──

        private void BeginSettingsEdit()
        {
            if (appData == null)
                return;

            ResetMiniPreviewOwnership();

            _inlineSettingsOriginal = CloneAppearance(appData.Appearance);
            _inlineSettingsDraft = CloneAppearance(appData.Appearance);
            _inlineStartupDraft = appData.StartupEnabled;

            DisarmSettingsReset();
            _inlinePetSpotsReset = false;
            _inlineSettingsLoading = true;
            InlineOpacitySlider.Value = _inlineSettingsDraft.Opacity * 100;
            InlineCompactTitleFontSizeSlider.Value = _inlineSettingsDraft.CompactTitleFontSize;
            InlineCompactDDayFontSizeSlider.Value = _inlineSettingsDraft.CompactDDayFontSize;
            InlineMonthTitleFontSizeSlider.Value = _inlineSettingsDraft.EffectiveMonthTitleFontSize;
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

            InlineSettingsPanel.Visibility = Visibility.Collapsed; // its named sections are hosted by SettingsWindow
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
                CompactTitleFontSize = source.CompactTitleFontSize,
                CompactDDayFontSize = source.CompactDDayFontSize,
                MonthTitleFontSize = source.EffectiveMonthTitleFontSize,
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

            _inlineSettingsDraft.CompactTitleFontSize = InlineCompactTitleFontSizeSlider.Value;
            _inlineSettingsDraft.CompactDDayFontSize = InlineCompactDDayFontSizeSlider.Value;
            _inlineSettingsDraft.MonthTitleFontSize = InlineMonthTitleFontSizeSlider.Value;
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
            if (InlineCompactTitleFontSizeText != null)
                InlineCompactTitleFontSizeText.Text = $"{(int)InlineCompactTitleFontSizeSlider.Value}";
            if (InlineCompactDDayFontSizeText != null)
                InlineCompactDDayFontSizeText.Text = $"{(int)InlineCompactDDayFontSizeSlider.Value}";
            if (InlineMonthTitleFontSizeText != null)
                InlineMonthTitleFontSizeText.Text = $"{(int)InlineMonthTitleFontSizeSlider.Value}";
            if (InlineTitleFontSizeText != null)
                InlineTitleFontSizeText.Text = $"{(int)InlineTitleFontSizeSlider.Value}";
            if (InlineDDayFontSizeText != null)
                InlineDDayFontSizeText.Text = $"{(int)InlineDDayFontSizeSlider.Value}";
            if (InlineCharacterScaleText != null)
                InlineCharacterScaleText.Text = $"{(int)InlineCharacterScaleSlider.Value}%";
        }

        private void InlineSettingsApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (AppProfile.IsDemo)
            {
                _inlineStartupDraft = false;
                InlineStartupToggle.IsChecked = InlineHotKeyToggle.IsChecked = false;
            }
            if (_inlineSettingsDraft == null || appData == null) return;
            // Music, pets and Google actions already save immediately. Commit the deferred pages in one write.
            var communication = appData.Communication;
            var reminders = appData.Reminders;
            bool contactsEdited = contactWindow?.HasPendingChanges == true;
            if (contactsEdited && !contactWindow.TryBuildSettings(out communication, out reminders)) return;

            if (!startupService.IsStartupEnabled(out bool startupWasEnabled, out string startupError))
            {
                UnifiedSettings?.ShowStatus(startupError);
                return;
            }
            bool startupChanged = startupWasEnabled != _inlineStartupDraft;
            bool SetStartup(bool enabled, out string error) => enabled
                ? startupService.TryEnableStartup(out error) : startupService.TryDisableStartup(out error);
            if (startupChanged && !SetStartup(_inlineStartupDraft, out startupError))
            {
                SetStartup(startupWasEnabled, out _);
                UnifiedSettings?.ShowStatus(startupError);
                return;
            }

            var before = new
            {
                appData.Appearance, appData.StartupEnabled, appData.AlwaysOnTop,
                appData.BringToFrontHotKeyEnabled, appData.BringToFrontHotKey,
                appData.MiniBlockDDayVisible, appData.MiniFlipEffect, appData.MiniDayCount,
                appData.Communication, appData.Reminders
            };
            void RestoreDeferredSettings()
            {
                appData.Appearance = before.Appearance;
                appData.StartupEnabled = before.StartupEnabled;
                appData.AlwaysOnTop = before.AlwaysOnTop;
                appData.BringToFrontHotKeyEnabled = before.BringToFrontHotKeyEnabled;
                appData.BringToFrontHotKey = before.BringToFrontHotKey;
                appData.MiniBlockDDayVisible = before.MiniBlockDDayVisible;
                appData.MiniFlipEffect = before.MiniFlipEffect;
                appData.MiniDayCount = before.MiniDayCount;
                appData.Communication = before.Communication;
                appData.Reminders = before.Reminders;
            }
            void Failed(string message)
            {
                RestoreDeferredSettings();
                ApplyBringToFrontHotKey();
                if (startupChanged && !SetStartup(startupWasEnabled, out string rollbackError))
                    message += "\n자동 시작 설정을 복원하지 못했습니다: " + rollbackError;
                UnifiedSettings?.ShowStatus(message);
            }

            appData.Appearance = CloneAppearance(_inlineSettingsDraft);
            appData.StartupEnabled = _inlineStartupDraft;
            appData.AlwaysOnTop = InlineAlwaysOnTopToggle.IsChecked == true;
            appData.MiniBlockDDayVisible = InlineBlockDDaySwitch.IsChecked == true;
            appData.MiniFlipEffect = DraftFlipEffect;
            appData.MiniDayCount = settingsDayCount == null || !settingsDayCountTouched ? before.MiniDayCount : settingsDayCount.SelectedIndex + 1;
            appData.BringToFrontHotKeyEnabled = InlineHotKeyToggle.IsChecked == true;
            appData.BringToFrontHotKey = _inlineHotKeyDraft.ToString();
            appData.Communication = communication;
            appData.Reminders = reminders;
            if (!ApplyBringToFrontHotKey())
            {
                Failed(_inlineHotKeyDraft + "는 다른 프로그램이 사용 중입니다. 다른 단축키를 선택해 주세요.");
                return;
            }
            if (!SaveDataSafely(showError: false))
            {
                Failed("설정을 저장하지 못했습니다. 입력 내용은 유지됩니다. 저장 경로를 확인한 뒤 다시 저장해 주세요.");
                return;
            }

            if (contactsEdited) contactWindow.AcceptSavedSettings();
            NativeMethods.SetWidgetStacking(this, appData.AlwaysOnTop);
            if (miniWindow != null)
            {
                NativeMethods.SetWidgetStacking(miniWindow, appData.AlwaysOnTop);
                if (before.MiniDayCount != appData.MiniDayCount) miniWindow.SetDayCount(appData.MiniDayCount, persist: false);
                else miniWindow.Refresh();
            }
            ApplyAppearance(appData.Appearance);
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
            InlineCompactTitleFontSizeSlider.Value = _inlineSettingsDraft.CompactTitleFontSize;
            InlineCompactDDayFontSizeSlider.Value = _inlineSettingsDraft.CompactDDayFontSize;
            InlineMonthTitleFontSizeSlider.Value = _inlineSettingsDraft.EffectiveMonthTitleFontSize;
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

            res["CompactTitleFontSize"] = settings.CompactTitleFontSize;
            res["CompactDDayFontSize"] = settings.CompactDDayFontSize;
            res["MonthTitleFontSize"] = settings.EffectiveMonthTitleFontSize;
            res["TitleFontSize"] = settings.TitleFontSize;
            res["DDayFontSize"] = settings.DDayFontSize;
            UpdateAppearancePreview(settings);

            MainBorder.Opacity = settings.Opacity;
            miniWindow?.ApplyAppearance(settings);
            petCompanion?.ApplyAppearance(settings);
            // The other windows only need recoloring when the theme itself changes, not on every opacity / font / color tick
            // (the mini window and the pets build themselves in the current theme when they open).
            if (_themeApplied && settings.ThemePreset == _themedPreset) return;
            _themeApplied = true;
            _themedPreset = settings.ThemePreset;
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
