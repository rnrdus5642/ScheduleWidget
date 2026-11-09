using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;

namespace ScheduleWidget
{
    internal sealed class PlaylistEdit
    {
        internal string Name;
        internal List<MusicTrack> Tracks;
        internal bool ExpandPlaylists;
    }

    public sealed class TrackNumberConverter : IValueConverter
    {
        public object Convert(object value, Type type, object parameter, CultureInfo culture) => value is int index ? (index + 1).ToString(culture) : "";
        public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    public partial class PlaylistEditorWindow : Window
    {
        private readonly Func<PlaylistEdit, string> commit;
        private readonly EnterKeySubmit nameSubmit = new EnterKeySubmit();
        private readonly AuxiliaryWindowPlacement placement;
        private bool closed, importing, committed;
        private Func<string, Task<string>> youTubeTitleLookupOverride = null;
        public ObservableCollection<MusicTrack> DraftTracks { get; }

        internal PlaylistEditorWindow(MusicPlaylist original, bool expand, Func<PlaylistEdit, string> commit)
        {
            this.commit = commit ?? throw new ArgumentNullException(nameof(commit));
            // Membership/order are edited locally. Retained track objects keep the currently playing track's identity.
            DraftTracks = new ObservableCollection<MusicTrack>(original?.Tracks ?? new List<MusicTrack>());
            InitializeComponent();
            DataContext = this;
            Title = EditorTitle.Text = original == null ? "플레이리스트 추가" : "플레이리스트 수정";
            PlaylistName.Text = original?.Name ?? "";
            ExpandPlaylists.IsChecked = expand;
            DraftTracks.CollectionChanged += (s, e) => UpdateTrackCount();
            UpdateTrackCount();
            ChromelessWindow.Apply(this);
            placement = new AuxiliaryWindowPlacement(this);
            Loaded += (s, e) => { PlaylistName.Focus(); PlaylistName.SelectAll(); };
            Closed += (s, e) => closed = true;
        }

        private void UpdateTrackCount()
        {
            TrackCount.Text = DraftTracks.Count.ToString(CultureInfo.CurrentCulture) + "곡";
            EmptyHint.Visibility = DraftTracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        internal void AddFiles(IEnumerable<string> paths, int index = int.MaxValue)
        {
            if (closed || importing) return;
            int at = Math.Max(0, Math.Min(index, DraftTracks.Count)), count = 0;
            foreach (string path in paths)
            {
                DraftTracks.Insert(at++, new MusicTrack { Source = path, Title = Path.GetFileNameWithoutExtension(path) });
                count++;
            }
            EditorStatus.Text = count + "곡을 추가했습니다. 저장하면 목록에 반영됩니다.";
        }

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var picker = new OpenFileDialog { Multiselect = true, Title = "음악 파일 선택 · 여러 개 선택 가능",
                Filter = "음악 파일|*" + string.Join(";*", MusicWindow.MusicExtensions) + "|모든 파일|*.*" };
            if (picker.ShowDialog(this) == true) AddFiles(picker.FileNames);
        }

        private void Files_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = !importing && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Files_Drop(object sender, DragEventArgs e)
        {
            if (!importing && e.Data.GetDataPresent(DataFormats.FileDrop)) AddFiles(MusicWindow.DroppedMusicFiles(e.Data));
            e.Handled = true;
        }

        private void MoveTrack_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.DataContext is MusicTrack track)) return;
            int current = DraftTracks.IndexOf(track), next = current + int.Parse((string)button.Tag);
            if (current >= 0 && next >= 0 && next < DraftTracks.Count) DraftTracks.Move(current, next);
        }

        private void RemoveTrack_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is MusicTrack track) DraftTracks.Remove(track);
        }

        private async void AddLinks_Click(object sender, RoutedEventArgs e) => await ImportYouTubeAsync();

        internal async Task ImportYouTubeAsync()
        {
            if (closed || importing) return;
            string[] links = YouTubeLinks.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(link => link.Trim()).Where(link => link.Length > 0).ToArray();
            if (links.Length == 0) return;
            for (int i = 0; i < links.Length; i++)
                if (!FeatureRules.TryYouTube(links[i], out _, out _))
                { EditorStatus.Text = (i + 1) + "번째 링크를 확인해 주세요. YouTube 영상·재생목록 주소를 입력하세요."; return; }
            bool expand = ExpandPlaylists.IsChecked == true;
            var tracks = new List<MusicTrack>();
            string warning = null;
            importing = true;
            AddFilesButton.IsEnabled = AddLinksButton.IsEnabled = YouTubeLinks.IsEnabled = ExpandPlaylists.IsEnabled = SaveButton.IsEnabled = false;
            AddLinksButton.Content = "추가 중…";
            try
            {
                foreach (string link in links)
                {
                    FeatureRules.TryYouTube(link, out string video, out string list);
                    if (expand && FeatureRules.TryYouTubePlaylistId(link, out string listId))
                    {
                        EditorStatus.Text = "재생목록의 곡을 가져오는 중…";
                        try
                        {
                            var videos = await YouTubePlaylistService.FetchAsync(listId);
                            if (closed) return;
                            tracks.AddRange(videos.Select(item => new MusicTrack { Source = "https://www.youtube.com/watch?v=" + item.Id, Title = item.Title }));
                            continue;
                        }
                        catch (InvalidOperationException error) { warning = error.Message; }
                    }
                    string title = video == null ? null : await (youTubeTitleLookupOverride ?? YouTubePlaylistService.FetchVideoTitleAsync)(video);
                    if (closed) return;
                    tracks.Add(new MusicTrack { Source = link, Title = title ?? (video != null ? "YouTube · " + video : "YouTube 재생목록 · " + list) });
                }
                if (closed) return;
                foreach (var track in tracks) DraftTracks.Add(track);
                YouTubeLinks.Clear();
                EditorStatus.Text = tracks.Count + "곡을 추가했습니다. 저장하면 목록에 반영됩니다." +
                    (warning == null ? "" : " 일부 재생목록은 링크로 추가했습니다: " + warning);
            }
            catch (Exception error) when (!SystemMediaService.IsFatal(error))
            {
                if (!closed) EditorStatus.Text = "곡을 가져오지 못했습니다. 다시 시도해 주세요. " + error.Message;
            }
            finally
            {
                importing = false;
                if (!closed)
                {
                    AddFilesButton.IsEnabled = AddLinksButton.IsEnabled = YouTubeLinks.IsEnabled = ExpandPlaylists.IsEnabled = SaveButton.IsEnabled = true;
                    AddLinksButton.Content = "링크 추가";
                }
            }
        }

        internal bool TryCommit()
        {
            if (closed || importing || committed) return false;
            string name = PlaylistName.Text.Trim();
            if (name.Length == 0) { EditorStatus.Text = "플레이리스트 이름을 입력하세요."; PlaylistName.Focus(); return false; }
            string error;
            try { error = commit(new PlaylistEdit { Name = name, Tracks = DraftTracks.ToList(), ExpandPlaylists = ExpandPlaylists.IsChecked == true }); }
            catch (Exception failure) when (!SystemMediaService.IsFatal(failure))
            { EditorStatus.Text = "저장하지 못했습니다. 다시 시도해 주세요. " + failure.Message; return false; }
            if (error != null) { EditorStatus.Text = error; return false; }
            committed = true;
            return true;
        }

        private void Save_Click(object sender, RoutedEventArgs e) { if (TryCommit()) Close(); }
        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
        private void PlaylistName_KeyDown(object sender, KeyEventArgs e) => nameSubmit.Handle(e, () => { if (!closed) Save_Click(sender, new RoutedEventArgs()); });
        private void Editor_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        }
    }
}
