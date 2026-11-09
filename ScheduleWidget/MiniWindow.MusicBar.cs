using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ScheduleWidget
{
    public partial class MiniWindow
    {
        public void UpdatePlayerBar()
        {
            if (closed || PlayerBar == null) return;
            bool shown = !companion && player != null;
            PlayerBar.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            UpdateCalendarBottom();
            if (!shown)
            {
                // Hidden bar: its title stops scrolling and the seek line stops ticking.
                ApplyMarqueePlaying();
                seekTimer?.Stop();
                return;
            }
            CheckExternalSource();
            bool hasTracks = player.HasTracks, playing = player.IsPlaying, external = player.SelectedExternal != null;
            string title = player.NowPlaying;
            // The song stays in the same strip as the controls; playback never adds another row.
            PlayerTitleHost.ToolTip = title;
            // Reachable with Tab too: Enter / Space do what a click does.
            System.Windows.Automation.AutomationProperties.SetName(PlayerTitleHost, title ?? string.Empty);
            System.Windows.Automation.AutomationProperties.SetHelpText(PlayerTitleHost, external ? "재생 중인 창으로 이동" : "지금 재생 목록 열기");
            UpdatePlayerDetails();
            if (PlayerTitle.Text != (title ?? string.Empty)) { PlayerTitle.Text = title ?? string.Empty; StartTitleMarquee(); }
            else ApplyMarqueePlaying();
            UpdateVolumeIcon();
            string source = player.SourceName ?? "목록 없음";
            PlayerPlaylistText.Text = source;
            PlayerPlaylist.ToolTip = (player.AutomaticSource ? "자동 감지 · " : "") + "지금 조작하는 곳: " + source + " · 눌러서 소스 선택";
            if (PlaylistPopup.IsOpen) BuildSourceChoices();
            PlayIcon.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            PauseIcon.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
            PlayerPlay.ToolTip = playing ? "일시정지" : hasTracks ? "재생" : "플레이리스트가 비어 있어요";
            if (!string.IsNullOrEmpty(title)) PlayerPlay.ToolTip = title + " · " + PlayerPlay.ToolTip;
            PlayerPlay.IsEnabled = PlayerPrevious.IsEnabled = PlayerNext.IsEnabled = hasTracks;
            // 순차 / 랜덤 / 1곡 is the widget's own playlist order: another app keeps its own, so the button goes away meanwhile.
            PlayerMode.Visibility = external ? Visibility.Collapsed : Visibility.Visible;
            PlayMode mode = player.Mode;
            PlayerModeText.Text = mode == PlayMode.Shuffle ? "랜덤" : mode == PlayMode.RepeatOne ? "1곡" : "순차";
            ModeSequentialIcon.Visibility = mode == PlayMode.Sequential ? Visibility.Visible : Visibility.Collapsed;
            ModeShuffleIcon.Visibility = mode == PlayMode.Shuffle ? Visibility.Visible : Visibility.Collapsed;
            ModeRepeatOneIcon.Visibility = mode == PlayMode.RepeatOne ? Visibility.Visible : Visibility.Collapsed;
            PlayerMode.ToolTip = (mode == PlayMode.Shuffle ? "랜덤 재생" : mode == PlayMode.RepeatOne ? "한 곡 반복" : "순차 재생") +
                " · 누르면 " + (mode == PlayMode.Sequential ? "랜덤" : mode == PlayMode.Shuffle ? "한 곡 반복" : "순차") + "으로";
            UpdatePlayerBarLayout();
        }

        public const double PlayerBarHeight = 41;

        // Reserve the same amount before the first layout and throughout every playback state.
        private void UpdateCalendarBottom()
        {
            bool shown = PlayerBar.Visibility == Visibility.Visible;
            double bar = shown ? PlayerBarHeight + 12 : 8;
            if (Math.Abs(WeekCalendar.Margin.Bottom - bar) > 0.5) WeekCalendar.Margin = new Thickness(0, 4, 2, bar);
        }

        private void PlayerBar_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateCalendarBottom();
            UpdatePlayerBarLayout();
        }

        private void UpdatePlayerBarLayout()
        {
            if (PlayerModeText == null || PlayerTimes == null) return;
            double width = PlayerBar.ActualWidth > 0 ? PlayerBar.ActualWidth : Width - 2 * Edge - 2;
            bool compact = width < 420;
            PlayerOpen.Visibility = width < 300 ? Visibility.Collapsed : Visibility.Visible;
            PlayerPlaylist.Width = width >= 640 ? 126 : width >= 520 ? 108 : compact ? 28 : 78;
            PlayerPlaylist.Padding = new Thickness(compact ? 6 : 8, 0, compact ? 6 : 8, 0);
            PlayerPlaylistText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            PlayerPrevious.Width = PlayerPlay.Width = PlayerNext.Width = compact ? 24 : 28;
            PlayerVolume.Width = compact ? 24 : 26;
            PlayerModeText.Visibility = width < 520 ? Visibility.Collapsed : Visibility.Visible;
            PlayerMode.Padding = new Thickness(width < 520 ? 4 : 9, 0, width < 520 ? 4 : 9, 0);
            PlayerTimes.Visibility = width >= 640 ? Visibility.Visible : Visibility.Collapsed;
            PlayerTrackInfo.Margin = new Thickness(compact ? 4 : 8, 0, compact ? 4 : 8, 0);
        }

        private void PlayerPlay_Click(object sender, RoutedEventArgs e) { if (!RecentlyDragged) player?.TogglePlay(); }
        private void PlayerNext_Click(object sender, RoutedEventArgs e) { if (!RecentlyDragged) player?.Next(); }
        private void PlayerPrevious_Click(object sender, RoutedEventArgs e) { if (!RecentlyDragged) player?.Previous(); }
        private void PlayerOpen_Click(object sender, RoutedEventArgs e)
        {
            if (RecentlyDragged) return;
            CloseDayPopup();
            if (player != null) player.ToggleSettings(); else music?.Invoke();
        }

        // ---- Scrolling song title: enters from the right edge and leaves on the left, then repeats. ----
        private const double MarqueeSpeed = 45; // px per second

        // A new title (or a new width): measured here; ApplyMarqueePlaying starts it entering from the right if music plays.
        private void StartTitleMarquee()
        {
            StopTitleMarquee();
            marqueeHost = PlayerTitleHost.ActualWidth;
            if (string.IsNullOrEmpty(PlayerTitle.Text) || marqueeHost <= 0) { marqueeHost = 0; PlayerTitleShift.X = 0; return; }
            PlayerTitle.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            marqueeText = PlayerTitle.DesiredSize.Width;
            marqueeStart = marqueeHost / MarqueeSpeed; // the moment the title's start reaches the left edge
            marqueeFromRight = true;
            ApplyMarqueePlaying();
        }

        internal const int MarqueeFrameRate = 30;

        // Stopped and taken off the title: a clock left running (it repeats forever) would keep ticking, closed window and all.
        private void StopTitleMarquee()
        {
            var controller = marqueeClock?.Controller;
            if (controller != null) { controller.Stop(); controller.Remove(); }
            PlayerTitleShift.BeginAnimation(TranslateTransform.XProperty, null);
            marqueeClock = null;
        }

        // The title only scrolls while music plays and it can be seen (not hidden to the tray, not with the bar hidden).
        // Otherwise it rests with its start at the left edge, with no clock at all: even a paused animation clock keeps WPF
        // redrawing the whole transparent window every frame. Playing again scrolls on from the rest.
        private System.Windows.Media.Animation.AnimationClock marqueeClock;
        private double marqueeStart, marqueeHost, marqueeText; // marqueeHost 0: no title to move
        private bool marqueeFromRight;                          // a new title enters from the right edge; after a rest it moves on

        private void ApplyMarqueePlaying()
        {
            if (marqueeHost <= 0) return;
            if (player?.IsPlaying == true && IsVisible && !closed && PlayerBar.Visibility == Visibility.Visible)
            {
                if (marqueeClock != null) return; // already scrolling
                var run = new System.Windows.Media.Animation.DoubleAnimation(marqueeHost, -marqueeText, TimeSpan.FromSeconds((marqueeHost + marqueeText) / MarqueeSpeed))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                };
                // 30 frames a second is smooth at this speed (1.5 px a frame) and halves the redraws of the whole transparent window.
                System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(run, MarqueeFrameRate);
                marqueeClock = run.CreateClock();
                PlayerTitleShift.ApplyAnimationClock(TranslateTransform.XProperty, marqueeClock);
                if (!marqueeFromRight) marqueeClock.Controller.Seek(TimeSpan.FromSeconds(marqueeStart), System.Windows.Media.Animation.TimeSeekOrigin.BeginTime);
                marqueeFromRight = false;
                return;
            }
            StopTitleMarquee();
            PlayerTitleShift.X = 0; // the title's start at the left edge
            marqueeFromRight = false;
        }

        private void OnMiniVisibleChanged(bool shown)
        {
            if (closed) return;
            ApplyMarqueePlaying();
            if (!shown)
            {
                seekTimer?.Stop(); // picked up again below when shown
                // 캐릭터 설정 and a placement belong to pets on screen: hidden (the TODO window's pets go with that window; the
                // mini window to the tray), they end — never left floating, or placing pets nobody sees.
                petSettings?.Close();
                EndPetPlacement();
                return;
            }
            UpdatePlayerDetails();
            CheckExternalSource();
            // The TODO window's pets were only hidden meanwhile: actions changed in the mini window (or its 초기화) reach their
            // page now (sizes, 보이기 and spots come with the next layout).
            if (companion) for (int i = 0; i < SlotCount; i++) PostToPage(JsonConvert.SerializeObject(new { action = "animation", index = i, state = SlotAnimation(i) }));
            if (companion) SendPetFlips(); // and 좌우 반전 changed there meanwhile
            if (reattachWhenShown)
            {
                reattachWhenShown = false;
                Dispatcher.BeginInvoke(new Action(ReattachToDesktop), System.Windows.Threading.DispatcherPriority.Background);
            }
            // Shown again (from the tray): the screens may have changed meanwhile — the calendar must be reachable.
            if (!companion && (ensureWhenShown || HasSavedPlace))
            {
                ensureWhenShown = false;
                Dispatcher.BeginInvoke(new Action(() => EnsureBoardVisible("shown")), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void PlayerTitleHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged) StartTitleMarquee();
        }

        // ---- Volume: hovering the speaker shows a vertical volume bar above it; a click mutes / unmutes; the mouse wheel
        // (on the speaker or the bar) and ↑/↓ on the focused speaker change the volume by 5%. ----
        private bool syncingVolume;
        // What 음소거 해제 brings back: the last audible level, kept in the saved music settings so it survives a restart
        // while muted (50% when there is none).
        private double lastAudibleVolume
        {
            get => data.Music != null && data.Music.VolumeBeforeMute > 0.001 ? data.Music.VolumeBeforeMute : 0.5;
            set { if (data.Music != null && value > 0.001) data.Music.VolumeBeforeMute = Math.Min(1, value); }
        }
        private System.Windows.Threading.DispatcherTimer volumeBarTimer;

        // With another app as the source, its own last audible level (not saved): the widget's 음소거 해제 level is not
        // overwritten by the browser's.
        private double externalAudibleVolume;
        private string externalAudibleApp;

        private void UpdateVolumeIcon()
        {
            if (player == null) return;
            double volume = player.Volume;
            bool muted = volume <= 0.001;
            if (!muted) RememberAudible(volume); // also follows changes made in the other app / the volume mixer
            VolumeMuted.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
            VolumeWaveLow.Visibility = !muted ? Visibility.Visible : Visibility.Collapsed;
            VolumeWaveHigh.Visibility = volume > 0.5 ? Visibility.Visible : Visibility.Collapsed;
            PlayerVolume.ToolTip = null; // the bar above the speaker shows the level
            System.Windows.Automation.AutomationProperties.SetHelpText(PlayerVolume,
                "음량 " + Math.Round(volume * 100) + "% · 누르면 " + (muted ? "음소거 해제" : "음소거") + ", 휠이나 ↑↓로 5%씩");
            if (VolumePopup.IsOpen) SyncVolumeSlider();
        }

        // Keyboard focus keeps the bar only when it came from the keyboard (Tab / ↑↓): a mouse click also focuses the speaker,
        // and the bar must still go away once the pointer leaves.
        private bool volumeKeyboardShown;

        internal Func<bool> volumeHoverOverride = null; // tests: stands in for the pointer (a real one resting on a window on screen counts)

        private bool VolumeHovered => volumeHoverOverride?.Invoke() ??
            (PlayerVolume.IsMouseOver || VolumeBar.IsMouseOver || VolumeSlider.IsMouseCaptureWithin ||
            volumeKeyboardShown && PlayerVolume.IsKeyboardFocusWithin);

        // Pointer (or keyboard focus) on the speaker or on the bar: show the bar. Left both: hide it a moment later, so the
        // pointer can cross from the speaker onto the bar.
        private void PlayerVolume_HoverChanged(object sender, RoutedEventArgs e)
        {
            if (player == null) return;
            if (e.RoutedEvent == UIElement.GotKeyboardFocusEvent) volumeKeyboardShown = InputManager.Current.MostRecentInputDevice is KeyboardDevice;
            else if (e.RoutedEvent == UIElement.LostKeyboardFocusEvent) volumeKeyboardShown = false;
            if (VolumeHovered) ShowVolumeBar();
            else HideVolumeBarSoon();
        }

        private double volumeAtOpen = -1; // the level when the bar opened: closing it saves only when that changed
        private bool volumeBarHooked;

        internal void ShowVolumeBar()
        {
            volumeBarTimer?.Stop();
            if (VolumePopup.IsOpen || player == null) return;
            if (!volumeBarHooked)
            {
                volumeBarHooked = true;
                VolumePopup.CustomPopupPlacementCallback = CenterAboveSpeaker;
                // A StaysOpen popup does not go away with its window: hidden to the tray or closed, the bar must close too
                // (it would otherwise stay on the desktop, like the style drop-down did).
                IsVisibleChanged += (s, e) => { if (!(bool)e.NewValue) CloseVolumeBar(); };
                Closed += (s, e) => CloseVolumeBar();
                // Dragging the bar's knob and letting go outside the bar: releasing the capture raises no MouseLeave.
                VolumeBar.LostMouseCapture += (s, e) => HideVolumeBarSoon();
                // Save when the bar closes — at IsOpen = false itself: Popup.Closed only comes after the fade, and a quick
                // re-hover in between would reset the baseline first, losing the change.
                var isOpen = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                    System.Windows.Controls.Primitives.Popup.IsOpenProperty, typeof(System.Windows.Controls.Primitives.Popup));
                EventHandler closedNow = (s, e) => { if (!VolumePopup.IsOpen) SaveBarVolumeIfChanged(); };
                isOpen.AddValueChanged(VolumePopup, closedNow);
                Closed += (s, e) => isOpen.RemoveValueChanged(VolumePopup, closedNow);
            }
            volumeAtOpen = player.Volume;
            SyncVolumeSlider();
            RemeasurePopup(VolumePopup);
            VolumePopup.IsOpen = true;
        }

        private void CloseVolumeBar()
        {
            volumeBarTimer?.Stop();
            VolumePopup.IsOpen = false;
        }

        // Volume changes from a click / the wheel / ↑↓ are saved once they settle — a spin of the wheel is one save, not one
        // per notch (each rewrote schedules.json and refreshed the TODO list) — and not again when the bar closes. Another
        // app's level is that app's own: nothing of ours to save.
        private void SaveVolume()
        {
            if (player?.SelectedExternal == null) SaveSoon();
            if (VolumePopup.IsOpen && player != null) volumeAtOpen = player.Volume;
        }

        private void RememberAudible(double volume)
        {
            if (player?.SelectedExternal == null) { lastAudibleVolume = volume; return; }
            externalAudibleVolume = volume;
            externalAudibleApp = player.SelectedExternal;
        }

        // 음소거 해제: back to the level before the mute (the widget's saved one, or the picked app's own), else 50%.
        private double UnmuteLevel()
        {
            string app = player?.SelectedExternal;
            if (app == null) return lastAudibleVolume > 0.001 ? lastAudibleVolume : 0.5;
            return string.Equals(app, externalAudibleApp, StringComparison.OrdinalIgnoreCase) && externalAudibleVolume > 0.001 ? externalAudibleVolume : 0.5;
        }

        private void HideVolumeBarSoon()
        {
            if (!VolumePopup.IsOpen) return;
            if (volumeBarTimer == null)
            {
                volumeBarTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                volumeBarTimer.Tick += (s, e) => { volumeBarTimer.Stop(); HideVolumeBarIfLeft(); };
            }
            volumeBarTimer.Stop();
            volumeBarTimer.Start();
        }

        internal void HideVolumeBarIfLeft()
        {
            if (!VolumeHovered) VolumePopup.IsOpen = false;
        }

        // Centred right above the speaker (its transparent bottom strip touching the button); below it when there is no room.
        private static System.Windows.Controls.Primitives.CustomPopupPlacement[] CenterAboveSpeaker(Size popupSize, Size targetSize, Point offset) => new[]
        {
            new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point((targetSize.Width - popupSize.Width) / 2, -popupSize.Height),
                System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal),
            new System.Windows.Controls.Primitives.CustomPopupPlacement(new Point((targetSize.Width - popupSize.Width) / 2, targetSize.Height),
                System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)
        };

        /// <summary>음소거 ↔ 음소거 해제 (back to the level it had before).</summary>
        internal void ToggleMute()
        {
            if (player == null) return;
            if (player.Volume > 0.001) { RememberAudible(player.Volume); SetVolume(0); }
            else SetVolume(UnmuteLevel());
            SaveVolume(); // saved like any other volume change
        }

        private void SyncVolumeSlider()
        {
            syncingVolume = true;
            try
            {
                VolumeSlider.Value = Math.Round(player.Volume * 100);
                VolumeText.Text = Math.Round(player.Volume * 100) + "%";
            }
            finally { syncingVolume = false; }
        }

        private void PlayerVolume_Click(object sender, RoutedEventArgs e)
        {
            if (player == null || RecentlyDragged) return;
            ToggleMute();
        }

        private void PlayerVolume_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (player == null) return;
            e.Handled = true;
            SetVolume(player.Volume + (e.Delta > 0 ? 0.05 : -0.05));
            SaveVolume();
        }

        private void PlayerVolume_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (player == null || (e.Key != Key.Up && e.Key != Key.Down)) return;
            e.Handled = true;
            volumeKeyboardShown = true;
            ShowVolumeBar();
            SetVolume(player.Volume + (e.Key == Key.Up ? 0.05 : -0.05));
            SaveVolume();
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (syncingVolume || player == null) return;
            SetVolume(VolumeSlider.Value / 100);
        }

        private void SetVolume(double value)
        {
            value = Math.Max(0, Math.Min(1, Math.Round(value * 100) / 100));
            player.SetVolume(value);
            VolumeText.Text = Math.Round(value * 100) + "%";
            UpdateVolumeIcon();
        }

        // Save once after adjusting with the bar — not after every hover: each save rewrites schedules.json (and rotates its
        // backup) and refreshes the TODO list, and the bar now opens whenever the pointer passes over the speaker.
        private void SaveBarVolumeIfChanged()
        {
            if (player != null && player.SelectedExternal == null && volumeAtOpen >= 0 && Math.Abs(player.Volume - volumeAtOpen) > 0.001)
            {
                saveTimer?.Stop(); // this save covers a wheel step still waiting to be saved
                changed?.Invoke();
            }
            volumeAtOpen = -1;
        }

        // A StaysOpen=False popup closes on the mouse-down of the click that hits its own toggle button, and the
        // click would then reopen it. Remember when each popup closed so a second press on its button just closes it.
        private readonly System.Collections.Generic.Dictionary<System.Windows.Controls.Primitives.Popup, DateTime> popupClosedAt =
            new System.Collections.Generic.Dictionary<System.Windows.Controls.Primitives.Popup, DateTime>();

        private void TrackPopupClose(params System.Windows.Controls.Primitives.Popup[] popups)
        {
            // Watch IsOpen itself: the Closed event can arrive after the fade, too late for the click that caused it.
            var isOpen = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(
                System.Windows.Controls.Primitives.Popup.IsOpenProperty, typeof(System.Windows.Controls.Primitives.Popup));
            foreach (var popup in popups)
            {
                EventHandler closedNow = (s, e) => { if (!popup.IsOpen) popupClosedAt[popup] = DateTime.Now; };
                isOpen.AddValueChanged(popup, closedNow);
                // The descriptor is shared app-wide and holds its handlers strongly: unhooked, a closed window stays in memory.
                Closed += (s, e) => isOpen.RemoveValueChanged(popup, closedNow);
            }
        }

        /// <summary>Opens the popup, or closes it when it is open / was just closed by this same press.</summary>
        // A popup's content is laid out while it is detached (closed, or pre-built in PrewarmPopups), and WPF then keeps
        // stale sizes: a list filled in later still measured 0 px high, so the style drop-down opened as an empty box.
        // Mark the whole content for a fresh measure right before opening.
        private static void RemeasurePopup(System.Windows.Controls.Primitives.Popup popup)
        {
            if (popup?.Child != null) RemeasureTree(popup.Child);
        }

        private static void RemeasureTree(DependencyObject node)
        {
            (node as UIElement)?.InvalidateMeasure();
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) RemeasureTree(VisualTreeHelper.GetChild(node, i));
        }

        private bool TogglePopup(System.Windows.Controls.Primitives.Popup popup)
        {
            RemeasurePopup(popup); // its content may have changed since it was last shown
            if (popup.IsOpen) { popup.IsOpen = false; return false; }
            if (popupClosedAt.TryGetValue(popup, out DateTime closedAt) && (DateTime.Now - closedAt).TotalMilliseconds < 300) return false;
            popup.IsOpen = true;
            return true;
        }

        /// <summary>
        /// True when the schedule block menu is open or was just closed by this same press.
        /// A right-click while its buttons are showing should only make them go away.
        /// </summary>
        private bool CloseRightClickMenus()
        {
            if (BlockPopup.IsOpen) { BlockPopup.IsOpen = false; return true; }
            return popupClosedAt.TryGetValue(BlockPopup, out DateTime closedAt) && (DateTime.Now - closedAt).TotalMilliseconds < 300;
        }

        // ---- Playlist picker (dropdown next to ⏮) ----
        private void PlayerPlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (player == null || RecentlyDragged) return;
            BuildSourceChoices();
            TogglePopup(PlaylistPopup);
        }

        // Dropdown rows: the widget's playlists, then every other app that is playing media now.
        private void BuildSourceChoices()
        {
            if (player == null) return;
            bool external = player.SelectedExternal != null;
            var current = player.CurrentPlaylist;
            AutomaticSourceChoice.Content = new
            {
                Automatic = true, Name = "자동 감지", Sub = "새로 재생되는 앱을 자동으로 표시", IsCurrent = player.AutomaticSource, CountLabel = ""
            };
            PlaylistChoices.ItemsSource = player.Playlists.Select(p => new
            {
                List = p, AppId = (string)null, p.Name, Sub = "", IsCurrent = !player.AutomaticSource && !external && p == current, CountLabel = p.Tracks.Count + "곡"
            }).ToList();
            var apps = player.ExternalSources;
            AppChoicesSection.Visibility = apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AppChoices.ItemsSource = apps.Select(a => new
            {
                List = (MusicPlaylist)null, a.AppId, Name = a.SourceName, Sub = a.Display ?? "",
                IsCurrent = !player.AutomaticSource && external && string.Equals(a.AppId, player.SelectedExternal, StringComparison.OrdinalIgnoreCase),
                CountLabel = a.IsPlaying ? "재생 중" : "일시정지"
            }).ToList();
        }

        private void PlaylistChoice_Click(object sender, RoutedEventArgs e)
        {
            var context = (sender as FrameworkElement)?.DataContext;
            var list = context?.GetType().GetProperty("List")?.GetValue(context) as MusicPlaylist;
            var appId = context?.GetType().GetProperty("AppId")?.GetValue(context) as string;
            PlaylistPopup.IsOpen = false;
            if (player == null) return;
            if (context?.GetType().GetProperty("Automatic")?.GetValue(context) is bool automatic && automatic) player.SelectAutomaticSource();
            else if (appId != null) player.SelectExternal(appId);
            else if (list != null) player.SelectPlaylist(list);
            else return;
            UpdatePlayerBar();
            if (QueuePopup.IsOpen) RefreshQueue();
        }

        // The app picked as the source went away (its tab or player closed): the bar would stay on it with dead buttons. Once
        // it has been missing from the playing apps for a few polls (read every 1.5 s while an app is the source), the bar goes
        // back to the widget's own playlist. While the window is hidden nothing is polled: it is looked at again when shown.
        private System.Windows.Threading.DispatcherTimer lostSourceTimer;
        internal TimeSpan lostSourceDelay = TimeSpan.FromSeconds(5);

        private bool SelectedSourceMissing()
        {
            string app = player?.SelectedExternal;
            return app != null && !(player.ExternalSources ?? new SystemMediaService.NowPlaying[0])
                .Any(s => string.Equals(s.AppId, app, StringComparison.OrdinalIgnoreCase));
        }

        private void CheckExternalSource()
        {
            if (player == null || closed) return;
            if (!SelectedSourceMissing()) { lostSourceTimer?.Stop(); return; }
            if (lostSourceTimer == null)
            {
                lostSourceTimer = new System.Windows.Threading.DispatcherTimer();
                lostSourceTimer.Tick += (s, e) => { lostSourceTimer.Stop(); FallBackFromLostSource(); };
            }
            if (lostSourceTimer.IsEnabled) return; // counting already
            lostSourceTimer.Interval = lostSourceDelay;
            lostSourceTimer.Start();
        }

        private void FallBackFromLostSource()
        {
            if (player == null || closed || !IsVisible || !SelectedSourceMissing()) return;
            if (player.AutomaticSource)
            {
                player.SelectAutomaticSource();
                UpdatePlayerBar();
                return;
            }
            var list = player.CurrentPlaylist ?? player.Playlists?.FirstOrDefault();
            if (list == null) return; // no playlist of ours to go back to
            player.SelectPlaylist(list);
            UpdatePlayerBar();
            if (QueuePopup.IsOpen) RefreshQueue();
        }

        // ---- Now-playing list (song title click) ----
        // Another app plays (browser YouTube, Spotify, …): clicking the title brings its playing tab / window to the front.
        // Tests replace the activation so no real window moves.
        internal static Func<string, string, System.Threading.Tasks.Task<bool>> activateExternalOverride = null;
        private bool activatingExternal; // one activation at a time (double clicks while the tab search runs are ignored)

        private async void ActivateExternalSource()
        {
            if (activatingExternal) return;
            string appId = player.SelectedExternal;
            var source = player.ExternalSources?.FirstOrDefault(s => string.Equals(s.AppId, appId, StringComparison.OrdinalIgnoreCase));
            string title = source?.Title ?? player.NowPlaying;
            activatingExternal = true;
            try { await (activateExternalOverride ?? MediaAppActivator.ActivateAsync)(appId, title); }
            // async void: anything escaping here would take the whole app down (there is no global handler), and a failed
            // "bring forward" is never worth that — UI Automation on a closing/hung browser can throw many kinds of errors.
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { }
            finally { activatingExternal = false; }
        }

        private void PlayerTitle_Click(object sender, MouseButtonEventArgs e)
        {
            if (player == null || RecentlyDragged) return;
            e.Handled = true;
            OpenPlayerTitle();
        }

        // The title with the keyboard (Tab to it): Enter or Space does what a click does.
        private void PlayerTitle_KeyDown(object sender, KeyEventArgs e)
        {
            if (player == null || e.Key != Key.Enter && e.Key != Key.Space) return;
            e.Handled = true;
            OpenPlayerTitle();
        }

        private void OpenPlayerTitle()
        {
            if (player.SelectedExternal != null) { ActivateExternalSource(); return; } // other apps keep their own queue
            CloseDayPopup();
            RangePopup.IsOpen = false;
            RefreshQueue();
            if (TogglePopup(QueuePopup)) ScrollQueueToCurrent();
        }

        // ---- Seek bar: a thin track inside the fixed strip's bottom edge ----
        private System.Windows.Threading.DispatcherTimer seekTimer;
        private bool seekDragging, updatingSeek;

        // The position only moves while music plays and can be seen: the timer runs only then (a pause, the tray or a hidden
        // bar stop it; playing again or showing the window starts it).
        private bool SeekTicks => player != null && !closed && IsVisible && PlayerBar.Visibility == Visibility.Visible &&
            SeekRow.Visibility == Visibility.Visible && !string.IsNullOrEmpty(player.NowPlaying) && player.IsPlaying;

        private void StartSeekTimer()
        {
            if (seekTimer == null)
            {
                seekTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                seekTimer.Tick += (s, e) =>
                {
                    if (!SeekTicks) seekTimer.Stop();
                    UpdateSeek();
                };
            }
            seekTimer.Start();
        }

        internal bool SeekTimerRunning => seekTimer?.IsEnabled == true;

        // Empty, playing and paused states all keep the same layout and a reserved progress track.
        private void UpdatePlayerDetails()
        {
            if (player == null || SeekRow == null) return;
            bool songLoaded = !string.IsNullOrEmpty(player.NowPlaying);
            PlayerTitleHost.Visibility = songLoaded ? Visibility.Visible : Visibility.Hidden;
            PlayerIdleText.Visibility = songLoaded ? Visibility.Collapsed : Visibility.Visible;
            UpdateSeek();
            if (SeekTicks) { if (seekTimer == null || !seekTimer.IsEnabled) StartSeekTimer(); }
            else seekTimer?.Stop();
        }

        private static string Clock(TimeSpan t) =>
            t.TotalHours >= 1 ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                              : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);

        // Shows position / length; the bar stays still while the user is dragging it.
        public void UpdateSeek()
        {
            if (player == null || SeekSlider == null) return;
            TimeSpan? duration = player.Duration, position = player.Position;
            bool known = !string.IsNullOrEmpty(player.NowPlaying) && duration.HasValue && duration.Value > TimeSpan.Zero;
            SeekSlider.IsEnabled = known;
            SeekTotal.Text = known ? Clock(duration.Value) : "--:--";
            if (seekDragging) return;
            double seconds = Math.Max(0, Math.Min(known ? duration.Value.TotalSeconds : 0, position?.TotalSeconds ?? 0));
            updatingSeek = true;
            try
            {
                SeekSlider.Maximum = known ? duration.Value.TotalSeconds : 1;
                SeekSlider.Value = seconds;
            }
            finally { updatingSeek = false; }
            SeekCurrent.Text = Clock(TimeSpan.FromSeconds(seconds));
            SeekSlider.ToolTip = SeekCurrent.Text + " / " + SeekTotal.Text;
        }

        private void SeekSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) => seekDragging = true;

        private void SeekSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            seekDragging = false;
            player?.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
            UpdatePlayerDetails();
        }

        // A click on the track (IsMoveToPointEnabled) or the mouse wheel moves the value without a drag: seek right away.
        private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (updatingSeek || SeekCurrent == null) return;
            SeekCurrent.Text = Clock(TimeSpan.FromSeconds(SeekSlider.Value));
            if (!seekDragging) player?.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
        }

        // One row of the now-playing list, updated in place.
        public sealed class QueueRow : System.ComponentModel.INotifyPropertyChanged
        {
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            private MusicTrack track; private string title, marker; private bool isCurrent, canUp, canDown;
            private void Set<T>(ref T field, T value, string name)
            {
                if (Equals(field, value)) return;
                field = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            }
            public MusicTrack Track { get => track; set => Set(ref track, value, nameof(Track)); }
            public string Title { get => title; set => Set(ref title, value, nameof(Title)); }
            public bool IsCurrent { get => isCurrent; set => Set(ref isCurrent, value, nameof(IsCurrent)); }
            public string Marker { get => marker; set => Set(ref marker, value, nameof(Marker)); }
            public bool CanUp { get => canUp; set => Set(ref canUp, value, nameof(CanUp)); }
            public bool CanDown { get => canDown; set => Set(ref canDown, value, nameof(CanDown)); }
        }

        // The list is virtualized and its rows are kept: a refresh (every playback change, each ▲▼) updates them in place, so
        // a long playlist opens fast and the list stays where the user scrolled it. Another length (a different playlist,
        // songs added or removed) builds it anew.
        private System.Collections.ObjectModel.ObservableCollection<QueueRow> queueRows = new System.Collections.ObjectModel.ObservableCollection<QueueRow>();

        public void RefreshQueue()
        {
            if (player == null || QueueList == null) return;
            var tracks = player.Queue;
            var current = player.Current;
            QueueTitle.Text = "지금 재생 목록 · " + tracks.Count + "곡";
            if (queueRows.Count != tracks.Count)
            {
                queueRows = new System.Collections.ObjectModel.ObservableCollection<QueueRow>(tracks.Select(t => new QueueRow()));
                QueueList.ItemsSource = queueRows;
            }
            else if (QueueList.ItemsSource != queueRows) QueueList.ItemsSource = queueRows;
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                var row = queueRows[i];
                row.Track = t;
                row.Title = t.Title ?? t.Source;
                row.IsCurrent = t == current;
                row.Marker = t == current ? (player.IsPlaying ? "▶" : "❚❚") : (i + 1).ToString(CultureInfo.InvariantCulture);
                row.CanUp = i > 0;
                row.CanDown = i < tracks.Count - 1;
            }
        }

        // Opened: the song playing now is in view, a couple of rows from the top.
        private void ScrollQueueToCurrent()
        {
            int index = -1;
            for (int i = 0; i < queueRows.Count && index < 0; i++) if (queueRows[i].IsCurrent) index = i;
            if (index < 0) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (QueuePopup.IsOpen) QueueScroll?.ScrollToVerticalOffset(Math.Max(0, index - 2)); // offsets count rows here
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        internal ScrollViewer QueueScroll => QueueList?.Template?.FindName("QueueScroll", QueueList) as ScrollViewer;

        private static MusicTrack QueueTrack(object sender) =>
            (sender as FrameworkElement)?.DataContext?.GetType().GetProperty("Track")?.GetValue(((FrameworkElement)sender).DataContext) as MusicTrack;

        private void QueuePlay_Click(object sender, RoutedEventArgs e)
        {
            var track = QueueTrack(sender);
            if (track != null) player?.Play(track);
        }

        private void QueueMove_Click(object sender, RoutedEventArgs e)
        {
            var track = QueueTrack(sender);
            if (track == null || player == null) return;
            int index = player.Queue.ToList().IndexOf(track);
            if (index < 0) return;
            player.Move(track, index + int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture));
            RefreshQueue();
        }

        // 순차 → 랜덤 → 1곡 → 순차
        private void PlayerMode_Click(object sender, RoutedEventArgs e)
        {
            if (player == null || RecentlyDragged || player.SelectedExternal != null) return; // another app keeps its own order
            PlayMode mode = player.Mode;
            player.SetMode(mode == PlayMode.Sequential ? PlayMode.Shuffle : mode == PlayMode.Shuffle ? PlayMode.RepeatOne : PlayMode.Sequential);
            UpdatePlayerBar();
        }

    }
}
