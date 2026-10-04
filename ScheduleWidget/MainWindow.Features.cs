using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ScheduleWidget
{
    public partial class MainWindow : IMusicControls
    {
        private MiniWindow miniWindow;
        private MusicWindow musicWindow;
        private ContactWindow contactWindow;
        private DispatcherTimer reminderTimer;
        private bool checkingReminders;
        private string reminderErrorChannel; // the channel whose failure the 연락 · 알림 button's tooltip shows
        private bool closingApp;
        private readonly Dictionary<string, DateTime> reminderRetries = new Dictionary<string, DateTime>();
        private readonly CommunicationService communicationService = new CommunicationService();

        private void MiniButton_Click(object sender, RoutedEventArgs e) => ShowMiniWindow();
        private void MusicButton_Click(object sender, RoutedEventArgs e) => ShowMusic();
        private void MusicButton_RightClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ShowMusicMenu((FrameworkElement)sender);
        }
        private void ContactButton_Click(object sender, RoutedEventArgs e) => ShowContacts();
        private void ChooseCharacter_Click(object sender, RoutedEventArgs e)
        {
            // Same picker as the mini window's; 드래그로 위치 설정 is offered while the mini window has pets to drag.
            var picker = new CharacterWindow(appData.CharacterManifest, offerPlacement: miniWindow?.CanPlacePets == true,
                otherSlots: appData.MiniExtraCharacters?.Select(s => s.Manifest));
            bool chosen = picker.ShowDialog() == true;
            if (chosen)
            {
                appData.CharacterManifest = picker.Result;
                SaveDataSafely();
            }
            // A deleted character may be one the pets show: they reload either way (before a placement, too).
            if (chosen || picker.DeletedAny)
            {
                miniWindow?.ReloadCharacter();
                petCompanion?.ReloadCharacter();
            }
            if (picker.PlacementRequested) miniWindow?.StartPetPlacement();
        }

        // ---- 👤: the pets beside the TODO window ----
        // A pets-only MiniWindow (companion mode) owned by this window: same pets, settings, left click = 캐릭터 선택,
        // right click = 캐릭터 설정; it follows this window and is shown only while this window is.
        private MiniWindow petCompanion;

        private void MainPetsButton_Click(object sender, RoutedEventArgs e)
        {
            if (appData == null) return;
            appData.MainPetsVisible = !appData.MainPetsVisible;
            SaveDataSafely(false);
            UpdatePetCompanion();
        }

        private void UpdatePetCompanion()
        {
            if (appData == null) return;
            MainPetsButton.Opacity = appData.MainPetsVisible ? 1 : 0.45; // off = dimmed
            // 👤 on: the pets are only hidden while this window is (mini mode, tray, Esc) and shown again with it — not rebuilt
            // (a new window, browser view and page) every time. 👤 off or 종료 closes them.
            bool wanted = appData.MainPetsVisible && !closingApp;
            if (!wanted)
            {
                if (petCompanion == null) return;
                var closing = petCompanion;
                petCompanion = null;
                closing.Close();
                return;
            }
            if (!IsVisible || _startingHidden)
            {
                petCompanion?.Hide(); // (its 캐릭터 설정 and a placement in progress end with it: MiniWindow.OnMiniVisibleChanged)
                return;
            }
            if (petCompanion == null)
            {
                // No music bar here, but pets in 음악 반복 still follow the music the bar would control.
                petCompanion = new MiniWindow(appData, () => SaveDataSafely(false), ShowMusic, ShowContacts, ShowMusicMenu, ExitApplication,
                    null, null, companion: true, musicPlaying: () => ((IMusicControls)this).IsPlaying) { Owner = this, ShowActivated = false };
                petCompanion.CompanionHideRequested += () => { appData.MainPetsVisible = false; SaveDataSafely(false); Dispatcher.BeginInvoke(new Action(UpdatePetCompanion)); };
                petCompanion.SlotsChanged += () => miniWindow?.ReloadCharacter(); // the same pets in the mini window
                petCompanion.Closed += (s, e) => { if (ReferenceEquals(petCompanion, s)) petCompanion = null; };
                if (_themeApplied) petCompanion.ApplyTheme(_themedPreset, refresh: false); // a theme being previewed in 설정
            }
            FollowPetCompanion();
            petCompanion.Show();
            FollowPetCompanion();
        }

        private void FollowPetCompanion()
        {
            if (petCompanion == null || !IsVisible) return;
            petCompanion.FollowBoard(new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height));
        }

        // Esc in the TODO window: close whatever panel is open first; with nothing open, hide the window.
        // The app keeps running in the tray, and the tray icon (열기) brings the window back.
        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            if (InlineHotKeyBox.IsKeyboardFocused) return; // Esc there only cancels typing a new shortcut
            e.Handled = true;
            if (RemoveConfirmPanel.Visibility == Visibility.Visible) CloseRemoveConfirmation();
            else if (InlineEditPanel.Visibility == Visibility.Visible) CloseInlineEdit();
            else if (MonitorPanel.Visibility == Visibility.Visible) CloseMonitorPanel();
            else if (InlineSettingsPanel.Visibility == Visibility.Visible && settingsHost == null) CloseInlineSettings(false);
            else Hide();
        }

        // Tray "열기" / double-click: bring back the window that was in use last.
        // If that window is already open but covered by other apps, it is raised to the front — never closed.
        public void OpenLastWindow()
        {
            if (appData?.MiniMode == true)
            {
                ShowMiniWindow();
                if (miniWindow != null) BringToFront(miniWindow);
                if (settingsHost != null) BringToFront(settingsHost); // its 설정 (still open) stays on top of it
            }
            else
            {
                ShowFullWindow();
                BringToFront(this);
            }
        }

        // Activate() alone can be ignored when another app owns the foreground; briefly making the window topmost
        // puts it above everything, then the 모든 창 위에 표시 setting is restored.
        private void BringToFront(Window window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Show();
            window.Topmost = true;
            window.Activate();
            NativeMethods.SetWidgetStacking(window, appData?.AlwaysOnTop == true);
            NativeMethods.RaiseAboveOtherApps(window); // desktop-owned: make sure it ends up above other apps
        }

        public void ShowFullWindow()
        {
            Show();
            miniWindow?.Close();
            Activate();
        }

        public void ShowMiniWindow()
        {
            if (appData == null) return;
            // Settings open in this window are cancelled; settings in their own window (opened from the mini window) stay.
            if (settingsHost == null) CloseInlineSettings(false);
            if (miniWindow == null)
            {
                miniWindow = new MiniWindow(appData, () =>
                {
                    bool saved = SaveDataSafely();
                    RefreshScheduleList(); // while this window is hidden that only refreshes the mini window
                    return saved;
                }, ShowMusic, ShowContacts, ShowMusicMenu, ExitApplication, this, OpenSettingsFromMini);
                if (_themeApplied && _themedPreset != appData.Appearance?.ThemePreset) miniWindow.ApplyTheme(_themedPreset, refresh: false);
                miniWindow.ThemeChosen += () => ApplyAppearance(appData.Appearance); // style picked in the mini window header
                miniWindow.SlotsChanged += () => petCompanion?.ReloadCharacter(); // the same pets beside the TODO window
                // Hidden (Esc → tray): no need to poll other apps' media every 1.5 s. Only the timer pauses — the known
                // sources stay, and showing the window again (ShowMiniWindow) restarts it with an immediate poll.
                miniWindow.IsVisibleChanged += (s, e) => { if (!(bool)e.NewValue) externalMediaTimer?.Stop(); };
                miniWindow.Closed += (s, e) =>
                {
                    miniWindow = null;
                    StopExternalMediaWatch();
                    // 앱 종료로 닫힐 때는 미니 모드 기록을 유지해 다음 실행에서 미니 창으로 복원합니다.
                    if (closingApp || Dispatcher.HasShutdownStarted) return;
                    appData.MiniMode = false;
                    SaveDataSafely(false);
                    Show();
                };
            }
            if (!appData.MiniMode)
            {
                appData.MiniMode = true;
                SaveDataSafely(false);
            }
            miniWindow.Show();
            miniWindow.Activate();
            StartExternalMediaWatch();
            Hide();
        }

        // Mini window menu → 설정: the mini window stays open and the same settings panel opens in its own window,
        // so theme / character size / character visibility changes show on the mini window before they are saved.
        // 저장 keeps them; 취소, X, Esc or closing the window puts the mini window back the way it was.
        private Window settingsHost;
        private Panel settingsHomeParent;
        private int settingsHomeIndex;
        private int previewOriginalScale;
        private List<int?> previewOriginalExtraScales;
        private bool previewOriginalVisible;
        private string previewOriginalSide;
        private int previewOriginalVertical, previewOriginalGap;
        private List<MiniPetSpot> previewOriginalSpots;
        private MiniWindow settingsPreviewMiniWindow;
        private int previewLastScale;
        private List<int?> previewLastExtraScales;
        private bool previewFirstScaleConflict;
        private List<bool> previewExtraScaleConflicts = new List<bool>();
        private bool previewLastVisible;
        private string previewLastSide;
        private int previewLastVertical, previewLastGap;
        private List<MiniPetSpot> previewLastSpots;
        private bool previewScaleConflict, previewVisibleConflict;
        private bool previewSideConflict, previewVerticalConflict, previewGapConflict, previewSpotsConflict;
        private bool previewApplying;
        private bool _inlineCharacterSideTouched, _inlineCharacterVerticalTouched, _inlineCharacterGapTouched;
        private bool settingsHostClosing;

        private void OpenSettingsFromMini()
        {
            if (appData == null) return;
            if (settingsHost != null) { BringToFront(settingsHost); return; }
            if (InlineSettingsPanel.Visibility == Visibility.Visible) CloseInlineSettings(false);
            SettingsButton_Click(this, new RoutedEventArgs());
            previewOriginalScale = appData.MiniCharacterScale;
            previewOriginalExtraScales = (appData.MiniExtraCharacters ?? new List<MiniCharacterSlot>()).Select(s => s.Scale).ToList();
            previewOriginalVisible = appData.MiniCharacterVisible;
            previewOriginalSide = appData.MiniCharacterSide;
            previewOriginalVertical = appData.MiniCharacterVertical;
            previewOriginalGap = appData.MiniCharacterGap;
            previewOriginalSpots = (appData.MiniPetSpots ?? new List<MiniPetSpot>()).Select(MiniWindow.CopySpot).ToList();
            CaptureMiniPreviewState();
            settingsPreviewMiniWindow = miniWindow;
            miniWindow.PetsChanged += MiniPetsChangedWhileSettingsOpen;
            miniWindow.MiniSettingsStateChanged += MiniPetsChangedWhileSettingsOpen;

            settingsHomeParent = (Panel)InlineSettingsPanel.Parent;
            settingsHomeIndex = settingsHomeParent.Children.IndexOf(InlineSettingsPanel);
            settingsHomeParent.Children.Remove(InlineSettingsPanel);
            settingsHost = new Window
            {
                Title = "설정", Width = 420, Height = Math.Min(720, SystemParameters.WorkArea.Height - 40),
                MinWidth = 340, MinHeight = 360, Content = InlineSettingsPanel,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = (System.Windows.Media.Brush)InlineSettingsPanel.Background
            };
            settingsHost.SetResourceReference(Window.BackgroundProperty, "AuxCanvasBrush"); // follows the theme
            ChromelessWindow.Apply(settingsHost, addCloseButton: false); // no title bar or taskbar button; the panel has its own X
            settingsHost.Closing += (s, e) =>
            {
                // Window X: same as 취소 (the panel goes back home; this window is already closing).
                if (settingsHost == null) return;
                settingsHostClosing = true;
                CloseInlineSettings(false);
                settingsHostClosing = false;
            };
            // Esc: same as 취소. Bubbling (KeyDown), so an open drop-down closes first and the shortcut box keeps its own Esc.
            settingsHost.KeyDown += (s, e) =>
            {
                if (e.Key != Key.Escape || InlineHotKeyBox.IsKeyboardFocused || settingsHost == null) return;
                e.Handled = true;
                CloseInlineSettings(false);
            };
            settingsHost.Show();
            settingsHost.Activate();
        }

        /// <summary>Settings shown for the mini window: preview a draft value on it right away.</summary>
        private void PreviewMiniSettings()
        {
            if (settingsHost == null || miniWindow == null || _inlineSettingsLoading) return;
            previewApplying = true;
            try
            {
                // Each control owns only the value the user changed. A second window can still change the others.
                if (_inlineCharacterScaleTouched && !previewScaleConflict) miniWindow.SetAllCharacterScales(_inlineCharacterScaleDraft);
                if (_inlineCharacterVisibleTouched && !previewVisibleConflict) miniWindow.SetCharacterVisible(InlineCharacterVisibleToggle.IsChecked == true);
                ApplyCharacterPlacementSetting();
                if (_inlinePetSpotsReset && !previewSpotsConflict) miniWindow.RestorePetSpots(new List<MiniPetSpot>());
            }
            finally { previewApplying = false; CaptureMiniPreviewState(); }
        }

        private void CaptureMiniPreviewState()
        {
            if (appData == null) return;
            previewLastScale = appData.MiniCharacterScale;
            previewLastExtraScales = (appData.MiniExtraCharacters ?? new List<MiniCharacterSlot>()).Select(s => s.Scale).ToList();
            previewLastVisible = appData.MiniCharacterVisible;
            previewLastSide = appData.MiniCharacterSide;
            previewLastVertical = appData.MiniCharacterVertical;
            previewLastGap = appData.MiniCharacterGap;
            previewLastSpots = (appData.MiniPetSpots ?? new List<MiniPetSpot>()).Select(MiniWindow.CopySpot).ToList();
        }

        private void MiniPetsChangedWhileSettingsOpen()
        {
            if (previewApplying || appData == null) return;
            if (appData.MiniCharacterScale != previewLastScale || previewFirstScaleConflict)
            {
                previewScaleConflict = previewFirstScaleConflict = true;
                previewOriginalScale = appData.MiniCharacterScale;
            }
            var currentExtraScales = (appData.MiniExtraCharacters ?? new List<MiniCharacterSlot>()).Select(s => s.Scale).ToList();
            if (!currentExtraScales.SequenceEqual(previewLastExtraScales ?? new List<int?>())) previewScaleConflict = true;
            for (int i = 0; i < Math.Min(currentExtraScales.Count, previewLastExtraScales?.Count ?? 0); i++)
            {
                if (currentExtraScales[i] == previewLastExtraScales[i] &&
                    (i >= previewExtraScaleConflicts.Count || !previewExtraScaleConflicts[i])) continue;
                while (previewExtraScaleConflicts.Count <= i) previewExtraScaleConflicts.Add(false);
                previewExtraScaleConflicts[i] = true;
                previewOriginalExtraScales[i] = currentExtraScales[i];
            }
            if (appData.MiniCharacterVisible != previewLastVisible) previewVisibleConflict = true;
            if (appData.MiniCharacterSide != previewLastSide) { previewSideConflict = true; previewSpotsConflict = true; }
            if (appData.MiniCharacterVertical != previewLastVertical) { previewVerticalConflict = true; previewSpotsConflict = true; }
            if (appData.MiniCharacterGap != previewLastGap) { previewGapConflict = true; previewSpotsConflict = true; }
            if (!SameMiniSpots(appData.MiniPetSpots, previewLastSpots)) previewSpotsConflict = true;
        }

        private static bool SameMiniSpots(IEnumerable<MiniPetSpot> a, IEnumerable<MiniPetSpot> b)
        {
            var left = (a ?? Enumerable.Empty<MiniPetSpot>()).ToList();
            var right = (b ?? Enumerable.Empty<MiniPetSpot>()).ToList();
            if (left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
            {
                var x = left[i]; var y = right[i];
                if (x == null || y == null) { if (x != y) return false; continue; }
                if (x.Left != y.Left || x.Top != y.Top || x.EdgeX != y.EdgeX || x.OffsetX != y.OffsetX || x.EdgeY != y.EdgeY || x.OffsetY != y.OffsetY || x.Key != y.Key) return false;
            }
            return true;
        }

        private void InlineCharacterVisibleToggle_Click(object sender, RoutedEventArgs e)
        {
            if (previewVisibleConflict)
            {
                previewOriginalVisible = appData.MiniCharacterVisible;
                previewVisibleConflict = false;
            }
            _inlineCharacterVisibleTouched = true;
            PreviewMiniSettings();
        }

        // ---- 캐릭터 위치 (달력 기준) ----
        private string DraftCharacterSide => InlineCharacterSideCombo.SelectedIndex == 1 ? "Right" : "Left";

        private void InlineCharacterPlacement_Changed(object sender, RoutedEventArgs e)
        {
            if (InlineCharacterGapText == null || InlineCharacterVerticalText == null) return; // still loading the XAML
            if (!_inlineSettingsLoading)
            {
                bool rebaseSpots = previewSpotsConflict || previewSideConflict || previewVerticalConflict || previewGapConflict;
                if (ReferenceEquals(sender, InlineCharacterSideCombo))
                {
                    if (previewSideConflict) { previewOriginalSide = appData.MiniCharacterSide; previewSideConflict = false; }
                    _inlineCharacterSideTouched = true;
                }
                else if (ReferenceEquals(sender, InlineCharacterVerticalSlider))
                {
                    if (previewVerticalConflict) { previewOriginalVertical = appData.MiniCharacterVertical; previewVerticalConflict = false; }
                    _inlineCharacterVerticalTouched = true;
                }
                else if (ReferenceEquals(sender, InlineCharacterGapSlider))
                {
                    if (previewGapConflict) { previewOriginalGap = appData.MiniCharacterGap; previewGapConflict = false; }
                    _inlineCharacterGapTouched = true;
                }
                if (rebaseSpots)
                {
                    previewOriginalSpots = (appData.MiniPetSpots ?? new List<MiniPetSpot>()).Select(MiniWindow.CopySpot).ToList();
                    previewSpotsConflict = false;
                }
            }
            UpdateCharacterPlacementLabels();
            PreviewMiniSettings();
        }

        private void UpdateCharacterPlacementLabels()
        {
            int vertical = (int)Math.Round(InlineCharacterVerticalSlider.Value), gap = (int)Math.Round(InlineCharacterGapSlider.Value);
            InlineCharacterVerticalText.Text = vertical >= 100 ? "아래" : vertical <= 0 ? "위" : vertical == 50 ? "가운데" : vertical + "%";
            InlineCharacterGapText.Text = gap < 0 ? "겹침 " + (-gap) + "px" : gap + "px";
        }

        // 저장: the mini window moves its pets now; without a mini window the values are just saved.
        private void ApplyCharacterPlacementSetting()
        {
            if (!_inlineCharacterSideTouched && !_inlineCharacterVerticalTouched && !_inlineCharacterGapTouched) return;
            string side = _inlineCharacterSideTouched && !previewSideConflict ? DraftCharacterSide : appData.MiniCharacterSide;
            int vertical = _inlineCharacterVerticalTouched && !previewVerticalConflict ? (int)Math.Round(InlineCharacterVerticalSlider.Value) : appData.MiniCharacterVertical;
            int gap = _inlineCharacterGapTouched && !previewGapConflict ? (int)Math.Round(InlineCharacterGapSlider.Value) : appData.MiniCharacterGap;
            if (miniWindow != null) { miniWindow.SetCharacterPlacement(side, vertical, gap); return; }
            // Same rules as the open mini window: a new side or gap puts dragged pets back; a new height resets their heights.
            vertical = Math.Max(0, Math.Min(100, vertical));
            gap = Math.Max(-80, Math.Min(60, gap));
            bool placementChanged = side != appData.MiniCharacterSide || gap != appData.MiniCharacterGap || vertical != appData.MiniCharacterVertical;
            if (side != appData.MiniCharacterSide || gap != appData.MiniCharacterGap) appData.MiniPetSpots?.Clear();
            else if (vertical != appData.MiniCharacterVertical && appData.MiniPetSpots != null) foreach (var spot in appData.MiniPetSpots) { spot.Top = null; spot.EdgeY = null; }
            appData.MiniCharacterSide = side;
            appData.MiniCharacterVertical = vertical;
            appData.MiniCharacterGap = gap;
            // ...and, like there, the characters in the slots forget the spots they no longer have (else they came back).
            if (placementChanged) MiniWindow.RememberMiniSpots(appData);
        }

        // Called when the settings close: undo the preview on cancel and put the panel back into this window.
        private void ReturnSettingsPanel(bool commit)
        {
            if (settingsHost == null) return;
            if (settingsPreviewMiniWindow != null)
            {
                settingsPreviewMiniWindow.PetsChanged -= MiniPetsChangedWhileSettingsOpen;
                settingsPreviewMiniWindow.MiniSettingsStateChanged -= MiniPetsChangedWhileSettingsOpen;
            }
            if (!commit && appData != null)
            {
                var latestSpots = (appData.MiniPetSpots ?? new List<MiniPetSpot>()).Select(MiniWindow.CopySpot).ToList();
                bool restoreOpeningSpots = !previewSpotsConflict && SameMiniSpots(latestSpots, previewLastSpots);
                var cancelSpotSource = restoreOpeningSpots ? previewOriginalSpots : latestSpots;
                var cancelSpots = (cancelSpotSource ?? new List<MiniPetSpot>()).Select(MiniWindow.CopySpot).ToList();
                bool restoreFirstScale = _inlineCharacterScaleTouched && appData.MiniCharacterScale == previewLastScale;
                var restoreExtraScales = new List<bool>();
                bool sameExtraCount = (appData.MiniExtraCharacters?.Count ?? 0) == (previewLastExtraScales?.Count ?? 0);
                if (sameExtraCount)
                    for (int i = 0; i < (appData.MiniExtraCharacters?.Count ?? 0); i++)
                        restoreExtraScales.Add(_inlineCharacterScaleTouched && appData.MiniExtraCharacters[i].Scale == previewLastExtraScales[i]);

                if (miniWindow != null)
                {
                    previewApplying = true;
                    try
                    {
                        if (restoreFirstScale || restoreExtraScales.Any(x => x))
                            miniWindow.RestorePetScalesOwned(previewOriginalScale, previewOriginalExtraScales ?? new List<int?>(), restoreFirstScale, restoreExtraScales);
                        if (_inlineCharacterVisibleTouched && !previewVisibleConflict && appData.MiniCharacterVisible == previewLastVisible) miniWindow.SetCharacterVisible(previewOriginalVisible);
                        bool restoreSide = _inlineCharacterSideTouched && !previewSideConflict && appData.MiniCharacterSide == previewLastSide;
                        bool restoreVertical = _inlineCharacterVerticalTouched && !previewVerticalConflict && appData.MiniCharacterVertical == previewLastVertical;
                        bool restoreGap = _inlineCharacterGapTouched && !previewGapConflict && appData.MiniCharacterGap == previewLastGap;
                        if (restoreSide || restoreVertical || restoreGap)
                            miniWindow.SetCharacterPlacement(restoreSide ? previewOriginalSide : appData.MiniCharacterSide,
                                restoreVertical ? previewOriginalVertical : appData.MiniCharacterVertical,
                                restoreGap ? previewOriginalGap : appData.MiniCharacterGap);
                        if (!SameMiniSpots(appData.MiniPetSpots, cancelSpots)) miniWindow.RestorePetSpots(cancelSpots);
                    }
                    finally { previewApplying = false; }
                }
                else
                {
                    // The mini window closed during this preview, so restore its data and per-character spot memory directly.
                    if (restoreFirstScale)
                    {
                        appData.MiniCharacterScale = previewOriginalScale;
                    }
                    var originalExtraScales = previewOriginalExtraScales ?? new List<int?>();
                    for (int i = 0; i < (appData.MiniExtraCharacters?.Count ?? 0) && i < originalExtraScales.Count && i < restoreExtraScales.Count; i++)
                        if (restoreExtraScales[i]) appData.MiniExtraCharacters[i].Scale = originalExtraScales[i];
                    if (_inlineCharacterVisibleTouched && !previewVisibleConflict && appData.MiniCharacterVisible == previewLastVisible) appData.MiniCharacterVisible = previewOriginalVisible;
                    bool restoreSide = _inlineCharacterSideTouched && !previewSideConflict && appData.MiniCharacterSide == previewLastSide;
                    bool restoreVertical = _inlineCharacterVerticalTouched && !previewVerticalConflict && appData.MiniCharacterVertical == previewLastVertical;
                    bool restoreGap = _inlineCharacterGapTouched && !previewGapConflict && appData.MiniCharacterGap == previewLastGap;
                    if (restoreSide) appData.MiniCharacterSide = previewOriginalSide;
                    if (restoreVertical) appData.MiniCharacterVertical = previewOriginalVertical;
                    if (restoreGap) appData.MiniCharacterGap = previewOriginalGap;
                    if (!SameMiniSpots(appData.MiniPetSpots, cancelSpots))
                    {
                        appData.MiniPetSpots = cancelSpots;
                        if (restoreOpeningSpots) MiniWindow.RememberMiniSpots(appData);
                    }
                    SaveDataSafely();
                }
            }
            var host = settingsHost;
            settingsHost = null;
            ResetMiniPreviewOwnership();
            host.Content = null;
            settingsHomeParent.Children.Insert(Math.Min(settingsHomeIndex, settingsHomeParent.Children.Count), InlineSettingsPanel);
            if (!settingsHostClosing) host.Close();
        }

        private void ResetMiniPreviewOwnership()
        {
            _inlineCharacterSideTouched = _inlineCharacterVerticalTouched = _inlineCharacterGapTouched = false;
            previewScaleConflict = previewVisibleConflict = previewSideConflict = previewVerticalConflict = previewGapConflict = previewSpotsConflict = false;
            previewFirstScaleConflict = false;
            previewExtraScaleConflicts = new List<bool>();
        }

        private void RebaseConflictedMiniScales()
        {
            if (appData == null) return;
            if (previewFirstScaleConflict || appData.MiniCharacterScale != previewLastScale)
                previewOriginalScale = appData.MiniCharacterScale;
            var current = (appData.MiniExtraCharacters ?? new List<MiniCharacterSlot>()).Select(s => s.Scale).ToList();
            for (int i = 0; i < Math.Min(current.Count, previewOriginalExtraScales?.Count ?? 0); i++)
                if (i < previewExtraScaleConflicts.Count && previewExtraScaleConflicts[i] ||
                    i < (previewLastExtraScales?.Count ?? 0) && current[i] != previewLastExtraScales[i])
                    previewOriginalExtraScales[i] = current[i];
            previewScaleConflict = previewFirstScaleConflict = false;
            previewExtraScaleConflicts = new List<bool>();
        }

        public void ShowMusic()
        {
            if (appData == null) return;
            EnsureMusicWindow();
            musicWindow.ShowPlayer();
        }

        private void ShowMusicMenu(FrameworkElement target)
        {
            if (appData == null) return;
            EnsureMusicWindow();
            musicWindow.OpenQuickMenu(target);
        }

        // Mini window player bar → the (possibly hidden) music window, created on first use.
        private MusicPlaylist SelectedPlaylist() => appData?.Music.Playlists.FirstOrDefault(p => p.Id == appData.Music.SelectedPlaylistId)
            ?? appData?.Music.Playlists.FirstOrDefault();
        // The bar controls one source, picked in its dropdown: one of the widget's playlists, or another app that is playing
        // media (YouTube / YouTube Music in a browser, Spotify, …) through Windows media sessions.
        private string selectedExternalApp; // null = the widget's own playlist
        private List<SystemMediaService.NowPlaying> externalSources = new List<SystemMediaService.NowPlaying>();
        private SystemMediaService.NowPlaying externalMedia => selectedExternalApp == null ? null
            : externalSources.FirstOrDefault(s => string.Equals(s.AppId, selectedExternalApp, StringComparison.OrdinalIgnoreCase));
        private bool UseExternal => selectedExternalApp != null;
        bool IMusicControls.HasTracks => UseExternal ? externalMedia != null : (musicWindow?.HasTracks ?? SelectedPlaylist()?.Tracks.Count > 0);
        bool IMusicControls.IsPlaying => UseExternal ? externalMedia?.IsPlaying == true : musicWindow?.IsPlaying == true;
        string IMusicControls.NowPlaying => UseExternal ? externalMedia?.Display : musicWindow?.NowPlayingTitle;
        IReadOnlyList<SystemMediaService.NowPlaying> IMusicControls.ExternalSources => externalSources;
        string IMusicControls.SelectedExternal => selectedExternalApp;
        string IMusicControls.SourceName => UseExternal ? (externalMedia?.SourceName ?? "다른 앱") : ((IMusicControls)this).CurrentPlaylist?.Name;
        async void IMusicControls.SelectExternal(string appId)
        {
            try
            {
                if (appData == null || string.IsNullOrEmpty(appId)) return;
                if (musicWindow?.IsPlaying == true) await musicWindow.TogglePlayAsync(); // one thing plays at a time
                selectedExternalApp = appId;
                externalVolumeReads = 0; // read the newly picked app's level on the next poll
                UpdateExternalPollInterval();
                MusicWindow.RaisePlaybackChanged();
            }
            catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { } // async void: a failure here must not end the app
        }
        PlayMode IMusicControls.Mode => appData?.Music.RepeatOne == true ? PlayMode.RepeatOne : appData?.Music.Shuffle == true ? PlayMode.Shuffle : PlayMode.Sequential;
        // Bar's settings button: a second press while the playlist settings window is open on screen closes it
        // (if music is playing it keeps playing from the hidden player).
        TimeSpan? IMusicControls.Position => UseExternal ? externalMedia?.PositionNow : musicWindow?.Position;
        TimeSpan? IMusicControls.Duration => UseExternal ? externalMedia?.Duration : musicWindow?.Duration;
        async void IMusicControls.Seek(TimeSpan position)
        {
            try
            {
                if (UseExternal) { await SystemMediaService.SeekAsync(selectedExternalApp, position); PollExternalMediaSoon(); }
                else musicWindow?.Seek(position);
            }
            catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
        }

        void IMusicControls.ToggleSettings()
        {
            if (appData == null) return;
            if (musicWindow != null && musicWindow.IsVisible && !musicWindow.InBackground && musicWindow.WindowState != WindowState.Minimized)
                musicWindow.Close();
            else ShowMusic();
        }

        async void IMusicControls.TogglePlay()
        {
            try
            {
                if (appData == null) return;
                if (UseExternal) { await SystemMediaService.TogglePlayPauseAsync(selectedExternalApp); PollExternalMediaSoon(); return; }
                EnsureMusicWindow(); await musicWindow.TogglePlayAsync();
            }
            catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
        }
        async void IMusicControls.Next()
        {
            try
            {
                if (appData == null) return;
                if (UseExternal) { await SystemMediaService.NextAsync(selectedExternalApp); PollExternalMediaSoon(); return; }
                EnsureMusicWindow(); await musicWindow.NextAsync();
            }
            catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
        }
        async void IMusicControls.Previous()
        {
            try
            {
                if (appData == null) return;
                if (UseExternal) { await SystemMediaService.PreviousAsync(selectedExternalApp); PollExternalMediaSoon(); return; }
                EnsureMusicWindow(); await musicWindow.PreviousAsync();
            }
            catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
        }

        // ---- Other apps' media (polled while the mini window is open) ----
        // Every 1.5 s while another app is the source or the source drop-down is open (and right away when it opens);
        // otherwise every 5 s, just to keep the drop-down's list of playing apps roughly current.
        private static readonly TimeSpan FastExternalPoll = TimeSpan.FromSeconds(1.5), SlowExternalPoll = TimeSpan.FromSeconds(5);
        private DispatcherTimer externalMediaTimer;
        private bool pollingExternalMedia;
        private System.Windows.Controls.Primitives.Popup watchedSourcePopup, watchedVolumePopup; // the mini window's drop-down / volume bar
        private int externalVolumeReads;

        private void StartExternalMediaWatch()
        {
            if (externalMediaTimer == null)
            {
                externalMediaTimer = new DispatcherTimer { Interval = SlowExternalPoll };
                externalMediaTimer.Tick += async (s, e) =>
                {
                    try { await PollExternalMediaAsync(); }
                    catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { } // a timer tick must never end the app
                };
            }
            WatchMiniMusicPopups();
            UpdateExternalPollInterval();
            externalMediaTimer.Start();
            PollExternalMediaSoon();
        }

        private void StopExternalMediaWatch()
        {
            externalMediaTimer?.Stop();
            externalSources = new List<SystemMediaService.NowPlaying>();
            WatchMiniMusicPopups(detach: true); // the closed mini window's popups are not kept alive from here
        }

        // The mini window's source drop-down and volume bar decide how eagerly other apps are read (found by name: the
        // mini window stays unaware of the polling).
        private void WatchMiniMusicPopups(bool detach = false)
        {
            var source = detach ? null : miniWindow?.FindName("PlaylistPopup") as System.Windows.Controls.Primitives.Popup;
            var volume = detach ? null : miniWindow?.FindName("VolumePopup") as System.Windows.Controls.Primitives.Popup;
            if (source != watchedSourcePopup)
            {
                if (watchedSourcePopup != null) { watchedSourcePopup.Opened -= SourcePopup_Opened; watchedSourcePopup.Closed -= SourcePopup_Closed; }
                watchedSourcePopup = source;
                if (source != null) { source.Opened += SourcePopup_Opened; source.Closed += SourcePopup_Closed; }
            }
            watchedVolumePopup = volume;
        }

        private void SourcePopup_Opened(object sender, EventArgs e) { UpdateExternalPollInterval(); PollExternalMediaSoon(0); }
        private void SourcePopup_Closed(object sender, EventArgs e) => UpdateExternalPollInterval();

        private void UpdateExternalPollInterval()
        {
            if (externalMediaTimer == null) return;
            var interval = UseExternal || watchedSourcePopup?.IsOpen == true ? FastExternalPoll : SlowExternalPoll;
            if (externalMediaTimer.Interval != interval) externalMediaTimer.Interval = interval;
        }

        private void PollExternalMediaSoon(int delayMs = 300) => Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (delayMs > 0) await System.Threading.Tasks.Task.Delay(delayMs);
                await PollExternalMediaAsync();
            }
            catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
        }));

        // Backup for older WebView2 runtimes: the widget's own hidden player may show up as a WebView2 media session playing
        // one of our songs. The titles to compare are built only when such a session is there, and kept while the
        // playlists keep their size (rebuilt at most every 30 s otherwise).
        private HashSet<string> ownTitlesCache;
        private int ownTitlesKey = -1;
        private DateTime ownTitlesAt;

        private HashSet<string> OwnTrackTitles()
        {
            int key = appData?.Music.Playlists.Sum(p => p.Tracks.Count + 1) ?? 0;
            if (ownTitlesCache == null || key != ownTitlesKey || DateTime.UtcNow - ownTitlesAt > TimeSpan.FromSeconds(30))
            {
                ownTitlesCache = new HashSet<string>(appData?.Music.Playlists.SelectMany(p => p.Tracks).Select(t => t.Title).Where(t => t != null)
                    ?? Enumerable.Empty<string>());
                ownTitlesKey = key;
                ownTitlesAt = DateTime.UtcNow;
            }
            var titles = new HashSet<string>(ownTitlesCache);
            if (musicWindow?.NowPlayingTitle != null) titles.Add(musicWindow.NowPlayingTitle);
            return titles;
        }

        private static bool IsWebViewSession(SystemMediaService.NowPlaying n) =>
            (n.AppId ?? "").IndexOf("msedgewebview2", StringComparison.OrdinalIgnoreCase) >= 0;

        private async System.Threading.Tasks.Task PollExternalMediaAsync()
        {
            if (pollingExternalMedia) return;
            pollingExternalMedia = true;
            try
            {
                var now = await SystemMediaService.GetAllAsync();
                if (now.Any(IsWebViewSession))
                {
                    var ownTitles = OwnTrackTitles();
                    now = now.Where(n => !(IsWebViewSession(n) && ownTitles.Contains(n.Title))).ToList();
                }
                string Signature(IEnumerable<SystemMediaService.NowPlaying> list) => string.Join("|", list.Select(n => n.AppId + ":" + n.SourceName + ":" + n.Display + ":" + n.IsPlaying));
                bool changed = Signature(now) != Signature(externalSources);
                externalSources = now;
                // The picked app's volume may also be changed elsewhere (Windows volume mixer, the site's own slider). It is
                // only shown on the music bar: read it every poll while the volume bar is open, every other poll (~3 s)
                // otherwise, never while the bar is hidden or a level of ours is still being applied.
                double? volumeBefore = externalVolume;
                bool barShown = appData?.MiniPlayerVisible != false;
                bool eager = watchedVolumePopup?.IsOpen == true || externalVolumeApp != selectedExternalApp;
                if (UseExternal && barShown && externalVolumeSetter?.IsBusy != true && (eager || externalVolumeReads++ % 2 == 0))
                {
                    // Core Audio enumeration costs ~6 ms: do it off the UI thread (COM objects are created per call on that thread).
                    string app = selectedExternalApp;
                    float? read = await System.Threading.Tasks.Task.Run(() => AppVolumeService.GetVolume(app));
                    if (app == selectedExternalApp && externalVolumeSetter?.IsBusy != true) { externalVolumeApp = app; externalVolume = read; }
                }
                bool volumeChanged = UseExternal && externalVolume.HasValue != volumeBefore.HasValue ||
                    externalVolume.HasValue && volumeBefore.HasValue && Math.Abs(externalVolume.Value - volumeBefore.Value) > 0.004;
                if (changed || volumeChanged) MusicWindow.RaisePlaybackChanged();
            }
            finally { pollingExternalMedia = false; }
        }
        void IMusicControls.SetMode(PlayMode mode) { if (appData == null) return; EnsureMusicWindow(); musicWindow.SetPlayMode(mode); }

        // Volume: with another app picked in the music bar, the speaker button shows and sets THAT app's volume
        // (its Windows mixer level, via AppVolumeService); otherwise the widget's own player. If the app has no
        // audio session yet (not playing) or Core Audio fails, it falls back to the widget's volume.
        private double? externalVolume;
        private string externalVolumeApp;

        private double? ExternalVolume(bool refresh)
        {
            // Cached per picked app, including "no audio session yet" (null): the poll refreshes it (every ~3 s, every 1.5 s
            // while the volume bar is open), so reads from the bar (every playback change) never repeat the ~6 ms Core Audio lookup.
            if (!refresh && externalVolumeApp == selectedExternalApp) return externalVolume;
            externalVolumeApp = selectedExternalApp;
            externalVolume = selectedExternalApp == null ? null : (double?)AppVolumeService.GetVolume(selectedExternalApp);
            return externalVolume;
        }

        double IMusicControls.Volume
        {
            get
            {
                if (UseExternal) { double? app = ExternalVolume(refresh: false); if (app.HasValue) return app.Value; }
                return musicWindow?.Volume ?? appData?.Music.Volume ?? 0.5;
            }
        }

        void IMusicControls.SetVolume(double value)
        {
            if (appData == null) return;
            value = Math.Max(0, Math.Min(1, value));
            if (UseExternal)
            {
                // Another app is the source: only its volume may change. If it has no audio session yet, nothing changes
                // (quietly changing the widget's own player instead was confusing). The bar shows the new level at once;
                // Core Audio gets it off the UI thread, newest level only (dragging the knob no longer stutters), and the
                // bar goes back to the app's last real level if the app took none.
                if (externalVolumeSetter == null)
                {
                    externalVolumeSetter = new AppVolumeService.CoalescingSetter(AppVolumeService.SetVolume);
                    externalVolumeSetter.Applied += ExternalVolumeApplied;
                }
                if (!externalVolumeSetter.IsBusy) externalVolumeTaken = externalVolumeApp == selectedExternalApp ? externalVolume : null;
                externalVolumeApp = selectedExternalApp;
                externalVolume = value;
                externalVolumeSetter.Set(selectedExternalApp, (float)value);
                return;
            }
            if (musicWindow != null) musicWindow.SetVolume(value);
            else appData.Music.Volume = value;
        }

        private AppVolumeService.CoalescingSetter externalVolumeSetter;
        private double? externalVolumeTaken; // the picked app's last level it really took (shown again when a newer one fails)

        private void ExternalVolumeApplied(string app, float level, bool ok, bool last)
        {
            if (!string.Equals(app, selectedExternalApp, StringComparison.OrdinalIgnoreCase)) return; // another source was picked meanwhile
            if (ok) { externalVolumeTaken = level; return; }
            if (!last || externalVolumeApp != selectedExternalApp) return;
            externalVolume = externalVolumeTaken;
            MusicWindow.RaisePlaybackChanged();
        }
        IReadOnlyList<MusicPlaylist> IMusicControls.Playlists => appData?.Music.Playlists ?? new List<MusicPlaylist>();
        MusicPlaylist IMusicControls.CurrentPlaylist => musicWindow?.CurrentPlaylist ?? SelectedPlaylist();
        async void IMusicControls.SelectPlaylist(MusicPlaylist list)
        {
            try
            {
                if (appData == null || list == null) return;
                if (selectedExternalApp != null)
                {
                    string app = selectedExternalApp; selectedExternalApp = null;
                    UpdateExternalPollInterval();
                    if (externalSources.Any(s => s.AppId == app && s.IsPlaying)) await SystemMediaService.PauseAsync(app); // one thing plays at a time
                    MusicWindow.RaisePlaybackChanged();
                }
                EnsureMusicWindow();
                bool wasPlaying = musicWindow.IsPlaying;
                musicWindow.SelectPlaylist(list); // switching stops the old list
                if (wasPlaying && musicWindow.HasTracks) await musicWindow.TogglePlayAsync(); // keep the music going with the new list
            }
            catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
        }
        IReadOnlyList<MusicTrack> IMusicControls.Queue => musicWindow?.CurrentTracks ?? (IReadOnlyList<MusicTrack>)SelectedPlaylist()?.Tracks ?? new List<MusicTrack>();
        MusicTrack IMusicControls.Current => musicWindow?.PlayingTrack;
        async void IMusicControls.Play(MusicTrack track)
        {
            try { if (appData == null || track == null) return; EnsureMusicWindow(); await musicWindow.PlayFromListAsync(track); }
            catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
        }
        void IMusicControls.Move(MusicTrack track, int index) { if (appData == null || track == null) return; EnsureMusicWindow(); musicWindow.MoveTrack(track, index); }

        // Songs added from a single YouTube link used to be saved as "YouTube · <video id>". Look up their real titles once.
        private bool fillingYouTubeTitles;
        private async System.Threading.Tasks.Task FillYouTubeTitlesAsync()
        {
            if (appData == null || fillingYouTubeTitles) return;
            fillingYouTubeTitles = true;
            try
            {
                bool updated = false;
                var pending = appData.Music.Playlists.SelectMany(p => p.Tracks)
                    .Where(t => (t.Title ?? "").StartsWith("YouTube · ", StringComparison.Ordinal)).ToList();
                foreach (var track in pending)
                {
                    if (!FeatureRules.TryYouTube(track.Source, out string video, out _) || video == null) continue;
                    string title = await YouTubePlaylistService.FetchVideoTitleAsync(video);
                    if (string.IsNullOrWhiteSpace(title) || !(track.Title ?? "").StartsWith("YouTube · ", StringComparison.Ordinal)) continue;
                    track.Title = title;
                    updated = true;
                }
                if (!updated) return;
                SaveDataSafely(false);
                musicWindow?.RefreshTrackTitles();
                MusicWindowTitlesChanged();
            }
            finally { fillingYouTubeTitles = false; }
        }

        private void MusicWindowTitlesChanged() => miniWindow?.RefreshQueue();

        private void EnsureMusicWindow()
        {
            if (musicWindow == null)
            {
                musicWindow = new MusicWindow(appData.Music, () => SaveDataSafely(), ExitApplication);
                musicWindow.Closed += (s, e) => musicWindow = null;
            }
        }

        public void ExitApplication()
        {
            closingApp = true;
            trayService.Dispose();
            Application.Current.Shutdown();
        }

        private void ExitApplication_Click(object sender, RoutedEventArgs e) => ExitApplication();

        public void ShowContacts() => OpenContacts(null);
        private void OpenContacts(string message)
        {
            if (appData == null) return;
            if (contactWindow == null)
            {
                contactWindow = new ContactWindow(appData, communicationService, () => SaveDataSafely());
                contactWindow.Closed += (s, e) => contactWindow = null;
            }
            if (message != null) contactWindow.SetMessage(message);
            contactWindow.Show();
            if (contactWindow.WindowState == WindowState.Minimized) contactWindow.WindowState = WindowState.Normal;
            contactWindow.Activate();
        }

        private void ShareSchedule_Click(object sender, RoutedEventArgs e)
        {
            var item = GetScheduleItemFromContextMenu(sender);
            if (item != null) OpenContacts(item.Title + "\n마감: " + item.PeriodText + " (" + item.DDay + ")");
        }

        private void CompleteSchedule_Click(object sender, RoutedEventArgs e)
        {
            var item = GetScheduleItemFromContextMenu(sender);
            if (item == null) return;
            item.IsCompleted = !item.IsCompleted;
            SaveDataSafely();
            RefreshScheduleList();
        }

        private static string PickColor(string current)
        {
            using (var picker = new System.Windows.Forms.ColorDialog { FullOpen = true })
            {
                if (FeatureRules.IsColor(current)) picker.Color = System.Drawing.ColorTranslator.FromHtml("#" + current.Substring(current.Length - 6));
                return picker.ShowDialog() == System.Windows.Forms.DialogResult.OK
                    ? string.Format("#{0:X2}{1:X2}{2:X2}", picker.Color.R, picker.Color.G, picker.Color.B) : null;
            }
        }

        private void ScheduleColor_Click(object sender, RoutedEventArgs e)
        {
            var item = GetScheduleItemFromContextMenu(sender);
            if (item == null) return;
            string color = PickColor(item.Color ?? appData.Appearance.CardColor);
            if (color == null) return;
            item.Color = color;
            SaveDataSafely();
            RefreshScheduleList();
        }

        private void ResetScheduleColor_Click(object sender, RoutedEventArgs e)
        {
            var item = GetScheduleItemFromContextMenu(sender);
            if (item == null) return;
            item.Color = null;
            SaveDataSafely();
            RefreshScheduleList();
        }

        private void AppearanceColor_Click(object sender, RoutedEventArgs e)
        {
            if (_inlineSettingsDraft == null) return;
            var property = typeof(AppearanceSettings).GetProperty((string)((Button)sender).Tag);
            string color = PickColor((string)property.GetValue(_inlineSettingsDraft));
            if (color == null) return;
            property.SetValue(_inlineSettingsDraft, color);
            ApplyAppearance(_inlineSettingsDraft);
        }

        // The Codex characters were copied, replaced or removed (Codex installed, updated or uninstalled: CodexPets): the pets
        // read their characters again, in the mini window and beside the TODO window.
        private void WatchCodexPets()
        {
            Action codexChanged = () => Dispatcher.BeginInvoke(new Action(() =>
            {
                miniWindow?.ReloadCharacter();
                petCompanion?.ReloadCharacter();
            }));
            CodexPets.Changed += codexChanged;
            Closed += (s, e) => CodexPets.Changed -= codexChanged;
        }

        private void InitializeReminders()
        {
            reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            reminderTimer.Tick += async (s, e) => await CheckRemindersAsync();
            reminderTimer.Start();
            Dispatcher.BeginInvoke(new Action(async () => await CheckRemindersAsync()));
        }

        private async System.Threading.Tasks.Task CheckRemindersAsync()
        {
            if (checkingReminders || appData == null || !appData.Reminders.Enabled) return;
            checkingReminders = true;
            try
            {
                foreach (var item in appData.Schedules.ToList())
                {
                    // Recheck after every await: a user may complete, edit, or delete a task during a send.
                    foreach (string channel in new[] { "Desktop", "Telegram", "Kakao" })
                    {
                        var settings = appData.Reminders;
                        if (!appData.Schedules.Contains(item) || !FeatureRules.IsReminderDue(item, settings, DateTime.Now)) break;
                        if (!(channel == "Desktop" ? settings.Desktop : channel == "Telegram" ? settings.Telegram : settings.Kakao)) continue;
                        string key = FeatureRules.ReminderKey(item, settings);
                        if (item.ReminderReceipts.TryGetValue(channel, out string receipt) && receipt == key) continue;
                        string retryKey = item.Id + "|" + channel;
                        if (reminderRetries.TryGetValue(retryKey, out DateTime retry) && retry > DateTime.Now) continue;
                        try
                        {
                            string message = FeatureRules.ReminderMessage(item);
                            if (channel == "Desktop") trayService.Notify("할 일 마감 알림", message);
                            else if (channel == "Telegram") await communicationService.SendTelegramAsync(appData.Communication, message);
                            else await communicationService.SendKakaoAsync(appData.Communication, message, true);
                            item.ReminderReceipts[channel] = key;
                            reminderRetries.Remove(retryKey);
                            // That channel works again: the 연락 · 알림 button stops showing its last error.
                            if (reminderErrorChannel == channel) { ContactButton.ToolTip = null; reminderErrorChannel = null; }
                            SaveDataSafely();
                        }
                        catch (Exception ex) when (ex is InvalidOperationException || ex is UnauthorizedAccessException || ex is System.Security.Cryptography.CryptographicException)
                        {
                            if (!reminderRetries.ContainsKey(retryKey)) trayService.Notify("마감 알림 전송 실패", ex.Message);
                            reminderRetries[retryKey] = DateTime.Now.AddMinutes(5);
                            ContactButton.ToolTip = ex.Message;
                            reminderErrorChannel = channel;
                            contactWindow?.ReportStatus(ex.Message);
                            SaveDataSafely(); // Preserve a refreshed token even if the following send failed.
                        }
                    }
                }
            }
            finally { checkingReminders = false; }
        }
    }
}
