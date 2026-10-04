using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static SystemMediaService.NowPlaying Media(string id, string title, bool playing = true, bool current = false) =>
            new SystemMediaService.NowPlaying { AppId = id, SourceName = id, Title = title, IsPlaying = playing, IsCurrent = current,
                Duration = TimeSpan.FromSeconds(200), Position = TimeSpan.FromSeconds(30), UpdatedAt = DateTimeOffset.UtcNow };

        private static void AutomaticMediaSelection()
        {
            var selector = new AutomaticMediaSource();
            DateTime now = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);
            var edge = Media("Edge", "Edge song");
            var chrome = Media("Chrome", "Chrome song", current: true);
            string picked = selector.Select(new[] { edge, chrome }, null, false, true, now);
            Require(picked == "Chrome", "The Windows current source was not selected on the first scan.");
            Require(selector.Select(new[] { chrome, edge }, picked, false, true, now.AddSeconds(1)) == picked, "Enumeration order changed the source.");
            selector.Select(new[] { Media("Edge", "Edge song", false), chrome }, picked, false, true, now.AddSeconds(2));
            picked = selector.Select(new[] { edge, chrome }, picked, false, true, now.AddSeconds(3));
            Require(picked == "Edge", "A newly started external app did not become the source.");
            picked = selector.Select(new[] { Media("Edge", "Edge song", false) }, picked, false, true, now.AddSeconds(4));
            Require(picked == "Edge", "Pausing discarded the source needed for resume.");
            Require(selector.Select(new[] { chrome }, "Edge", false, false, now.AddSeconds(5)) == "Edge", "Automatic detection overrode a manual app choice.");
            Require(selector.Select(new[] { chrome }, null, false, false, now.AddSeconds(6)) == null, "Automatic detection overrode a manual playlist choice.");
            Require(selector.Select(new[] { chrome }, "Chrome", true, true, now.AddSeconds(7)) == null, "External media displaced the widget's own active player.");
            selector.Reset();
            Require(selector.Select(new[] { Media("Edge", "Old song", false) }, null, false, true, now) == null, "A paused stale source was automatically chosen.");
            picked = selector.Select(new[] { edge }, null, false, true, now);
            Require(selector.Select(new SystemMediaService.NowPlaying[0], picked, false, true, now.AddSeconds(1)) == picked, "A transient missing source was cleared immediately.");
            Require(selector.Select(new[] { edge }, picked, false, true, now.AddSeconds(2)) == picked, "A recovered source was not retained.");
            selector.Select(new SystemMediaService.NowPlaying[0], picked, false, true, now.AddSeconds(3));
            Require(selector.Select(new SystemMediaService.NowPlaying[0], picked, false, true, now.AddSeconds(9)) == null, "A closed source never returned to the widget.");
            selector.Reset();
            selector.Select(new[] { edge }, null, false, true, now);
            Require(selector.Select(new[] { chrome }, "Edge", false, true, now.AddSeconds(1)) == "Chrome", "A playing alternative did not replace a closed source.");
        }

        private static void AutomaticMediaRouting()
        {
            var main = new MainWindow();
            ((TrayService)Field(main, "trayService")).Dispose();
            var data = new AppData { MiniCharacterVisible = false, MiniPlayerVisible = true, StartupEnabled = false, BringToFrontHotKeyEnabled = false };
            var playlist = new MusicPlaylist { Name = "Own playlist" };
            playlist.Tracks.Add(new MusicTrack { Title = "Embedded track", Source = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" });
            data.Music.Playlists.Add(playlist);
            data.Music.SelectedPlaylistId = playlist.Id;
            SetField(main, "appData", data);
            SetField(main, "dataStore", Store("automatic-media"));
            var controls = (IMusicControls)main;
            var mini = new MiniWindow(data, () => true, () => { }, player: controls);
            SetField(main, "miniWindow", mini);
            var sources = new List<SystemMediaService.NowPlaying> { Media("Edge", "Edge song") };
            SetField(main, "mediaSourcesOverride", (Func<Task<List<SystemMediaService.NowPlaying>>>)(() => Task.FromResult(sources.ToList())));
            SetField(main, "mediaVolumeOverride", (Func<string, float?>)(_ => .25f));
            int notifications = 0;
            Action changed = () => notifications++;
            MusicWindow.PlaybackChanged += changed;
            void Poll()
            {
                ((Task)typeof(MainWindow).GetMethod("PollExternalMediaAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(main, null)).GetAwaiter().GetResult();
                // The normal event queues this UI refresh. Do not pump App's startup operation in headless checks.
                mini.UpdatePlayerBar();
            }
            try
            {
                Require(controls.AutomaticSource, "Automatic mode is not the startup default.");
                Poll();
                Require(notifications > 0 && controls.SelectedExternal == "Edge" && controls.NowPlaying == "Edge song" && Control<System.Windows.Controls.TextBlock>(mini, "PlayerTitle").Text == "Edge song", "External playback did not automatically reach the bar.");
                Require(Field(main, "musicWindow") == null && Math.Abs(controls.Volume - .25) < .001, "Detection created an internal player or lost external volume.");
                sources = new List<SystemMediaService.NowPlaying> { Media("msedgewebview2", "Embedded track", current: true), Media("Edge", "Edge song") };
                Poll();
                Require(controls.SelectedExternal == "Edge" && controls.ExternalSources.Count == 1, "The widget's embedded YouTube player was detected as another app.");
                controls.SelectExternal("Edge");
                sources = new List<SystemMediaService.NowPlaying> { Media("Edge", "Paused Edge", false), Media("Chrome", "Chrome song") };
                Poll();
                Require(!controls.AutomaticSource && controls.SelectedExternal == "Edge", "Manual selection was replaced by a new external song.");
                controls.SelectAutomaticSource();
                Poll();
                Require(controls.AutomaticSource && controls.SelectedExternal == "Chrome" && controls.NowPlaying == "Chrome song", "Returning to automatic mode did not select active media.");
                Call(mini, "BuildSourceChoices");
                var autoRow = Control<ContentControl>(mini, "AutomaticSourceChoice").Content;
                Require((bool)autoRow.GetType().GetProperty("IsCurrent").GetValue(autoRow), "The dropdown does not show automatic mode as selected.");
                Call(main, "EnsureMusicWindow");
                var music = (MusicWindow)Field(main, "musicWindow");
                var silent = new MusicTrack { Title = "Own silent track", Source = SilentWave() };
                playlist.Tracks.Add(silent);
                music.SetVolume(0);
                music.PlayFromListAsync(silent).GetAwaiter().GetResult();
                Require(controls.SelectedExternal == null && controls.NowPlaying == silent.Title, "Starting music in settings left the bar on an external app.");
                Poll();
                Require(controls.SelectedExternal == null, "Automatic detection interrupted the active internal player.");
                Call(music, "StopPlayback");
                Poll();
                Require(controls.SelectedExternal == "Chrome", "External media did not return after internal playback ended.");
                sources = new List<SystemMediaService.NowPlaying> { Media("Chrome", "Paused Chrome", false) };
                Poll();
                controls.SelectPlaylist(playlist); // paused source: no real OS pause command is needed
                sources = new List<SystemMediaService.NowPlaying> { Media("Edge", "New external song") };
                Poll();
                Require(!controls.AutomaticSource && controls.SelectedExternal == null, "A manual playlist choice was not retained.");
                var timer = new System.Windows.Threading.DispatcherTimer();
                SetField(main, "externalMediaTimer", timer);
                Call(main, "UpdateExternalPollInterval");
                Require(timer.Interval == TimeSpan.FromSeconds(1.5), "A visible idle bar still waits five seconds for external playback.");
                Require(!mini.IsVisible, "Automatic routing displayed a check window.");
            }
            finally
            {
                MusicWindow.PlaybackChanged -= changed;
                if (Field(main, "musicWindow") is MusicWindow music) { Call(music, "StopPlayback"); music.Close(); }
                mini.Close();
            }
        }
    }
}
