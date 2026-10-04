using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace ScheduleWidget
{
    /// <summary>
    /// Clicking the song title while another app plays: bring that app to the front. For a browser (Edge, Chrome, Whale,
    /// Brave, Firefox, …) the tab whose title holds the song is selected through UI Automation — the tab strip every
    /// browser exposes for screen readers — so no extension is needed.
    /// </summary>
    public static class MediaAppActivator
    {
        /// <summary>
        /// Finds (and in a browser selects) what plays the song, off the UI thread, then brings its window to the front on
        /// the caller's thread — the UI thread that just handled the click, which is the one Windows lets take the foreground.
        /// Returns true when a window was brought to the front.
        /// </summary>
        public static async Task<bool> ActivateAsync(string appId, string title)
        {
            IntPtr hwnd = await Task.Run(() => FindAndSelect(appId, title));
            return hwnd != IntPtr.Zero && BringToFront(hwnd);
        }

        // The window to bring forward (its playing tab already selected), or zero when the app has no window.
        private static IntPtr FindAndSelect(string appId, string title)
        {
            try
            {
                var windows = AppWindows(ProcessNames(appId));
                if (windows.Count == 0) return IntPtr.Zero;
                string song = (title ?? "").Trim();
                // Fast path: a window whose title already shows the song has that tab selected.
                IntPtr exact = song.Length == 0 ? IntPtr.Zero : windows.FirstOrDefault(w => Contains(w.Title, song)).Handle;
                if (exact != IntPtr.Zero) return exact;
                if (song.Length > 0)
                    foreach (var window in windows)
                        if (SelectTab(window.Handle, song)) return window.Handle;
                return windows[0].Handle; // no tab matched: the app's front-most window
            }
            catch (Exception ex) when (!SystemMediaService.IsFatal(ex))
            {
                // The app or tab went away mid-search (ElementNotAvailable, Win32, COM, access errors …): nothing to show.
                return IntPtr.Zero;
            }
        }

        // Windows media session ids → process names: "MSEdge" → msedge, "Chrome" → chrome, "Spotify.exe" → spotify, …
        public static List<string> ProcessNames(string appId)
        {
            string id = (appId ?? "").ToLowerInvariant();
            var names = new List<string>();
            if (id.Contains("edge")) names.Add("msedge");
            if (id.Contains("chrome")) names.Add("chrome");
            if (id.Contains("whale")) names.Add("whale");
            if (id.Contains("brave")) names.Add("brave");
            // Firefox reports a hash of its install folder as its media id (release / Nightly defaults), not its name.
            if (id.Contains("firefox") || id.Contains("mozilla") || id == "308046b0af4a39cb" || id == "6f193ccc56814779") names.Add("firefox");
            if (id.Contains("opera")) names.Add("opera");
            if (id.Contains("vivaldi")) names.Add("vivaldi");
            if (names.Count == 0 && id.Length > 0)
            {
                // "Spotify.exe" / "C:\...\Spotify.exe" → spotify; a packaged id "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"
                // → its app name after "!" (spotify), and the part of the package name before the first dot.
                string last = id.Split('\\', '/', '!').Last();
                if (last.EndsWith(".exe")) last = last.Substring(0, last.Length - 4);
                if (last.Length > 0) names.Add(last);
                if (id.Contains("!"))
                {
                    string package = id.Split('!')[0].Split('.')[0];
                    if (package.EndsWith("ab") && package.Length > 2) package = package.Substring(0, package.Length - 2); // "SpotifyAB"
                    if (package.Length > 0) names.Add(package);
                }
            }
            return names.Where(n => n.Length > 0 && n.IndexOfAny(new[] { '\\', '/', ':' }) < 0).Distinct().ToList();
        }

        /// <summary>
        /// A tab name holds the song title (browsers add " - YouTube", "(3) ", " - 메모리 사용량: …" and so on). A very short
        /// title ("Up", "ON") would be found inside unrelated tab names, so it must start the name (after a "(3) " count) as a
        /// whole word.
        /// </summary>
        public static bool Contains(string text, string song)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(song)) return false;
            song = song.Trim();
            if (song.Length >= 4) return text.IndexOf(song, StringComparison.OrdinalIgnoreCase) >= 0;
            string name = System.Text.RegularExpressions.Regex.Replace(text.TrimStart(), @"^\(\d+\)\s*", "");
            return name.StartsWith(song, StringComparison.OrdinalIgnoreCase) &&
                   (name.Length == song.Length || !char.IsLetterOrDigit(name[song.Length]));
        }

        // Controls that never hold a tab strip: the search does not look inside them. Leaves (buttons, text boxes, …) and,
        // above all, the web page — a Document, or the Chromium views around it (MultiContentsView, ContentsWebView,
        // Chrome_RenderWidgetHostHWND). Stepping into the page would make the browser build the accessibility tree of the
        // site (extra CPU and memory in the browser), and it is by far the biggest part of the tree.
        private static readonly ControlType[] Leaves =
        {
            ControlType.Document, ControlType.Button, ControlType.Edit, ControlType.Text, ControlType.Image, ControlType.Hyperlink,
            ControlType.Separator, ControlType.MenuItem, ControlType.MenuBar, ControlType.CheckBox, ControlType.RadioButton,
            ControlType.SplitButton, ControlType.ComboBox, ControlType.ProgressBar, ControlType.ScrollBar, ControlType.Slider,
            ControlType.StatusBar, ControlType.ToolTip, ControlType.TitleBar
        };

        /// <summary>Whether the tab search looks inside a control (see <see cref="Leaves"/> and the web-page views).</summary>
        public static bool ShouldDescend(ControlType type, string className)
        {
            if (type == null || Array.IndexOf(Leaves, type) >= 0) return false;
            string cls = className ?? "";
            return cls.IndexOf("Contents", StringComparison.OrdinalIgnoreCase) < 0 && cls.IndexOf("WebView", StringComparison.OrdinalIgnoreCase) < 0 &&
                   cls.IndexOf("RenderWidgetHost", StringComparison.OrdinalIgnoreCase) < 0;
        }

        // Walks the browser's own UI (never the web page) breadth-first for tab items and selects the one showing the song.
        // Each step fetches type, class and name in one cached cross-process call. Chrome's tab strip sits about 8 levels down
        // (…BrowserView › TabStrip › TabContainerImpl › TabItem); with the pruning that is ~40 elements and well under 0.2 s.
        private static bool SelectTab(IntPtr window, string song)
        {
            try
            {
                var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.Full, TreeScope = TreeScope.Element };
                cache.Add(AutomationElement.ControlTypeProperty);
                cache.Add(AutomationElement.ClassNameProperty);
                cache.Add(AutomationElement.NameProperty);
                var walker = TreeWalker.ControlViewWalker;
                var queue = new Queue<KeyValuePair<AutomationElement, int>>();
                queue.Enqueue(new KeyValuePair<AutomationElement, int>(AutomationElement.FromHandle(window), 0));
                int visited = 0;
                var watch = Stopwatch.StartNew();
                while (queue.Count > 0 && visited < 600 && watch.ElapsedMilliseconds < 1500)
                {
                    var pair = queue.Dequeue();
                    for (var child = walker.GetFirstChild(pair.Key, cache); child != null; child = walker.GetNextSibling(child, cache))
                    {
                        visited++;
                        var type = child.Cached.ControlType;
                        if (type == ControlType.TabItem)
                        {
                            if (!Contains(child.Cached.Name, song)) continue;
                            if (child.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object selection)) ((SelectionItemPattern)selection).Select();
                            else if (child.TryGetCurrentPattern(InvokePattern.Pattern, out object invoke)) ((InvokePattern)invoke).Invoke();
                            else continue;
                            return true;
                        }
                        if (pair.Value < 12 && ShouldDescend(type, child.Cached.ClassName))
                            queue.Enqueue(new KeyValuePair<AutomationElement, int>(child, pair.Value + 1));
                    }
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException || ex is InvalidOperationException || ex is COMException || ex is ArgumentException)
            {
                // the window changed or closed during the search: fall back to just bringing it forward
            }
            return false;
        }

        private struct AppWindow { public IntPtr Handle; public string Title; }

        // Visible top-level windows with a title, of the given processes, front-most first.
        private static List<AppWindow> AppWindows(List<string> processNames)
        {
            var ids = new HashSet<int>();
            foreach (string name in processNames)
                foreach (var process in Process.GetProcessesByName(name))
                    using (process) ids.Add(process.Id);
            var result = new List<AppWindow>();
            if (ids.Count == 0) return result;
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4) != IntPtr.Zero) return true; // owned windows (popups) skipped
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (!ids.Contains((int)pid)) return true;
                var text = new StringBuilder(512);
                GetWindowText(hwnd, text, text.Capacity);
                if (text.Length > 0) result.Add(new AppWindow { Handle = hwnd, Title = text.ToString() });
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private static bool BringToFront(IntPtr hwnd)
        {
            if (!IsWindow(hwnd)) return false;
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE
            return SetForegroundWindow(hwnd);
        }

        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    }
}
