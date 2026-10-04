using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace ScheduleWidget
{
    // Reads and controls what other apps are playing through Windows media sessions (the same source as the
    // volume flyout / lock-screen player): YouTube or YouTube Music in Chrome/Edge, the YouTube Music app, Spotify, …
    // Sessions from this app itself (its hidden YouTube player) are skipped.
    public static class SystemMediaService
    {
        public sealed class NowPlaying
        {
            public string AppId { get; set; }       // Windows' id for the playing app (used to pick the session again)
            public string SourceName { get; set; }  // "Chrome (YouTube)", "Chrome (YouTube Music)", "Spotify", …
            public string Title { get; set; }
            public string Artist { get; set; }
            public bool IsPlaying { get; set; }
            // Timeline for the seek bar (null when the app does not report one). Position is as of UpdatedAt.
            public TimeSpan? Position { get; set; }
            public TimeSpan? Duration { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
            public TimeSpan? PositionNow => Position.HasValue && IsPlaying ? Position + (DateTimeOffset.Now - UpdatedAt) : Position;
            public string Display => string.IsNullOrWhiteSpace(Artist) ? Title : Title + " · " + Artist;
        }

        private static Task<GlobalSystemMediaTransportControlsSessionManager> manager;

        private static async Task<GlobalSystemMediaTransportControlsSessionManager> ManagerAsync()
        {
            if (manager == null || manager.IsFaulted) manager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask();
            return await manager.ConfigureAwait(false);
        }

        private static bool IsOwn(GlobalSystemMediaTransportControlsSession session) =>
            (session.SourceAppUserModelId ?? string.Empty).IndexOf("ScheduleWidget", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Every other app with a media session (playing first). Empty when none or unsupported.</summary>
        public static async Task<List<NowPlaying>> GetAllAsync()
        {
            var result = new List<NowPlaying>();
            try
            {
                var sessions = (await ManagerAsync().ConfigureAwait(false)).GetSessions().Where(s => !IsOwn(s)).ToList();
                var titles = sessions.Count > 0 ? WindowTitles() : new List<string>(); // window scan only when something plays
                foreach (var session in sessions)
                {
                    var props = await session.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
                    if (props == null || string.IsNullOrWhiteSpace(props.Title)) continue;
                    string title = props.Title.Trim();
                    string app = AppName(session.SourceAppUserModelId);
                    string site = SiteFromWindowTitle(titles, title);
                    result.Add(new NowPlaying
                    {
                        AppId = session.SourceAppUserModelId, Title = title, Artist = props.Artist?.Trim(),
                        SourceName = site != null ? app + " (" + site + ")" : app,
                        IsPlaying = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    });
                    var timeline = session.GetTimelineProperties();
                    if (timeline != null && timeline.EndTime > timeline.StartTime)
                    {
                        var added = result[result.Count - 1];
                        added.Duration = timeline.EndTime - timeline.StartTime;
                        added.Position = timeline.Position - timeline.StartTime;
                        added.UpdatedAt = timeline.LastUpdatedTime;
                    }
                }
            }
            catch (Exception ex) when (!IsFatal(ex))
            {
                // Media sessions come and go at any moment: a tab closed mid-read ends in RO_E_CLOSED (ObjectDisposedException),
                // a vanished app in Argument / FileNotFound errors, a busy service in COM errors — none may take the app down.
                // A one-off failure keeps the last good answer for a few seconds, so the bar (and the pets sized around it) do
                // not blink empty between two polls. Older Windows or blocked media sessions keep failing: after that window it
                // is "nothing playing".
                if (lastGood != null && DateTime.UtcNow - lastGoodAt < TransientGrace) return lastGood.ToList();
                return result;
            }
            var ordered = result.OrderByDescending(n => n.IsPlaying).ToList();
            lastGood = ordered;
            lastGoodAt = DateTime.UtcNow;
            return ordered;
        }

        private static readonly TimeSpan TransientGrace = TimeSpan.FromSeconds(5);
        private static volatile List<NowPlaying> lastGood;
        private static DateTime lastGoodAt;

        public static Task TogglePlayPauseAsync(string appId) => Run(appId, s => s.TryTogglePlayPauseAsync().AsTask());
        public static Task NextAsync(string appId) => Run(appId, s => s.TrySkipNextAsync().AsTask());
        public static Task PreviousAsync(string appId) => Run(appId, s => s.TrySkipPreviousAsync().AsTask());
        public static Task PauseAsync(string appId) => Run(appId, s => s.TryPauseAsync().AsTask());
        // The bar shows Position − StartTime (see GetAllAsync), so a spot on the bar is StartTime + that spot for the app.
        public static Task SeekAsync(string appId, TimeSpan position) =>
            Run(appId, s => s.TryChangePlaybackPositionAsync(SeekTarget(s.GetTimelineProperties()?.StartTime ?? TimeSpan.Zero, position)).AsTask());

        /// <summary>The app's own position (in ticks) for a point on the seek bar, which counts from the timeline's start.</summary>
        public static long SeekTarget(TimeSpan startTime, TimeSpan barPosition) => Math.Max(0, (startTime + barPosition).Ticks);

        private static async Task Run(string appId, Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
        {
            try
            {
                var session = (await ManagerAsync().ConfigureAwait(false)).GetSessions()
                    .FirstOrDefault(s => string.Equals(s.SourceAppUserModelId, appId, StringComparison.OrdinalIgnoreCase));
                if (session != null) await action(session).ConfigureAwait(false);
            }
            catch (Exception ex) when (!IsFatal(ex)) { } // the session closed meanwhile, the app refused, …: nothing to do
        }

        /// <summary>Errors no handler may swallow; every other failure of another app's media session is just "not now".</summary>
        public static bool IsFatal(Exception ex) =>
            ex is OutOfMemoryException || ex is StackOverflowException || ex is AccessViolationException ||
            ex is System.Threading.ThreadAbortException || ex is AppDomainUnloadedException || ex is BadImageFormatException;

        // "Chrome", "MSEdge", "Spotify.exe", "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" → readable app name.
        private static readonly Dictionary<string, string> KnownApps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "msedgewebview2", "WebView 앱" }, { "chrome", "Chrome" }, { "msedge", "Edge" }, { "308046B0AF4A39CB", "Firefox" }, { "firefox", "Firefox" },
            { "whale", "Whale" }, { "opera", "Opera" }, { "brave", "Brave" }, { "spotify", "Spotify" },
            { "ZuneMusic", "미디어 플레이어" }, { "YouTubeMusic", "YouTube Music" }
        };

        private static string AppName(string appId)
        {
            string id = appId ?? string.Empty;
            string core = id.Contains("!") ? id.Substring(id.LastIndexOf('!') + 1) : id;
            core = core.Replace(".exe", string.Empty);
            foreach (var known in KnownApps)
                if (id.IndexOf(known.Key, StringComparison.OrdinalIgnoreCase) >= 0) return known.Value;
            core = core.Contains(".") ? core.Substring(core.LastIndexOf('.') + 1) : core;
            return string.IsNullOrWhiteSpace(core) ? "다른 앱" : core;
        }

        // Browser windows are titled "<tab title> - <browser>", and YouTube tabs "<video> - YouTube" / "… - YouTube Music".
        // Find the window showing this media title and take the site part. Null when not found (tab not in front).
        private static readonly string[] BrowserSuffixes = { " - Google Chrome", " - Chrome", " - Microsoft​ Edge", " - Microsoft Edge", " - Mozilla Firefox", " - Whale", " - Opera", " - Brave" };

        private static string SiteFromWindowTitle(IEnumerable<string> titles, string mediaTitle)
        {
            string key = mediaTitle.Length > 24 ? mediaTitle.Substring(0, 24) : mediaTitle;
            foreach (string raw in titles)
            {
                if (raw.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string t = raw;
                foreach (string suffix in BrowserSuffixes)
                    if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { t = t.Substring(0, t.Length - suffix.Length); break; }
                int dash = t.LastIndexOf(" - ", StringComparison.Ordinal);
                if (dash < 0) continue;
                string site = t.Substring(dash + 3).Trim();
                if (site.Length > 0 && site.Length <= 30 && site.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0) return site;
            }
            return null;
        }

        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

        private static List<string> WindowTitles()
        {
            var titles = new List<string>();
            EnumWindows((hwnd, l) =>
            {
                if (!IsWindowVisible(hwnd)) return true;
                var text = new StringBuilder(512);
                if (GetWindowText(hwnd, text, text.Capacity) > 0) titles.Add(text.ToString());
                return true;
            }, IntPtr.Zero);
            return titles;
        }
    }
}
