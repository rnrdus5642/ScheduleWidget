using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static void PlaylistPopupAudit()
        {
            Run("Playlist popup rollback preserves playback; successful edits and deletions commit once", () =>
            {
                var data = new MusicSettings(); bool reject = false; int saves = 0;
                var music = new MusicWindow(data, () => { saves++; return !reject; }, () => { }, embedded: true);
                try
                {
                    var list = music.CurrentPlaylist;
                    var track = new MusicTrack { Title = "Silent popup check", Source = SilentWave() };
                    list.Tracks.Add(track); Call(music, "RefreshTracks", (MusicTrack)null);
                    music.SetVolume(0); music.PlayFromListAsync(track).GetAwaiter().GetResult();
                    var originalTracks = list.Tracks; string originalName = list.Name;
                    var edit = music.CreatePlaylistEditor(false);
                    try
                    {
                        Control<TextBox>(edit, "PlaylistName").Text = "수정한 이름";
                        edit.AddFiles(new[] { SilentWave(), SilentWave() });
                        reject = true;
                        Require(!edit.TryCommit() && ReferenceEquals(list.Tracks, originalTracks) && list.Name == originalName &&
                            music.IsPlaying && ReferenceEquals(music.PlayingTrack, track), "A rejected edit changed the live list or stopped playback.");
                        reject = false; int before = saves;
                        Require(edit.TryCommit() && saves == before + 1 && list.Tracks.Count == 3 && list.Name == "수정한 이름" &&
                            music.IsPlaying && ReferenceEquals(music.PlayingTrack, track), "Successful editing restarted playback or saved multiple times.");
                        Require(!edit.TryCommit() && saves == before + 1, "A repeated save committed twice.");
                    }
                    finally { edit.Close(); }

                    var cancelled = music.CreatePlaylistEditor(false);
                    cancelled.DraftTracks.Clear(); cancelled.Close();
                    Require(list.Tracks.Count == 3 && music.IsPlaying, "Cancel removed tracks or stopped the current song.");

                    reject = true;
                    Require(!music.DeletePlaylist(list) && data.Playlists.Contains(list) && ReferenceEquals(music.CurrentPlaylist, list) && music.IsPlaying,
                        "A failed delete lost its playlist, selection, or playback.");
                    reject = false;
                    Require(music.DeletePlaylist(list) && !data.Playlists.Contains(list) && data.Playlists.Count == 1 && !music.IsPlaying,
                        "Confirmed deletion did not remove the selected list and stop its track.");

                    var create = music.CreatePlaylistEditor(true);
                    try
                    {
                        Control<TextBox>(create, "PlaylistName").Text = "저장 실패";
                        int count = data.Playlists.Count;
                        reject = true;
                        Require(!create.TryCommit() && data.Playlists.Count == count, "A failed create left a phantom playlist.");
                    }
                    finally { create.Close(); }
                }
                finally { reject = false; music.Close(); }
            });

            Run("Closing an importing playlist popup discards late results and keeps save controls visible on small screens", () =>
            {
                int commits = 0;
                var window = new PlaylistEditorWindow(null, true, draft => { commits++; return null; });
                var title = new TaskCompletionSource<string>();
                SetField(window, "youTubeTitleLookupOverride", (Func<string, Task<string>>)(_ => title.Task));
                try
                {
                    foreach (var size in new[] { new Size(680, 740), new Size(500, 480) })
                    {
                        var body = (FrameworkElement)window.Content;
                        body.Measure(size); body.Arrange(new Rect(new Point(), size)); body.UpdateLayout();
                        var save = Control<Button>(window, "SaveButton");
                        Rect bounds = save.TransformToAncestor(body).TransformBounds(new Rect(save.RenderSize));
                        Require(bounds.Bottom <= size.Height && bounds.Right <= size.Width && bounds.Top > 0,
                            "The playlist popup's save button is clipped on a small screen.");
                    }
                    Control<TextBox>(window, "PlaylistName").Text = "취소할 목록";
                    Control<TextBox>(window, "YouTubeLinks").Text = "https://youtu.be/dQw4w9WgXcQ";
                    Task pending = window.ImportYouTubeAsync();
                    Require(!pending.IsCompleted && !Control<Button>(window, "SaveButton").IsEnabled && !window.TryCommit(),
                        "The popup can commit a partially imported batch.");
                    window.Close();
                    title.SetResult("Late title");
                    Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
                    Require(pending.IsCompleted && window.DraftTracks.Count == 0 && commits == 0 && !window.TryCommit(),
                        "A cancelled popup accepted late imports or saved a playlist.");
                    pending.GetAwaiter().GetResult();
                }
                finally { window.Close(); }
            });
        }
    }
}
