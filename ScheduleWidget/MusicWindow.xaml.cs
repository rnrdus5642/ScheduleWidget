using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ScheduleWidget
{
    public partial class MusicWindow : Window
    {
        private readonly MusicSettings settings;
        private readonly Func<bool> save;
        private readonly Action exitApplication;
        private readonly MediaPlayer media = new MediaPlayer();
        private readonly Random shuffleRandom = new Random();
        private readonly System.Collections.Generic.List<MusicTrack> shuffleOrder = new System.Collections.Generic.List<MusicTrack>();
        private MusicPlaylist playlist;
        private MusicTrack playing;
        private bool loading = true, paused, closed, mediaOpening;
        private int playRequest;
        private int shuffleIndex = -1;
        private MusicPlaylist shufflePlaylist;
        private Task playerInitialization;
        private TaskCompletionSource<bool> shellReady;

        public MusicWindow(MusicSettings settings, Func<bool> save, Action exitApplication)
        {
            this.settings = settings;
            this.save = save;
            this.exitApplication = exitApplication;
            InitializeComponent();
            // The player view is disposed on this thread when the window closes (or the app's dispatcher shuts down): the
            // finalizer must never do it — HwndHost's finalizer thread cleanup crashed a run (InvalidCastException).
            GC.SuppressFinalize(YouTubePlayer);
            ChromelessWindow.Apply(this); // no title bar or taskbar button; X in the top-right corner
            Icon = TrayService.CreateWindowIcon();
            if (settings.Playlists.Count == 0) settings.Playlists.Add(new MusicPlaylist());
            VolumeSlider.Value = settings.Volume;
            media.Volume = settings.Volume;
            RepeatToggle.IsChecked = settings.Repeat;
            ShuffleToggle.IsChecked = settings.Shuffle;
            RepeatOneToggle.IsChecked = settings.RepeatOne;
            RefreshPlaylists(settings.Playlists.FirstOrDefault(p => p.Id == settings.SelectedPlaylistId) ?? settings.Playlists[0]);
            media.MediaOpened += (s, e) =>
            {
                if (playing == null || playing.IsYouTube || !mediaOpening) return;
                mediaOpening = false;
                if (!paused) media.Play();
                MusicStatus.Text = "재생 중 · " + playing.Title + SkippedNote();
                autoSkips = 0;
            };
            media.MediaEnded += async (s, e) =>
            {
                try { if (playing != null && !playing.IsYouTube) await AdvanceAsync(1, true); }
                catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { } // an event handler must not end the app
            };
            media.MediaFailed += (s, e) =>
            {
                if (playing == null || playing.IsYouTube) return;
                if (SkipBroken(playing)) return;
                StopPlayback();
                MusicStatus.Text = "파일을 재생할 수 없습니다. 파일 위치와 Windows 코덱을 확인한 뒤 다음 곡을 선택하세요.";
            };
            StateChanged += (s, e) =>
            {
                // A minimized WebView stops YouTube, so keep playing from the hidden background position instead.
                if (WindowState == WindowState.Minimized && playing?.IsYouTube == true) MoveToBackground();
            };
            SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
            Dispatcher.ShutdownStarted += DispatcherShuttingDown;
            Closed += (s, e) =>
            {
                closed = true;
                StopPlayback();
                media.Close();
                volumeSaveTimer?.Stop();
                SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged; // static event: unhook or the window leaks
                Dispatcher.ShutdownStarted -= DispatcherShuttingDown;
                DisposePlayer();
                save();
                Dispatcher.BeginInvoke(new Action(NotifyPlayback)); // after the owner has dropped its reference
            };
            loading = false;
        }

        private void DispatcherShuttingDown(object sender, EventArgs e) => DisposePlayer();

        // Monitors added, removed or rearranged: the background spot left of every monitor moves too, and a window left at
        // the old spot could end up on the new screen.
        private void DisplaySettingsChanged(object sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (inBackground && !closed) ParkOffscreen();
        }), DispatcherPriority.Background);

        private void RefreshPlaylists(MusicPlaylist selected)
        {
            PlaylistCombo.ItemsSource = null;
            PlaylistCombo.ItemsSource = settings.Playlists;
            PlaylistCombo.SelectedItem = selected;
        }
        private void PlaylistChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(PlaylistCombo.SelectedItem is MusicPlaylist selected)) return;
            if (playlist != selected) { StopPlayback(); ResetShuffle(); }
            playlist = selected;
            settings.SelectedPlaylistId = selected.Id;
            NotifyPlayback();
            PlaylistName.Text = selected.Name;
            RefreshTracks();
            if (!loading) save();
        }
        private void RefreshTracks(MusicTrack selected = null)
        {
            TrackList.ItemsSource = null;
            TrackList.ItemsSource = playlist?.Tracks;
            TrackList.SelectedItem = selected;
            if (selected == null && playlist?.Tracks.Count > 0) TrackList.SelectedIndex = 0;
            ShowSelectedTrack();
            if (settings.Shuffle) ResetShuffle(playing != null && playlist?.Tracks.Contains(playing) == true ? playing : null);
        }

        // The song list scrolls inside its own box (long lists are virtualized): keep the selected song in view. Only the list
        // scrolls — ScrollIntoView would also move the page around it away from what is being looked at (the video).
        private void ShowSelectedTrack() => Dispatcher.BeginInvoke(new Action(() =>
        {
            int index = TrackList.SelectedIndex;
            var list = FindDescendant<ScrollViewer>(TrackList);
            if (index < 0 || list == null || !list.CanContentScroll || list.ViewportHeight <= 0) return; // offsets count songs
            if (index < list.VerticalOffset) list.ScrollToVerticalOffset(index);
            else if (index + 1 > list.VerticalOffset + list.ViewportHeight) list.ScrollToVerticalOffset(index + 1 - Math.Floor(list.ViewportHeight));
        }), DispatcherPriority.Loaded);

        private void ResetShuffle(MusicTrack first = null)
        {
            shuffleOrder.Clear();
            shufflePlaylist = playlist;
            shuffleIndex = -1;
            if (!settings.Shuffle || playlist == null) return;

            shuffleOrder.AddRange(playlist.Tracks);
            for (int i = shuffleOrder.Count - 1; i > 0; i--)
            {
                int swap = shuffleRandom.Next(i + 1);
                MusicTrack track = shuffleOrder[i];
                shuffleOrder[i] = shuffleOrder[swap];
                shuffleOrder[swap] = track;
            }
            if (first != null)
            {
                int index = shuffleOrder.IndexOf(first);
                if (index >= 0)
                {
                    shuffleOrder[index] = shuffleOrder[0];
                    shuffleOrder[0] = first;
                    shuffleIndex = 0;
                }
            }
        }

        private bool IsShuffleOrderValid()
        {
            return settings.Shuffle && shufflePlaylist == playlist &&
                shuffleOrder.Count == playlist?.Tracks.Count &&
                playlist.Tracks.All(track => shuffleOrder.Contains(track));
        }

        private async Task PlaySelectedTrackAsync(MusicTrack track)
        {
            if (track == null || playlist?.Tracks.Contains(track) != true) return;
            if (settings.Shuffle) ResetShuffle(track);
            await PlayTrackAsync(track);
        }
        private void CreatePlaylist_Click(object sender, RoutedEventArgs e)
        {
            string name = PlaylistName.Text.Trim();
            if (name.Length == 0) { MusicStatus.Text = "새 플레이리스트 이름을 입력하세요."; return; }
            var list = new MusicPlaylist { Name = name };
            settings.Playlists.Add(list); RefreshPlaylists(list); save();
        }
        private void RenamePlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (playlist == null || string.IsNullOrWhiteSpace(PlaylistName.Text)) return;
            playlist.Name = PlaylistName.Text.Trim(); RefreshPlaylists(playlist); save();
        }
        private void DeletePlaylist_Click(object sender, RoutedEventArgs e)
        {
            if (playlist == null || MessageBox.Show(this, "‘" + playlist.Name + "’ 목록을 삭제할까요? 원본 음악 파일은 유지됩니다.",
                "플레이리스트 삭제", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            StopPlayback(); settings.Playlists.Remove(playlist);
            if (settings.Playlists.Count == 0) settings.Playlists.Add(new MusicPlaylist());
            RefreshPlaylists(settings.Playlists[0]); save();
        }
        private static readonly string[] MusicExtensions = { ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac", ".ogg", ".aiff", ".aif" };
        private const string TrackDragFormat = "ScheduleWidget.MusicTrack";
        private Point? trackDragStart;

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Multiselect = true, Title = "음악 파일 선택",
                Filter = "음악 파일|*" + string.Join(";*", MusicExtensions) + "|모든 파일|*.*" };
            if (dialog.ShowDialog(this) != true) return;
            InsertFiles(dialog.FileNames, playlist?.Tracks.Count ?? 0);
        }

        private void InsertFiles(System.Collections.Generic.IEnumerable<string> paths, int index)
        {
            if (playlist == null) return;
            var tracks = paths.Select(path => new MusicTrack { Source = path, Title = Path.GetFileNameWithoutExtension(path) }).ToList();
            if (tracks.Count == 0) { MusicStatus.Text = "추가할 수 있는 음악 파일이 없습니다."; return; }
            playlist.Tracks.InsertRange(Math.Max(0, Math.Min(index, playlist.Tracks.Count)), tracks);
            RefreshTracks(tracks[0]); save();
            MusicStatus.Text = tracks.Count + "곡을 추가했습니다.";
        }

        // Dropped files plus music files inside dropped folders; files dropped explicitly are kept even with unknown extensions.
        private static System.Collections.Generic.List<string> DroppedMusicFiles(IDataObject data)
        {
            var result = new System.Collections.Generic.List<string>();
            if (!(data.GetData(DataFormats.FileDrop) is string[] items)) return result;
            foreach (string item in items)
            {
                if (File.Exists(item)) result.Add(item);
                else if (Directory.Exists(item))
                {
                    try
                    {
                        result.AddRange(Directory.EnumerateFiles(item, "*", SearchOption.AllDirectories)
                            .Where(f => MusicExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase));
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            return result;
        }

        private void MoveTrackTo(MusicTrack track, int index)
        {
            if (playlist == null || track == null) return;
            int current = playlist.Tracks.IndexOf(track);
            if (current < 0) return;
            index = Math.Max(0, Math.Min(index, playlist.Tracks.Count - 1));
            if (index == current) return;
            playlist.Tracks.RemoveAt(current);
            playlist.Tracks.Insert(index, track);
            RefreshTracks(track); save();
        }

        private void RemoveTrack(MusicTrack track)
        {
            if (playlist == null || track == null) return;
            if (track == playing) StopPlayback();
            playlist.Tracks.Remove(track); RefreshTracks(); save();
        }

        private void RowMove_Click(object sender, RoutedEventArgs e)
        {
            var track = (sender as FrameworkElement)?.DataContext as MusicTrack;
            if (track == null) return;
            MoveTrackTo(track, playlist.Tracks.IndexOf(track) + int.Parse((string)((Button)sender).Tag));
            e.Handled = true;
        }

        private void RowRemove_Click(object sender, RoutedEventArgs e)
        {
            RemoveTrack((sender as FrameworkElement)?.DataContext as MusicTrack);
            e.Handled = true;
        }

        private void TrackList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // Row buttons and the scrollbar keep their own click behavior.
            var source = e.OriginalSource as DependencyObject;
            trackDragStart = FindAncestor<ButtonBase>(source) == null && FindAncestor<ScrollBar>(source) == null
                && FindAncestor<ListBoxItem>(source) != null ? e.GetPosition(TrackList) : (Point?)null;
        }

        private void TrackList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!trackDragStart.HasValue || e.LeftButton != MouseButtonState.Pressed) { trackDragStart = null; return; }
            Vector moved = e.GetPosition(TrackList) - trackDragStart.Value;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            trackDragStart = null;
            if (!(item?.DataContext is MusicTrack track)) return;
            DragDrop.DoDragDrop(item, new DataObject(TrackDragFormat, track), DragDropEffects.Move);
            DropLine.Visibility = Visibility.Collapsed;
        }

        // Insert position for the pointer: before the hovered row's upper half, after its lower half.
        private int DropIndex(DragEventArgs e)
        {
            if (playlist == null) return 0;
            var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (item == null || !(item.DataContext is MusicTrack track)) return playlist.Tracks.Count;
            int index = playlist.Tracks.IndexOf(track);
            if (index < 0) return playlist.Tracks.Count;
            return e.GetPosition(item).Y > item.ActualHeight / 2 ? index + 1 : index;
        }

        private void ShowDropLine(int index)
        {
            double y;
            if (playlist == null || playlist.Tracks.Count == 0) y = 6;
            else
            {
                bool after = index >= playlist.Tracks.Count;
                var item = TrackList.ItemContainerGenerator.ContainerFromIndex(after ? playlist.Tracks.Count - 1 : index) as ListBoxItem;
                if (item == null || !item.IsVisible) { DropLine.Visibility = Visibility.Collapsed; return; }
                y = item.TranslatePoint(new Point(0, after ? item.ActualHeight : 0), TrackList).Y;
            }
            DropLine.Margin = new Thickness(8, Math.Max(0, Math.Min(TrackList.ActualHeight - 3, y - 1.5)), 8, 0);
            DropLine.Visibility = Visibility.Visible;
        }

        private void TrackList_DragOver(object sender, DragEventArgs e)
        {
            bool track = e.Data.GetDataPresent(TrackDragFormat), files = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = track ? DragDropEffects.Move : files ? DragDropEffects.Copy : DragDropEffects.None;
            if (track || files) ShowDropLine(DropIndex(e)); else DropLine.Visibility = Visibility.Collapsed;
            AutoScrollWhileDragging(e);
            e.Handled = true;
        }

        private void AutoScrollWhileDragging(DragEventArgs e)
        {
            var viewer = FindDescendant<ScrollViewer>(TrackList);
            if (viewer == null) return;
            double y = e.GetPosition(TrackList).Y;
            if (y < 24) viewer.LineUp();
            else if (y > TrackList.ActualHeight - 24) viewer.LineDown();
        }

        // At the top or bottom of the song list the wheel scrolls the page instead (the list sits inside the window's scroll).
        private void TrackList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var inner = FindDescendant<ScrollViewer>(TrackList);
            if (inner == null || (e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight)) return;
            var page = FindAncestor<ScrollViewer>(VisualTreeHelper.GetParent(TrackList));
            if (page == null) return;
            e.Handled = true;
            page.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = TrackList });
        }

        private void TrackList_DragLeave(object sender, DragEventArgs e) => DropLine.Visibility = Visibility.Collapsed;

        private void TrackList_Drop(object sender, DragEventArgs e)
        {
            DropLine.Visibility = Visibility.Collapsed;
            int index = DropIndex(e);
            if (e.Data.GetData(TrackDragFormat) is MusicTrack track && playlist?.Tracks.Contains(track) == true)
            {
                int current = playlist.Tracks.IndexOf(track);
                MoveTrackTo(track, index > current ? index - 1 : index);
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop)) InsertFiles(DroppedMusicFiles(e.Data), index);
            e.Handled = true;
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) InsertFiles(DroppedMusicFiles(e.Data), playlist?.Tracks.Count ?? 0);
            e.Handled = true;
        }

        private static T FindAncestor<T>(DependencyObject node) where T : DependencyObject
        {
            while (node != null && !(node is T))
                node = node is Visual || node is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            return node as T;
        }

        private static T FindDescendant<T>(DependencyObject node) where T : DependencyObject
        {
            for (int i = 0; node != null && i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is T match) return match;
                var nested = FindDescendant<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }
        private async void AddLinks_Click(object sender, RoutedEventArgs e)
        {
            string[] links = YouTubeLinks.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            if (links.Length == 0) return;
            foreach (string link in links)
                if (!FeatureRules.TryYouTube(link, out _, out _))
                { MusicStatus.Text = "올바른 YouTube 링크인지 확인해 주세요: " + link; return; }

            MusicPlaylist target = playlist;
            bool expand = ExpandPlaylists.IsChecked == true;
            var tracks = new System.Collections.Generic.List<MusicTrack>();
            var failures = new System.Collections.Generic.List<string>();
            AddLinksButton.IsEnabled = false;
            try
            {
                foreach (string link in links)
                {
                    FeatureRules.TryYouTube(link, out string video, out string list);
                    if (expand && FeatureRules.TryYouTubePlaylistId(link, out string listId))
                    {
                        MusicStatus.Text = "재생목록 곡을 불러오는 중… (" + listId + ")";
                        try
                        {
                            var videos = await YouTubePlaylistService.FetchAsync(listId);
                            if (closed) return;
                            tracks.AddRange(videos.Select(v => new MusicTrack
                                { Source = "https://www.youtube.com/watch?v=" + v.Id, Title = v.Title }));
                            continue;
                        }
                        catch (InvalidOperationException ex)
                        {
                            // Keep the link usable: fall back to the original single entry below.
                            failures.Add(ex.Message);
                        }
                    }
                    string videoTitle = video != null ? await YouTubePlaylistService.FetchVideoTitleAsync(video) : null;
                    if (closed) return;
                    tracks.Add(new MusicTrack { Source = link, Title = videoTitle ?? (video != null ? "YouTube · " + video : "YouTube 재생목록 · " + list) });
                }
            }
            finally { AddLinksButton.IsEnabled = true; }

            if (target == null || !settings.Playlists.Contains(target)) { MusicStatus.Text = "추가할 플레이리스트를 다시 선택해 주세요."; return; }
            target.Tracks.AddRange(tracks);
            YouTubeLinks.Clear();
            if (target == playlist) RefreshTracks(TrackList.SelectedItem as MusicTrack);
            save();
            MusicStatus.Text = tracks.Count + "곡을 추가했습니다." +
                (failures.Count > 0 ? " 일부 재생목록은 곡별로 가져오지 못해 한 항목으로 추가했습니다: " + failures[0] : "");
        }
        private void RemoveTrack_Click(object sender, RoutedEventArgs e)
        {
            if (TrackList.SelectedItem is MusicTrack track) RemoveTrack(track);
        }
        private void MoveTrack_Click(object sender, RoutedEventArgs e)
        {
            if (TrackList.SelectedItem is MusicTrack track)
                MoveTrackTo(track, playlist.Tracks.IndexOf(track) + int.Parse((string)((Button)sender).Tag));
        }
        private async void Play_Click(object sender, RoutedEventArgs e)
        {
            if (TrackList.SelectedItem is MusicTrack track && track != playing) { await PlaySelectedTrackAsync(track); return; }
            await TogglePlaybackAsync();
        }

        private async Task TogglePlaybackAsync()
        {
            if (playing == null)
            {
                MusicTrack track = TrackList.SelectedItem as MusicTrack ?? playlist?.Tracks.FirstOrDefault();
                if (track != null) await PlaySelectedTrackAsync(track);
                return;
            }
            paused = !paused;
            if (playing.IsYouTube) Post(new { action = paused ? "pause" : "resume" });
            else if (paused) media.Pause(); else if (!mediaOpening) media.Play();
            PlayButton.Content = paused ? "재생" : "일시정지";
            NotifyPlayback();
        }
        private async void Track_DoubleClick(object sender, MouseButtonEventArgs e)
        { if (TrackList.SelectedItem is MusicTrack track) await PlaySelectedTrackAsync(track); }
        private async void Previous_Click(object sender, RoutedEventArgs e) => await AdvanceAsync(-1, false);
        private async void Next_Click(object sender, RoutedEventArgs e) => await AdvanceAsync(1, false);
        private void Stop_Click(object sender, RoutedEventArgs e) { StopPlayback(); ResetShuffle(); }
        private async Task AdvanceAsync(int direction, bool automatic)
        {
            bool skipping = skippingBroken; // moving past a song that could not play: never "repeat" that song
            skippingBroken = false;
            if (playlist == null || playlist.Tracks.Count == 0) return;
            if (playing != null && !playlist.Tracks.Contains(playing)) StopPlayback();
            if (automatic && settings.RepeatOne && playing != null && !skipping)
            {
                MusicTrack again = playing; // 한 곡 반복: 끝난 곡을 처음부터 다시
                pendingAutomatic = true;
                await PlayTrackAsync(again);
                return;
            }

            if (settings.Shuffle)
            {
                if (!IsShuffleOrderValid()) ResetShuffle(playing ?? TrackList.SelectedItem as MusicTrack);
                if (shuffleOrder.Count == 0) return;
                if (shuffleIndex < 0)
                {
                    MusicTrack current = playing ?? TrackList.SelectedItem as MusicTrack;
                    shuffleIndex = current == null ? -1 : shuffleOrder.IndexOf(current);
                    if (shuffleIndex < 0) shuffleIndex = direction < 0 ? 0 : -1;
                }
                int shuffledNext = shuffleIndex + direction;
                if (shuffledNext >= shuffleOrder.Count || shuffledNext < 0)
                {
                    if (automatic && !settings.Repeat) { StopPlayback(); MusicStatus.Text = "플레이리스트 재생을 마쳤습니다."; return; }
                    ResetShuffle();
                    shuffledNext = direction < 0 ? shuffleOrder.Count - 1 : 0;
                }
                shuffleIndex = shuffledNext;
                pendingAutomatic = automatic;
                await PlayTrackAsync(shuffleOrder[shuffleIndex]);
                return;
            }
            int index = playing == null ? TrackList.SelectedIndex : playlist.Tracks.IndexOf(playing);
            int next = index + direction;
            if (next >= playlist.Tracks.Count || next < 0)
            {
                if (automatic && !settings.Repeat) { StopPlayback(); MusicStatus.Text = "플레이리스트 재생을 마쳤습니다."; return; }
                next = next < 0 ? playlist.Tracks.Count - 1 : 0;
            }
            pendingAutomatic = automatic;
            await PlayTrackAsync(playlist.Tracks[next]);
        }
        private async Task PlayTrackAsync(MusicTrack track)
        {
            bool automatic = pendingAutomatic; // set by AdvanceAsync right before: this song follows one that ended
            pendingAutomatic = false;
            TimeSpan start = resumeStart; int startIndex = resumeIndex; bool startPaused = resumePaused; // after a player crash
            resumeStart = TimeSpan.Zero; resumeIndex = 0; resumePaused = false;
            if (track == null || playlist?.Tracks.Contains(track) != true) return;
            if (settings.Shuffle && (playing == null || !IsShuffleOrderValid() || shuffleIndex < 0)) ResetShuffle(track);
            StopPlayback();
            int request = playRequest;
            autoAdvancing = automatic;
            if (!automatic) autoSkips = 0; // a song picked by hand starts a new count
            playing = track; paused = startPaused; liveTitle = null; ytPosition = start; ytDuration = TimeSpan.Zero; ytIndex = startIndex;
            TrackList.SelectedItem = track;
            ShowSelectedTrack();
            NowPlaying.Text = track.Title; PlayButton.Content = paused ? "재생" : "일시정지";
            NotifyPlayback();
            try
            {
                if (track.IsYouTube)
                {
                    // YouTube needs a live (shown) WebView. Without opening the settings window, host it off-screen.
                    if (!closed && (!IsVisible || WindowState == WindowState.Minimized)) MoveToBackground();
                    YouTubePlayer.Visibility = Visibility.Visible;
                    YouTubePlayer.BringIntoView();
                    MusicStatus.Text = "YouTube 연결 중… 자동 재생이 차단되면 영상의 재생 버튼을 누르세요.";
                    await EnsurePlayerAsync();
                    if (closed || request != playRequest) return;
                    FeatureRules.TryYouTube(track.Source, out string video, out string list);
                    Post(new { action = "load", video, list, request, volume = Math.Round(settings.Volume * 100), paused,
                        start = Math.Round(start.TotalSeconds, 1), index = startIndex });
                }
                else
                {
                    if (!Path.IsPathRooted(track.Source) || !File.Exists(track.Source))
                        throw new InvalidOperationException("음악 파일을 찾을 수 없습니다. 이동한 파일은 다시 추가해 주세요.");
                    mediaOpening = true;
                    media.Open(new Uri(Path.GetFullPath(track.Source)));
                    media.Volume = settings.Volume;
                    MusicStatus.Text = "파일을 여는 중…";
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException ||
                ex is ArgumentException || ex is System.Runtime.InteropServices.COMException || ex is WebView2RuntimeNotFoundException)
            {
                if (closed || request != playRequest) return;
                if (SkipBroken(track)) return; // automatic advance: on to the next song
                StopPlayback();
                MusicStatus.Text = "재생 실패: " + ex.Message + " YouTube에는 Edge WebView2 Runtime과 인터넷 연결이 필요합니다.";
            }
        }

        // ---- A song that cannot play during automatic advance ----
        // A song that ended moves on by itself; if the next one cannot play — a moved or broken file, a blocked, removed or
        // unreachable video — it is skipped once, on to the one after. At most one pass over the list: where nothing can
        // play, it stops and says so. A song picked by hand that cannot play still stops with the reason, as before.
        private bool pendingAutomatic, autoAdvancing, skippingBroken;
        private int autoSkips;

        private string SkippedNote() => autoSkips > 0 ? " (재생할 수 없는 곡 " + autoSkips + "개를 건너뛰었습니다)" : "";

        private bool SkipBroken(MusicTrack broken)
        {
            if (!autoAdvancing || closed || playlist == null || broken == null) return false;
            autoAdvancing = false;
            StopPlayback(); // the song stays selected: the next one counts from it
            if (++autoSkips >= Math.Max(1, playlist.Tracks.Count))
            {
                autoSkips = 0;
                MusicStatus.Text = "재생할 수 있는 곡이 없어 멈췄습니다. 파일 위치와 인터넷 연결을 확인해 주세요.";
                return true;
            }
            MusicStatus.Text = "‘" + broken.Title + "’을(를) 재생할 수 없어 다음 곡으로 넘어갑니다.";
            int token = playRequest;
            // A fresh step from the dispatcher, not a nested call: a long list of missing files never builds a deep chain.
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (closed || token != playRequest) return; // stopped, or another song was picked meanwhile
                skippingBroken = true;
                try { await AdvanceAsync(1, true); }
                catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
                finally { skippingBroken = false; }
            }), DispatcherPriority.Background);
            return true;
        }

        private void StopPlayback()
        {
            playRequest++; playing = null; paused = false; mediaOpening = false; autoAdvancing = false;
            media.Close(); Post(new { action = "stop" });
            if (YouTubePlayer != null) YouTubePlayer.Visibility = Visibility.Collapsed;
            if (PlayButton != null) PlayButton.Content = "재생";
            if (NowPlaying != null) NowPlaying.Text = "재생할 곡을 선택하세요.";
            NotifyPlayback();
        }
        private Task EnsurePlayerAsync()
        {
            if (playerInitialization == null || playerInitialization.IsFaulted) playerInitialization = InitializePlayerAsync();
            return playerInitialization;
        }
        private async Task InitializePlayerAsync()
        {
            var view = YouTubePlayer; // a crashed browser swaps the view: work on the one this started with
            if (playerDisposed) throw new InvalidOperationException("플레이어가 닫혔습니다.");
            await EmbeddedBrowser.InitializeMusicAsync(view);
            if (closed || view != YouTubePlayer) return;
            var ready = new TaskCompletionSource<bool>();
            shellReady = ready;
            var core = view.CoreWebView2;
            core.WebMessageReceived -= PlayerMessage;
            core.WebMessageReceived += PlayerMessage;
            core.NewWindowRequested -= PlayerNewWindow; // once per view: a reloaded page must not add another
            core.NewWindowRequested += PlayerNewWindow;
            core.ProcessFailed -= PlayerProcessFailed;
            core.ProcessFailed += PlayerProcessFailed;
            core.Navigate(EmbeddedBrowser.Origin + "player.html");
            if (await Task.WhenAny(ready.Task, Task.Delay(15000)) != ready.Task)
                throw new InvalidOperationException("플레이어 초기화 시간이 초과되었습니다. 다시 재생해 주세요.");
        }

        private void PlayerNewWindow(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            MusicStatus.Text = "외부 링크는 ‘YouTube에서 열기’를 사용하세요.";
        }

        // ---- Player crash recovery ----
        // The player's browser can die (crash, runtime update, ended from Task Manager). A dead page only needs loading
        // again; a dead browser process needs a new view (and environment). A YouTube song that was playing goes on where
        // it was (paused if it was paused); a player that keeps dying is not restarted in a loop (at most 3 times a minute).
        private readonly System.Collections.Generic.List<DateTime> playerRecoveries = new System.Collections.Generic.List<DateTime>();
        private TimeSpan resumeStart;
        private int resumeIndex, ytIndex;
        private bool resumePaused, playerDisposed, recoveryQueued;
        internal Action<bool, MusicTrack, TimeSpan, bool> playerRecoveryOverride = null; // checks: (new view?, song to resume, position, paused)

        private void PlayerProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            var kind = e.ProcessFailedKind;
            // After the view's own event, not inside it: replacing or disposing the view from its event can trip the control.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { HandlePlayerProcessFailed(kind); }
                catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
            }));
        }

        internal void HandlePlayerProcessFailed(CoreWebView2ProcessFailedKind kind)
        {
            if (closed || playerDisposed) return;
            bool recreate = kind == CoreWebView2ProcessFailedKind.BrowserProcessExited;
            if (!recreate && kind != CoreWebView2ProcessFailedKind.RenderProcessExited &&
                kind != CoreWebView2ProcessFailedKind.FrameRenderProcessExited && kind != CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                return; // GPU and helper processes restart by themselves
            // The old page (or browser) is gone either way: the next YouTube song loads a fresh one.
            playerInitialization = null;
            shellReady = null;
            MusicTrack resume = null;
            TimeSpan at = ytPosition; bool wasPaused = paused; int index = ytIndex;
            if (!recoveryQueued && playing?.IsYouTube == true) // one crash can report several processes: resume once
            {
                DateTime now = DateTime.UtcNow;
                playerRecoveries.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
                playerRecoveries.Add(now);
                if (playerRecoveries.Count <= 3) resume = playing;
                else
                {
                    StopPlayback();
                    MusicStatus.Text = "YouTube 플레이어가 계속 멈춥니다. 잠시 뒤 다시 재생해 주세요.";
                }
            }
            if (playerRecoveryOverride != null) { playerRecoveryOverride(recreate, resume, at, wasPaused); return; }
            if (recreate)
            {
                EmbeddedBrowser.ResetMusicEnvironment();
                RecreatePlayerView();
            }
            if (resume == null) return;
            MusicStatus.Text = "YouTube 플레이어를 다시 여는 중…";
            recoveryQueued = true;
            int token = playRequest;
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                recoveryQueued = false;
                if (closed || token != playRequest || playing != resume) return; // stopped or changed meanwhile
                resumeStart = at; resumeIndex = index; resumePaused = wasPaused;
                try { await PlayTrackAsync(resume); }
                catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
            }), DispatcherPriority.Background);
        }

        // A browser that crashed leaves its view unusable (even reading CoreWebView2 throws): put a new one in its place.
        private void RecreatePlayerView()
        {
            var old = YouTubePlayer;
            if (!(old?.Parent is Panel parent)) return;
            int index = parent.Children.IndexOf(old);
            var fresh = new Microsoft.Web.WebView2.Wpf.WebView2
            {
                Visibility = old.Visibility, MinWidth = old.MinWidth, MinHeight = old.MinHeight,
                HorizontalAlignment = old.HorizontalAlignment, VerticalAlignment = old.VerticalAlignment
            };
            GC.SuppressFinalize(fresh); // disposed on this thread, like the first one
            parent.Children.RemoveAt(index);
            parent.Children.Insert(index, fresh);
            try { UnregisterName("YouTubePlayer"); } catch (ArgumentException) { }
            RegisterName("YouTubePlayer", fresh);
            YouTubePlayer = fresh;
            DisposeView(old);
        }

        private void DisposePlayer()
        {
            if (playerDisposed) return;
            playerDisposed = true;
            DisposeView(YouTubePlayer);
        }

        private static void DisposeView(Microsoft.Web.WebView2.Wpf.WebView2 view)
        {
            try { view?.Dispose(); }
            catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { } // a crashed browser may object; the view is gone either way
        }

        // The player's CoreWebView2 — null before it is ready, after it is disposed, or once its browser crashed (the view
        // then throws on every access).
        private CoreWebView2 LivePlayerCore
        {
            get
            {
                if (playerDisposed || YouTubePlayer == null) return null;
                try { return YouTubePlayer.CoreWebView2; }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException || ex is ObjectDisposedException) { return null; }
            }
        }

        private async void PlayerMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                if (closed || !e.Source.StartsWith(EmbeddedBrowser.Origin, StringComparison.Ordinal)) return;
                JObject message;
                try { message = JObject.Parse(e.WebMessageAsJson); } catch (JsonException) { return; }
                await HandlePlayerMessageAsync(message);
            }
            catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { } // an odd or late message must never end the app
        }

        // Messages from player.html. Every field is read by type (a wrong type counts as missing) and numbers are kept in range.
        internal async Task HandlePlayerMessageAsync(JObject message)
        {
            if (closed || message == null) return;
            string type = MessageText(message, "type");
            if (type == "ready") { shellReady?.TrySetResult(true); return; }
            if (playing?.IsYouTube != true || MessageNumber(message, "request") != playRequest) return;
            if (type == "ended") await AdvanceAsync(1, true);
            else if (type == "error") YouTubeError(MessageText(message, "code"));
            else if (type == "blocked") MusicStatus.Text = "자동 재생이 차단되었습니다. 영상 안의 재생 버튼을 눌러 주세요.";
            else if (type == "time")
            {
                ytPosition = MessageSeconds(message, "current");
                ytDuration = MessageSeconds(message, "duration");
                double? index = MessageNumber(message, "index");
                if (index >= 0 && index < 100000) ytIndex = (int)index.Value;
            }
            else if (type == "title") ApplyYouTubeTitle(MessageText(message, "title"));
            else if (type == "state")
            {
                paused = MessageNumber(message, "state") != 1; PlayButton.Content = paused ? "재생" : "일시정지";
                NotifyPlayback();
                if (!paused) { MusicStatus.Text = "YouTube 재생 중" + SkippedNote(); autoSkips = 0; }
            }
        }

        private void YouTubeError(string codeText)
        {
            MusicTrack failed = playing;
            // YouTube's player script could not be loaded (offline, blocked): that page stays without it, so the next YouTube
            // song loads the page afresh — also when this one is only skipped during automatic advance.
            if (codeText == "network") playerInitialization = null;
            if (SkipBroken(failed)) return; // automatic advance: on to the next song
            if (codeText == "network")
            {
                StopPlayback();
                MusicStatus.Text = "YouTube에 연결할 수 없습니다. 인터넷 연결을 확인한 뒤 다시 재생해 주세요.";
                return;
            }
            if (int.TryParse(codeText, out int code) && (code == 100 || code == 101 || code == 150 || code == 153))
            {
                StopPlayback();
                NowPlaying.Text = failed?.Title ?? "";
                OpenYouTubePage(failed);
                return;
            }
            paused = true; PlayButton.Content = "재생";
            NotifyPlayback();
            MusicStatus.Text = "YouTube 재생 오류 " + (codeText != null && codeText.Length <= 12 ? codeText : "") +
                ". 연결을 확인하거나 ‘YouTube에서 열기’를 사용하세요.";
        }

        /// <summary>A text (or number) field of a player message; null when missing or of another type.</summary>
        public static string MessageText(JObject message, string name)
        {
            if (!(message?[name] is JValue value) || value.Value == null) return null;
            switch (value.Type)
            {
                case JTokenType.String: return (string)value.Value;
                case JTokenType.Integer:
                case JTokenType.Float: return Convert.ToString(value.Value, CultureInfo.InvariantCulture);
                default: return null;
            }
        }

        /// <summary>A finite number field of a player message; null when missing, of another type, NaN or infinite.</summary>
        public static double? MessageNumber(JObject message, string name)
        {
            if (!(message?[name] is JValue value) || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)) return null;
            double number;
            try { number = Convert.ToDouble(value.Value, CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is InvalidCastException || ex is OverflowException || ex is FormatException) { return null; }
            return double.IsNaN(number) || double.IsInfinity(number) ? (double?)null : number;
        }

        /// <summary>A time field in seconds, kept between 0 and 30 days (0 when missing or odd).</summary>
        public static TimeSpan MessageSeconds(JObject message, string name) =>
            TimeSpan.FromSeconds(Math.Max(0, Math.Min(30 * 86400.0, MessageNumber(message, name) ?? 0)));

        private void Post(object message)
        {
            var core = LivePlayerCore;
            if (core == null) return;
            try { core.PostWebMessageAsJson(JsonConvert.SerializeObject(message)); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException) { }
        }
        private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (loading) return;
            settings.Volume = VolumeSlider.Value; media.Volume = settings.Volume;
            if (playing?.IsYouTube == true) Post(new { action = "volume", volume = Math.Round(settings.Volume * 100) });
            if (!settingVolume) SaveVolumeSoon(); // this window's own slider; SetVolume's callers save for themselves
            NotifyPlayback();
        }

        // The slider fires for every step of a drag: save once it rests.
        private DispatcherTimer volumeSaveTimer;
        private bool settingVolume;

        private void SaveVolumeSoon()
        {
            if (volumeSaveTimer == null)
            {
                volumeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                volumeSaveTimer.Tick += (s, e) => { volumeSaveTimer.Stop(); if (!closed) save(); };
            }
            volumeSaveTimer.Stop();
            volumeSaveTimer.Start();
        }
        private void SetRepeat(bool value)
        {
            settings.Repeat = value;
            RepeatToggle.IsChecked = value;
            save();
            NotifyPlayback();
        }

        private void Repeat_Click(object sender, RoutedEventArgs e) => SetRepeat(RepeatToggle.IsChecked == true);

        // 초기화 → 정말 초기화 → play options back to the defaults (playlists and tracks stay).
        private bool playOptionsResetArmed;

        private void PlayOptionsReset_Click(object sender, RoutedEventArgs e)
        {
            if (!playOptionsResetArmed)
            {
                playOptionsResetArmed = true;
                PlayOptionsResetButton.Content = "정말 초기화";
                return;
            }
            playOptionsResetArmed = false;
            PlayOptionsResetButton.Content = "초기화";
            var defaults = new MusicSettings();
            SetVolume(defaults.Volume); // first: the saves below keep it
            SetRepeat(defaults.Repeat);
            SetShuffle(defaults.Shuffle);
            SetRepeatOne(defaults.RepeatOne);
            ExpandPlaylists.IsChecked = true;
        }

        // Controls used by the mini window's player bar. PlaybackChanged fires whenever play state, track or mode changes.
        public static event Action PlaybackChanged;
        private void NotifyPlayback() => PlaybackChanged?.Invoke();
        public static void RaisePlaybackChanged() => PlaybackChanged?.Invoke();
        public bool IsPlaying => playing != null && !paused;
        public string NowPlayingTitle => playing == null ? null : liveTitle ?? playing.Title;
        private string liveTitle; // title reported by the YouTube player for the video actually playing

        private void ApplyYouTubeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title) || playing == null) return;
            title = title.Trim();
            if (title.Length > 300) title = title.Substring(0, 300); // YouTube titles are at most 100 characters
            if (title == liveTitle) return;
            liveTitle = title;
            NowPlaying.Text = liveTitle;
            // A single-video entry still named after its ID ("YouTube · abc…") takes the real title for good.
            if (FeatureRules.TryYouTube(playing.Source, out string video, out _) && video != null &&
                (playing.Title ?? "").StartsWith("YouTube · ", StringComparison.Ordinal))
            {
                playing.Title = liveTitle;
                TrackList.Items.Refresh();
                save();
            }
            NotifyPlayback();
        }
        public bool HasTracks => playlist?.Tracks.Count > 0;
        public double Volume => settings.Volume;
        public MusicPlaylist CurrentPlaylist => playlist;

        // Seek bar: position and length of the song playing now (null when nothing is loaded or the length is unknown).
        private TimeSpan ytPosition, ytDuration;
        public TimeSpan? Position => playing == null ? (TimeSpan?)null : playing.IsYouTube ? ytPosition : media.Position;
        public TimeSpan? Duration => playing == null ? (TimeSpan?)null
            : playing.IsYouTube ? (ytDuration > TimeSpan.Zero ? ytDuration : (TimeSpan?)null)
            : media.NaturalDuration.HasTimeSpan ? media.NaturalDuration.TimeSpan : (TimeSpan?)null;

        public void Seek(TimeSpan position)
        {
            if (playing == null || position < TimeSpan.Zero) return;
            if (playing.IsYouTube) { ytPosition = position; Post(new { action = "seek", seconds = position.TotalSeconds }); }
            else media.Position = position;
        }
        public void SelectPlaylist(MusicPlaylist list) { if (list != null && settings.Playlists.Contains(list)) PlaylistCombo.SelectedItem = list; }
        public void SetVolume(double value)
        {
            settingVolume = true; // the caller (mini bar, 초기화) saves: no extra save from the slider
            try { VolumeSlider.Value = Math.Max(0, Math.Min(1, value)); } // VolumeChanged applies it
            finally { settingVolume = false; }
        }
        public System.Collections.Generic.IReadOnlyList<MusicTrack> CurrentTracks => playlist?.Tracks ?? new System.Collections.Generic.List<MusicTrack>();
        public MusicTrack PlayingTrack => playing;
        public Task PlayFromListAsync(MusicTrack track) => PlaySelectedTrackAsync(track);
        public void MoveTrack(MusicTrack track, int index) { MoveTrackTo(track, index); NotifyPlayback(); }
        public Task TogglePlayAsync() => TogglePlaybackAsync();
        public Task NextAsync() => AdvanceAsync(1, false);
        public Task PreviousAsync() => AdvanceAsync(-1, false);
        public PlayMode Mode => settings.RepeatOne ? PlayMode.RepeatOne : settings.Shuffle ? PlayMode.Shuffle : PlayMode.Sequential;

        // 미니 막대의 모드 버튼: 순차 / 랜덤 / 한 곡 반복 중 하나. 목록 반복(Repeat)은 그대로 둡니다.
        public void SetPlayMode(PlayMode mode)
        {
            settings.RepeatOne = mode == PlayMode.RepeatOne;
            RepeatOneToggle.IsChecked = settings.RepeatOne;
            SetShuffle(mode == PlayMode.Shuffle); // saves and notifies
        }

        private void SetRepeatOne(bool value)
        {
            settings.RepeatOne = value;
            RepeatOneToggle.IsChecked = value;
            save();
            NotifyPlayback();
        }

        private void RepeatOne_Click(object sender, RoutedEventArgs e) => SetRepeatOne(RepeatOneToggle.IsChecked == true);

        private void SetShuffle(bool value)
        {
            settings.Shuffle = value;
            ShuffleToggle.IsChecked = value;
            if (value) ResetShuffle(playing ?? TrackList.SelectedItem as MusicTrack);
            else ResetShuffle();
            save();
            NotifyPlayback();
        }

        private void Shuffle_Click(object sender, RoutedEventArgs e) => SetShuffle(ShuffleToggle.IsChecked == true);

        // Background mode: the window stays "shown" (so the YouTube WebView keeps playing) but sits outside every
        // monitor, off the taskbar and never activated. ShowPlayer brings it back; closing it while music plays returns here.
        private bool inBackground;
        public bool InBackground => inBackground;

        private void MoveToBackground()
        {
            inBackground = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            ParkOffscreen();
            if (!IsVisible) Show();
        }

        private void ParkOffscreen()
        {
            double width = ActualWidth > 0 ? ActualWidth : Width;
            Left = SystemParameters.VirtualScreenLeft - width - 400;
            Top = SystemParameters.VirtualScreenTop;
        }

        // Small screens (or large scaling): the whole window fits the work area, and its page scrolls inside.
        private void FitToWorkArea()
        {
            var area = SystemParameters.WorkArea;
            double height = Math.Max(320, area.Height - 40), width = Math.Max(480, area.Width - 40);
            if (MinHeight > height) MinHeight = height;
            if (MinWidth > width) MinWidth = width;
            if (Height > height) Height = height;
            if (Width > width) Width = width;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Closing the settings window keeps the music going; only app exit (or nothing playing) really closes it.
            if (!closed && playing != null && !Dispatcher.HasShutdownStarted && Application.Current?.Dispatcher.HasShutdownStarted != true)
            {
                e.Cancel = true;
                MoveToBackground();
                return;
            }
            base.OnClosing(e);
        }

        public void RefreshTrackTitles()
        {
            TrackList.Items.Refresh();
            if (playing != null && liveTitle == null) NowPlaying.Text = playing.Title;
            NotifyPlayback();
        }

        public void ShowPlayer()
        {
            FitToWorkArea();
            if (inBackground)
            {
                inBackground = false;
                ShowInTaskbar = false; // like the other extra windows: no taskbar button
                ShowActivated = true;
                var area = SystemParameters.WorkArea;
                double width = Math.Min(Width, ActualWidth > 0 ? ActualWidth : Width), height = Math.Min(Height, ActualHeight > 0 ? ActualHeight : Height);
                Left = area.Left + Math.Max(0, (area.Width - width) / 2);
                Top = area.Top + Math.Max(0, (area.Height - height) / 2);
            }
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            ShowSelectedTrack(); // songs changed while the window was away: show the current one
        }

        private ContextMenu quickMenu;
        private DateTime quickMenuClosedAt;

        public void OpenQuickMenu(FrameworkElement target)
        {
            if (target == null) return;
            // Pressing the same click again while the menu shows (or the press that just closed it) only closes it.
            if (quickMenu != null && quickMenu.IsOpen) { quickMenu.IsOpen = false; return; }
            if ((DateTime.Now - quickMenuClosedAt).TotalMilliseconds < 300) return;
            RepeatToggle.IsChecked = settings.Repeat;
            ShuffleToggle.IsChecked = settings.Shuffle;
            RepeatOneToggle.IsChecked = settings.RepeatOne;
            var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint,
                Style = (Style)FindResource("AuxContextMenu") };
            quickMenu = menu;
            var previousMenu = target.ContextMenu;
            menu.Closed += (s, e) =>
            {
                quickMenuClosedAt = DateTime.Now;
                // Only while it shows: a later right-click must not bring back this menu with its old state.
                if (target.ContextMenu == menu) target.ContextMenu = previousMenu;
            };
            menu.Resources[typeof(MenuItem)] = FindResource("AuxMenuItem");
            target.ContextMenu = menu;
            menu.Items.Add(new MenuItem
            {
                Header = "현재 재생: " + (playing == null ? "없음" : playing.Title),
                IsEnabled = false
            });

            var repeat = new MenuItem { Header = "반복 재생", IsCheckable = true, IsChecked = settings.Repeat };
            repeat.Click += (s, e) => SetRepeat(repeat.IsChecked);
            menu.Items.Add(repeat);
            var shuffle = new MenuItem { Header = "랜덤 재생", IsCheckable = true, IsChecked = settings.Shuffle };
            shuffle.Click += (s, e) => SetShuffle(shuffle.IsChecked);
            menu.Items.Add(shuffle);
            var repeatOne = new MenuItem { Header = "한 곡 반복", IsCheckable = true, IsChecked = settings.RepeatOne };
            repeatOne.Click += (s, e) => SetRepeatOne(repeatOne.IsChecked);
            menu.Items.Add(repeatOne);
            menu.Items.Add(new Separator());

            var play = new MenuItem
            {
                Header = playing != null && !paused ? "일시정지" : "재생",
                IsEnabled = playlist?.Tracks.Count > 0
            };
            play.Click += async (s, e) => await TogglePlaybackAsync();
            menu.Items.Add(play);
            var stop = new MenuItem { Header = "정지", IsEnabled = playing != null };
            stop.Click += (s, e) => { StopPlayback(); ResetShuffle(); };
            menu.Items.Add(stop);
            var previous = new MenuItem { Header = "이전 곡", IsEnabled = playlist?.Tracks.Count > 0 };
            previous.Click += async (s, e) => await AdvanceAsync(-1, false);
            menu.Items.Add(previous);
            var next = new MenuItem { Header = "다음 곡", IsEnabled = playlist?.Tracks.Count > 0 };
            next.Click += async (s, e) => await AdvanceAsync(1, false);
            menu.Items.Add(next);

            var tracks = new MenuItem { Header = "재생할 곡 선택", IsEnabled = playlist?.Tracks.Count > 0 };
            foreach (MusicTrack track in playlist?.Tracks ?? new System.Collections.Generic.List<MusicTrack>())
            {
                var item = new MenuItem { Header = track.Title ?? track.Source, IsCheckable = true, IsChecked = track == playing, Tag = track };
                item.Click += QuickTrack_Click;
                tracks.Items.Add(item);
            }
            menu.Items.Add(tracks);

            var playlists = new MenuItem { Header = "플레이리스트 선택", IsEnabled = settings.Playlists.Count > 0 };
            foreach (MusicPlaylist list in settings.Playlists)
            {
                var item = new MenuItem { Header = list.Name, IsCheckable = true, IsChecked = list == playlist, Tag = list };
                item.Click += QuickPlaylist_Click;
                playlists.Items.Add(item);
            }
            menu.Items.Add(playlists);
            var edit = new MenuItem { Header = "플레이리스트 수정…" };
            edit.Click += (s, e) => ShowPlayer();
            menu.Items.Add(edit);
            menu.Items.Add(new Separator());
            var exit = new MenuItem { Header = "종료" };
            exit.Click += (s, e) => exitApplication?.Invoke();
            menu.Items.Add(exit);
            menu.IsOpen = true;
        }

        private async void QuickTrack_Click(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuItem;
            var track = item?.Tag as MusicTrack;
            if (track == null) return;
            await PlaySelectedTrackAsync(track);
        }

        private void QuickPlaylist_Click(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuItem;
            var selected = item?.Tag as MusicPlaylist;
            if (selected != null) PlaylistCombo.SelectedItem = selected;
        }

        private void OpenYouTube_Click(object sender, RoutedEventArgs e)
        {
            var track = TrackList.SelectedItem as MusicTrack;
            if (track?.IsYouTube != true) { MusicStatus.Text = "YouTube 곡을 선택하세요."; return; }
            StopPlayback();
            OpenYouTubePage(track);
        }

        private void OpenYouTubePage(MusicTrack track)
        {
            // Only ever a plain YouTube page rebuilt from the link's ids — never the stored text itself (an edited or
            // imported list could hold anything there).
            string url = CanonicalYouTubeUrl(track?.Source);
            if (url == null) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is FileNotFoundException)
            {
                MusicStatus.Text = "기본 브라우저를 열 수 없습니다.";
                return;
            }
            MusicStatus.Text = "이 영상은 앱 안에서 재생할 수 없어 YouTube를 열었습니다.";
        }

        /// <summary>The YouTube (or YouTube Music) page of a track's link, rebuilt from its ids; null for anything else.</summary>
        public static string CanonicalYouTubeUrl(string source)
        {
            if (!FeatureRules.TryYouTube(source, out string video, out string list)) return null;
            FeatureRules.TryYouTubePlaylistId(source, out string carried);
            bool music = Uri.TryCreate(source, UriKind.Absolute, out Uri uri) && uri.Host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase);
            string site = music ? "https://music.youtube.com/" : "https://www.youtube.com/";
            return video != null
                ? site + "watch?v=" + video + (carried != null ? "&list=" + carried : "")
                : site + "playlist?list=" + list;
        }
    }
}
