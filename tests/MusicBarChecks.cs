using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static void FixedMusicBar()
        {
            var player = new MusicBarPlayer();
            var data = new AppData { MiniCharacterVisible = false, MiniPlayerVisible = true };
            var mini = new MiniWindow(data, () => true, () => { }, player: player);
            try
            {
                var content = (FrameworkElement)mini.Content;
                var bar = Control<FrameworkElement>(mini, "PlayerBar");
                var calendar = Control<FrameworkElement>(mini, "WeekCalendar");
                void Layout(double width)
                {
                    mini.Width = width;
                    mini.Height = 280;
                    content.Measure(new Size(width, 280));
                    content.Arrange(new Rect(0, 0, width, 280));
                    content.UpdateLayout();
                }
                foreach (double width in new[] { 800d, 640d, 420d, 320d, 228d })
                {
                    player.NowPlaying = null;
                    player.IsPlaying = false;
                    player.SelectedExternal = null;
                    mini.UpdatePlayerBar();
                    Layout(width);
                    double idleBar = bar.ActualHeight, idleCalendar = calendar.ActualHeight;
                    Require(Math.Abs(idleBar - MiniWindow.PlayerBarHeight) < 1, "Idle music bar is not the compact height.");
                    foreach (var state in new[] { "playing", "paused", "external", "stopped" })
                    {
                        player.NowPlaying = state == "stopped" ? null : "아주 긴 곡 제목도 한 줄 음악 막대 안에서 표시합니다";
                        player.IsPlaying = state == "playing" || state == "external";
                        player.SelectedExternal = state == "external" ? "checks.music" : null;
                        player.Duration = state == "stopped" ? (TimeSpan?)null : TimeSpan.FromSeconds(200);
                        player.Position = TimeSpan.FromSeconds(45);
                        mini.UpdatePlayerBar();
                        Layout(width);
                        Require(Math.Abs(bar.ActualHeight - idleBar) < .1 && Math.Abs(calendar.ActualHeight - idleCalendar) < .1,
                            state + " resized the music bar or calendar at width " + width);
                        foreach (string name in new[] { "PlayerPlaylist", "PlayerPrevious", "PlayerPlay", "PlayerNext", "PlayerVolume", "PlayerMode", "SeekSlider" })
                        {
                            var control = Control<FrameworkElement>(mini, name);
                            if (control.Visibility != Visibility.Visible) continue;
                            var bounds = control.TransformToAncestor(bar).TransformBounds(new Rect(control.RenderSize));
                            Require(bounds.Left >= 0 && bounds.Right <= bar.ActualWidth + 1 && bounds.Bottom <= bar.ActualHeight + 1,
                                name + " escaped the compact bar at width " + width);
                        }
                    }
                }
                player.NowPlaying = "Seek check";
                player.Duration = TimeSpan.FromSeconds(200);
                player.Position = TimeSpan.Zero;
                mini.UpdatePlayerBar();
                Layout(800);
                var slider = Control<Slider>(mini, "SeekSlider");
                Require(slider.IsEnabled && slider.Template.FindName("PART_Track", slider) != null, "The thin seek track was not constructed.");
                slider.Value = 60;
                Require(player.LastSeek == TimeSpan.FromSeconds(60), "The thin progress bar no longer seeks.");
                double beforeHide = calendar.ActualHeight;
                mini.SetPlayerVisible(false);
                Layout(800);
                Require(calendar.ActualHeight > beforeHide, "Hiding the player did not release its space.");
                mini.SetPlayerVisible(true);
                Layout(800);
                Require(Math.Abs(calendar.ActualHeight - beforeHide) < .1 && Math.Abs(bar.ActualHeight - MiniWindow.PlayerBarHeight) < 1,
                    "Showing the player did not restore the fixed strip.");
                Require(!mini.IsVisible, "The player layout check showed a desktop window.");
            }
            finally { mini.Close(); }
        }

        private sealed class MusicBarPlayer : IMusicControls
        {
            public bool HasTracks => true;
            public bool IsPlaying { get; set; }
            public string NowPlaying { get; set; }
            public PlayMode Mode { get; private set; }
            public TimeSpan? Position { get; set; }
            public TimeSpan? Duration { get; set; }
            public TimeSpan? LastSeek { get; private set; }
            public double Volume { get; private set; } = .5;
            public MusicPlaylist CurrentPlaylist { get; } = new MusicPlaylist { Name = "내 플레이리스트" };
            public IReadOnlyList<MusicPlaylist> Playlists => new[] { CurrentPlaylist };
            public string SelectedExternal { get; set; }
            public bool AutomaticSource { get; private set; } = true;
            public void SelectAutomaticSource() { AutomaticSource = true; }
            public string SourceName => SelectedExternal == null ? CurrentPlaylist.Name : "테스트 앱";
            public IReadOnlyList<SystemMediaService.NowPlaying> ExternalSources => new[] {
                new SystemMediaService.NowPlaying { AppId = "checks.music", Title = NowPlaying, SourceName = "테스트 앱", IsPlaying = IsPlaying } };
            public IReadOnlyList<MusicTrack> Queue => CurrentPlaylist.Tracks;
            public MusicTrack Current => null;
            public void TogglePlay() { IsPlaying = !IsPlaying; }
            public void Next() { }
            public void Previous() { }
            public void SetMode(PlayMode mode) { Mode = mode; }
            public void ToggleSettings() { }
            public void Seek(TimeSpan position) { LastSeek = Position = position; }
            public void SetVolume(double value) { Volume = value; }
            public void SelectPlaylist(MusicPlaylist list) { AutomaticSource = false; SelectedExternal = null; }
            public void SelectExternal(string appId) { AutomaticSource = false; SelectedExternal = appId; }
            public void Play(MusicTrack track) { }
            public void Move(MusicTrack track, int index) { }
        }
    }
}
