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
        // ---- The pets' web view: start it, and bring it back when it dies or stops drawing ----
        // Pets live only inside the web page; if its renderer / browser process exits (sleep, GPU reset, runtime update,
        // memory pressure) or the composition capture stalls, the calendar stays but the pets vanish. Recovery runs on the
        // view's own ProcessFailed event and on display / power / session / show events, plus a quiet look every 2 minutes
        // while the window shows (a stall that came with no event at all; see WatchPetsAsync).
        private System.Windows.Threading.DispatcherTimer recoveryTimer, healthTimer;
        private bool recoveryRecreate;              // the pending recovery must build a new view (browser process gone)
        private string recoveryReason;
        private readonly System.Collections.Generic.List<DateTime> recoveries = new System.Collections.Generic.List<DateTime>();
        private bool viewReady;                     // CoreWebView2 initialised on the current CharacterView
        private bool characterDisplayChanging;
        private System.Windows.Threading.DispatcherTimer displayResumeTimer;
        internal Action<bool, string> recoveryOverride = null;   // tests: (recreate, reason) instead of reloading a real view
        internal Func<System.Threading.Tasks.Task<bool>> healthCheckOverride = null; // tests: the view's answer to the health check
        internal Action<string> pagePostOverride = null;         // tests: messages that would go to the page
        internal Func<bool?> petPixelsOverride = null;           // tests: what the pixel check would see (null = could not tell)
        private System.Windows.Threading.DispatcherTimer pixelTimer;
        private System.Windows.Threading.DispatcherTimer petWatchTimer;   // the quiet look every PetWatchInterval (shown only)
        internal static readonly TimeSpan PetWatchDefault = TimeSpan.FromMinutes(2);
        internal static TimeSpan PetWatchInterval = PetWatchDefault;      // checks stretch it: no look in the middle of theirs
        private bool pixelConfirming;                     // the scheduled pixel check is the second look after an empty one
        private DateTime pageReadyAt;                     // when the current page said "ready" (MinValue: loading)
        // When the current page was asked for (MinValue: none yet). A page that never says "ready" (navigation failed, a
        // script error before it, the message lost) still answers the health check, so it is reloaded after NeverReadyAfter.
        private DateTime navigatedAt = DateTime.MinValue;
        internal static TimeSpan NeverReadyAfter = TimeSpan.FromSeconds(15);
        private bool PageNeverReady => pageReadyAt == DateTime.MinValue && navigatedAt != DateTime.MinValue && DateTime.Now - navigatedAt > NeverReadyAfter;
        // A view rebuilt because it looked empty is looked at again once its new page is ready. If that fresh view looks
        // empty too, "empty" means nothing for that layout (a black pet, a pet whose part outside the calendar is only its
        // transparent margin): it is not rebuilt again for it — no rebuild on every reopen — until it shows or pets move.
        private bool pixelVerifyAfterRebuild;
        private readonly System.Collections.Generic.HashSet<string> pixelDistrust = new System.Collections.Generic.HashSet<string>();

        private async System.Threading.Tasks.Task InitCharacterViewAsync()
        {
            if (closed || characterDisplayChanging) return;
            viewReady = false;
            var view = CharacterView;
            try
            {
                view.DefaultBackgroundColor = System.Drawing.Color.Transparent;
                await EmbeddedBrowser.InitializeAsync(view);
                if (closed || view != CharacterView) return;
                view.CoreWebView2.WebMessageReceived += (sender, args) =>
                {
                    // A page message must never take the app down: odd values are ignored, a view that just died too.
                    try { OnPageMessage(view, args.Source, args.WebMessageAsJson); }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException || ex is ArgumentException)
                    { PetLog.Write("page-message", ex.GetType().Name + ": " + ex.Message); }
                };
                view.CoreWebView2.NewWindowRequested += (sender, args) => args.Handled = true;
                view.CoreWebView2.ProcessFailed += (sender, args) => { if (view == CharacterView) HandleCharacterProcessFailed(args.ProcessFailedKind); };
                EnsureViewClickThrough(); // observe native creation/title changes before navigation creates the surface
                viewReady = true;
                ReloadCharacter();
            }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex) || ex is System.Runtime.InteropServices.COMException ||
                                       ex is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException || ex is ObjectDisposedException)
            { if (!closed && view == CharacterView && !characterDisplayChanging) CharacterViewStartFailed(ex); }
        }

        internal void CharacterViewStartFailed(Exception ex)
        {
            if (closed) return;
            ShowCharacterError("캐릭터를 표시하려면 WebView2 설치를 확인해 주세요.");
            PetLog.Write("init-failed", ex.GetType().Name + ": " + ex.Message);
            // A start that failed once (the runtime busy updating, a browser process that died while starting) is tried
            // again with a new view — at most 3 times a minute, then once a minute.
            RequestCharacterRecovery(true, "init-failed");
        }

        private void OnPageMessage(Microsoft.Web.WebView2.Wpf.WebView2CompositionControl view, string source, string json)
        {
            if (closed || view != CharacterView || source == null || !source.StartsWith(EmbeddedBrowser.Origin, StringComparison.Ordinal)) return;
            JObject message;
            try { message = JObject.Parse(json); } catch (JsonException) { return; }
            string type = PageMessage.Text(message, "type");
            if (type == "ready")
            {
                pageReadyAt = DateTime.Now;
                EnsureViewClickThrough();
                // Once more when the page's title (what finds its window) has surely settled.
                var again = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                again.Tick += (s, e) => { again.Stop(); EnsureViewClickThrough(); };
                again.Start();
                string load = FreshCharacterPayload();
                if (load != null) view.CoreWebView2?.PostWebMessageAsJson(load);
                lastLayoutJson = null;
                SendPetLayout(); // + the layout as it is now
                AfterPageReady();
            }
            else if (type == "invalid")
                ShowPetImageError(PageMessage.Text(message, "reason"), Math.Max(0, PageMessage.Index(message, Math.Max(SlotCount, slotCharacters.Count))));
        }

        /// <summary>
        /// The stored load with the music state and each pet's action as they are now: a music start / stop or an action
        /// picked while the page was loading went to the old page and was lost, and the new page must not start from the
        /// values of when the reload began. Remembers the music state the page now has (SendMusicState sends only changes).
        /// </summary>
        internal string FreshCharacterPayload()
        {
            if (characterPayload == null) return null;
            JObject payload;
            try { payload = JObject.Parse(characterPayload); } catch (JsonException) { return characterPayload; }
            bool playing = MusicPlaying;
            payload["music"] = playing;
            int count = payload["characters"] is JArray list ? list.Count : 0;
            payload["state"] = SlotAnimation(0);
            payload["states"] = new JArray(Enumerable.Range(0, count).Select(i => (object)SlotAnimation(i)).ToArray());
            payload["flips"] = new JArray(Enumerable.Range(0, count).Select(i => (object)PetFlipped(i)).ToArray()); // 좌우 반전 as it is now
            musicSent = musicPlaying == null ? (bool?)null : playing;
            characterPayload = payload.ToString(Formatting.None);
            return characterPayload;
        }

        internal void HandleCharacterProcessFailed(Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind kind)
        {
            switch (kind)
            {
                case Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.BrowserProcessExited:
                    RequestCharacterRecovery(true, "browser-exited"); break;
                case Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.RenderProcessExited:
                case Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                case Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                case Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.GpuProcessExited:
                    RequestCharacterRecovery(false, kind.ToString()); break;
                default:
                    PetLog.Write("process-failed", kind.ToString()); break; // utility processes etc.: the page keeps drawing
            }
        }

        /// <summary>Reload the page (or build a new view) about a second from now; at most 3 times a minute.</summary>
        internal void RequestCharacterRecovery(bool recreate, string reason)
        {
            if (closed || characterDisplayChanging) return;
            PetLog.Write(recreate ? "recover-view" : "recover-page", reason);
            recoveryRecreate |= recreate;
            recoveryReason = reason;
            if (recoveryTimer == null)
            {
                recoveryTimer = new System.Windows.Threading.DispatcherTimer();
                recoveryTimer.Tick += (s, e) => { recoveryTimer.Stop(); RunPendingRecovery(); };
            }
            // Already on its way: keep that time and just merge what it has to do. Restarting the countdown here let failure
            // events that repeat (a hung renderer reports "unresponsive" every few seconds, every display change runs a
            // health check) postpone the recovery for good — with the one-minute backoff the pets never came back.
            if (recoveryTimer.IsEnabled) return;
            recoveries.RemoveAll(t => DateTime.Now - t > TimeSpan.FromMinutes(1));
            // Backoff: a view that keeps dying is tried again after a minute instead of in a tight loop.
            recoveryTimer.Interval = recoveries.Count >= 3 ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(1);
            recoveryDue = DateTime.Now + recoveryTimer.Interval;
            recoveryTimer.Start();
        }

        private DateTime recoveryDue; // when the pending recovery runs (tests)

        internal void RunPendingRecovery()
        {
            if (closed || characterDisplayChanging) return;
            recoveryTimer?.Stop();
            bool recreate = recoveryRecreate;
            string reason = recoveryReason;
            recoveryRecreate = false;
            recoveries.Add(DateTime.Now);
            if (recoveryOverride != null) { recoveryOverride(recreate, reason); return; }
            if (recreate || !viewReady) { var _ = RecreateCharacterViewAsync(); return; }
            try { ReloadCharacter(); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException || ex is ObjectDisposedException)
            { var _ = RecreateCharacterViewAsync(); } // the view itself is gone: build a new one
        }

        // A dead browser process cannot be reused: drop the view and put a fresh one in the same place.
        private async System.Threading.Tasks.Task RecreateCharacterViewAsync()
        {
            if (closed || characterDisplayChanging) return;
            EmbeddedBrowser.ResetEnvironment();
            ReplaceCharacterView();
            await InitCharacterViewAsync();
        }

        // A composition view retains a D3D surface on its old display adapter. Dispose it before the display/DPI
        // transition is rendered, so WPF cannot submit that stale surface to an adapter that disappeared.
        internal void PrepareCharacterDisplayChange()
        {
            if (closed) return;
            ResetPointerForScreenChange();
            if (characterDisplayChanging) return;
            EnsureViewClickThrough(); // keep the retiring surface harmless while the runtime disposes it
            characterDisplayChanging = true;
            recoveryTimer?.Stop();
            healthTimer?.Stop();
            pixelTimer?.Stop();
            petWatchTimer?.Stop();
            recoveryRecreate = false;
            ReplaceCharacterView();
            PetLog.Write("display-changing", "graphics surface released");
        }

        private void ScheduleCharacterDisplayResume(int milliseconds = 500)
        {
            if (closed || !characterDisplayChanging) return;
            if (displayResumeTimer == null)
            {
                displayResumeTimer = new System.Windows.Threading.DispatcherTimer();
                displayResumeTimer.Tick += (s, e) => ResumeCharacterAfterDisplayChange();
            }
            displayResumeTimer.Stop();
            displayResumeTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            displayResumeTimer.Start();
        }

        internal void ResumeCharacterAfterDisplayChange()
        {
            displayResumeTimer?.Stop();
            if (closed || !characterDisplayChanging) return;
            characterDisplayChanging = false;
            RecoverScreenInputLayout();
            PetLog.Write("display-changed", "graphics surface restarting");
            if (recoveryOverride != null) { recoveryOverride(true, "display-change"); return; }
            var _ = InitCharacterViewAsync();
            UpdatePetWatch();
        }

        private void ReplaceCharacterView()
        {
            viewReady = false;
            var old = CharacterView;
            int index = CharacterHost.Children.IndexOf(old);
            var fresh = new Microsoft.Web.WebView2.Wpf.WebView2CompositionControl
            {
                Width = old.Width, Height = old.Height, IsHitTestVisible = false, Background = System.Windows.Media.Brushes.Transparent,
                AllowExternalDrop = false
            };
            System.Windows.Automation.AutomationProperties.SetName(fresh, System.Windows.Automation.AutomationProperties.GetName(old));
            Canvas.SetLeft(fresh, Canvas.GetLeft(old));
            Canvas.SetTop(fresh, Canvas.GetTop(old));
            CharacterHost.Children.Remove(old);
            CharacterHost.Children.Insert(Math.Max(0, index), fresh); // same z-order: under the hit boxes and the error text
            CharacterView = fresh;
            pageReadyAt = DateTime.MinValue;
            navigatedAt = DateTime.MinValue; // the new view asks for its page itself (ReloadCharacter)
            // FindName("CharacterView") must find the new view too, not the disposed one.
            try { UnregisterName("CharacterView"); } catch (ArgumentException) { }
            RegisterName("CharacterView", fresh);
            try { old.Dispose(); } catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException) { }
            lastLayoutJson = null;
        }

        // Display / DPI change, resume, unlock, shown again: ask the page for a sign of life a second later. No answer →
        // reload it; an answer → resend the layout and make it repaint (restarts a stalled capture).
        internal void ScheduleHealthCheck(string reason)
        {
            if (closed || characterDisplayChanging) return;
            if (healthTimer == null)
            {
                healthTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                healthTimer.Tick += async (s, e) =>
                {
                    healthTimer.Stop();
                    // An async void timer handler: anything thrown here would take the whole app down (there is no
                    // unhandled-exception handler), for a check that only exists to help. Log it and carry on.
                    try { await RunHealthCheckAsync((string)healthTimer.Tag); }
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { PetLog.Write("check-failed", ex.GetType().Name + ": " + ex.Message); }
                };
            }
            healthTimer.Tag = reason;
            healthTimer.Stop();
            healthTimer.Start();
        }

        internal async System.Threading.Tasks.Task RunHealthCheckAsync(string reason)
        {
            if (closed || characterDisplayChanging || !IsVisible && healthCheckOverride == null) return; // hidden: checked again when shown
            RecoverScreenInputLayout();
            EnsureViewClickThrough();
            if (recoveryTimer != null && recoveryTimer.IsEnabled) return; // a reload / new view is already on its way
            var observedView = CharacterView;
            bool? alive = await PageAnswersAsync();
            if (alive == null || closed || characterDisplayChanging || observedView != CharacterView) return; // still starting, or a replaced view answered late
            if (alive == false) { RequestCharacterRecovery(false, "no-answer-" + reason); return; }
            if (PageNeverReady) { RequestCharacterRecovery(false, "never-ready-" + reason); return; }
            RepaintPets();
            SchedulePetPixelCheck(reason, false); // the page answered; make sure the window really shows it
        }

        // The page's sign of life: it runs a script within 3 s. Null: nothing to ask yet (still starting, or recovering).
        private async System.Threading.Tasks.Task<bool?> PageAnswersAsync()
        {
            if (healthCheckOverride != null) return await healthCheckOverride();
            if (!viewReady || LiveCore == null) return null;
            try
            {
                var answer = CharacterView.ExecuteScriptAsync("1");
                return await System.Threading.Tasks.Task.WhenAny(answer, System.Threading.Tasks.Task.Delay(3000)) == answer && !answer.IsFaulted;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException || ex is ObjectDisposedException)
            { return false; }
        }

        // The quiet watch: runs while the window shows (stopped while hidden or closed). A view can also stop drawing with no
        // Windows event at all (a GPU hiccup, a remote-desktop tool switching its capture); every PetWatchInterval it asks the
        // page for a sign of life and looks at the pets' pixels. Healthy → nothing is repainted and nothing is logged; no
        // answer → reload; empty → repaint, look again, then a new view — the same steps as after a display change.
        private void UpdatePetWatch()
        {
            if (closed || characterDisplayChanging || !IsVisible) { petWatchTimer?.Stop(); return; }
            if (petWatchTimer == null)
            {
                petWatchTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background);
                petWatchTimer.Tick += async (s, e) =>
                {
                    try { await WatchPetsAsync(); } // async void: never let it crash the app
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { PetLog.Write("check-failed", ex.GetType().Name + ": " + ex.Message); }
                };
            }
            petWatchTimer.Interval = PetWatchInterval;
            if (!petWatchTimer.IsEnabled) petWatchTimer.Start();
        }

        internal async System.Threading.Tasks.Task WatchPetsAsync()
        {
            if (closed || characterDisplayChanging || !IsVisible || !PetsShown || placingPets || weekFlip != null) return;
            // A check or recovery already on its way (an event came just now) does the job.
            if (recoveryTimer != null && recoveryTimer.IsEnabled || healthTimer != null && healthTimer.IsEnabled ||
                pixelTimer != null && pixelTimer.IsEnabled) return;
            var observedView = CharacterView;
            bool? alive = await PageAnswersAsync();
            if (alive == null || closed || characterDisplayChanging || observedView != CharacterView) return;
            if (alive == false) { RequestCharacterRecovery(false, "no-answer-watch"); return; }
            if (PageNeverReady) { RequestCharacterRecovery(false, "never-ready-watch"); return; }
            await CheckPetPixelsAsync("watch", false);
        }

        // After a display change (Parsec adding or removing its virtual monitor, a monitor waking up, a DPI change), resume or
        // unlock, the page can answer while the view's capture stays empty: the pets are gone although nothing failed. So a
        // moment after the repaint, look at what the window really shows where the pets stand. Empty → repaint and look once
        // more; still empty → build a new view (a stalled capture needs a new controller; a page reload may not restart it).
        // Runs after those events and from the quiet watch (WatchPetsAsync).
        private void SchedulePetPixelCheck(string reason, bool confirming)
        {
            if (closed || characterDisplayChanging) return;
            if (pixelTimer == null)
            {
                pixelTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                pixelTimer.Tick += async (s, e) =>
                {
                    pixelTimer.Stop();
                    bool second = pixelConfirming;
                    pixelConfirming = false;
                    try { await CheckPetPixelsAsync((string)pixelTimer.Tag, second); } // async void: never let it crash the app
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { PetLog.Write("check-failed", ex.GetType().Name + ": " + ex.Message); }
                };
            }
            pixelTimer.Tag = reason;
            pixelConfirming = confirming;
            pixelTimer.Stop();
            pixelTimer.Start();
        }

        /// <summary>What the window shows where the pets stand: true drawn, false empty, null skipped / could not tell.</summary>
        internal async System.Threading.Tasks.Task<bool?> CheckPetPixelsAsync(string reason, bool confirming)
        {
            // A pet whose image failed (its error shows) is left out below; the other pets are still looked at.
            if (closed || characterDisplayChanging || !IsVisible || !PetsShown || placingPets) return null;
            if (recoveryTimer != null && recoveryTimer.IsEnabled) return null; // a reload / new view is already on its way
            // A page still loading (or loaded a moment ago) may not have drawn its pets yet: that is not a stalled view.
            if (pageReadyAt == DateTime.MinValue || DateTime.Now - pageReadyAt < TimeSpan.FromSeconds(2)) return null;
            var areas = PetAreasOutsideBoard();
            string key = PixelAreasKey(areas);
            var observedView = CharacterView;
            bool? drawn = null;
            if (areas.Count > 0)
            {
                if (petPixelsOverride != null) drawn = petPixelsOverride();
                else
                {
                    IntPtr hwnd = new WindowInteropHelper(this).Handle;
                    var dpi = VisualTreeHelper.GetDpi(this);
                    Int32Rect Device(Rect r) => new Int32Rect(
                        (int)Math.Round(r.X * dpi.DpiScaleX), (int)Math.Round(r.Y * dpi.DpiScaleY),
                        (int)Math.Round(r.Width * dpi.DpiScaleX), (int)Math.Round(r.Height * dpi.DpiScaleY));
                    var device = areas.Select(Device).ToList();
                    // The calendar is WPF's own drawing: if even it reads empty, the capture shows nothing at all (monitors
                    // asleep, DWM not composing this window) and says nothing about the pets. The TODO window's pets have
                    // no calendar to compare with.
                    var reference = companion ? null : new System.Collections.Generic.List<Int32Rect> { Device(BoardRect) };
                    drawn = await System.Threading.Tasks.Task.Run(() => ReadPetPixels(hwnd, device, reference));
                    // Back on the window's thread whatever called this (the rest touches the view and the timers).
                    if (!Dispatcher.CheckAccess())
                    {
                        bool? seen = drawn;
                        return await Dispatcher.InvokeAsync(() => characterDisplayChanging || observedView != CharacterView
                            ? null : ConcludePixelCheck(reason, confirming, seen, key)).Task;
                    }
                }
            }
            return characterDisplayChanging || observedView != CharacterView ? null : ConcludePixelCheck(reason, confirming, drawn, key);
        }

        private bool? ConcludePixelCheck(string reason, bool confirming, bool? drawn, string key)
        {
            if (closed) return drawn;
            bool distrusted = drawn == false && key != null && pixelDistrust.Contains(key);
            // "shown" comes with every reopen from the tray, the watch every 2 minutes: not worth a log line while the pets show
            // (the watch's second look is logged either way: it tells whether a repaint brought them back).
            if (reason != "shown" && !(reason == "watch" && !confirming && drawn != false))
                PetLog.Write("check", reason + (confirming ? " again" : "") + " " + (drawn == true ? "drawn" : drawn == false ? "blank" : "unknown") +
                    (distrusted ? " (ignored: a rebuilt view looked the same here)" : ""));
            if (drawn == true && key != null) pixelDistrust.Remove(key);
            if (drawn != false || distrusted) return drawn;
            if (!confirming) { RepaintPets(); SchedulePetPixelCheck(reason, true); return drawn; } // a first frame may just be late
            if (reason == "rebuilt")
            {
                // The view was rebuilt because it looked empty, and the fresh one looks empty too: the pixels cannot tell
                // here (a black pet, only a transparent margin outside the calendar). No more rebuilds for this layout.
                if (key != null) { if (pixelDistrust.Count >= 20) pixelDistrust.Clear(); pixelDistrust.Add(key); }
                return drawn;
            }
            pixelVerifyAfterRebuild = true; // look at the new view once its page is ready (AfterPageReady)
            RequestCharacterRecovery(true, "blank-" + reason);
            return drawn;
        }

        /// <summary>
        /// The pets' view (WebView2 composition) keeps a see-through browser window of its own over the pets. Windows could
        /// raise it above this window (e.g. when the desktop was activated), and from then on it took every click on the pets
        /// and much of the calendar — the pets stayed dead until the window was dragged. Click-through, it never can.
        /// </summary>
        private BrowserWindowInputGuard browserInputGuard;
        private uint guardedBrowserProcessId;

        private void EnsureViewClickThrough()
        {
            if (closed) return;
            var core = LiveCore;
            if (core == null) { browserInputGuard?.Verify(); return; }
            uint pid;
            try { pid = core.BrowserProcessId; }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException) { return; }
            if (pid == 0) return;
            if (browserInputGuard == null || guardedBrowserProcessId != pid)
            {
                browserInputGuard?.Dispose();
                guardedBrowserProcessId = pid;
                browserInputGuard = new BrowserWindowInputGuard(pid, "character.html?mini", Dispatcher,
                    changed => PetLog.Write("view-input", "browser surface made click-through (" + changed + ")"));
            }
            else browserInputGuard.Verify();
        }

        /// <summary>Called when a new page says "ready": after a rebuild for looking empty, check that the new view shows.</summary>
        internal void AfterPageReady()
        {
            if (!pixelVerifyAfterRebuild) return;
            pixelVerifyAfterRebuild = false;
            ScheduleHealthCheck("rebuilt"); // health check → repaint → pixel look, with the ready grace already passed
        }

        private static string PixelAreasKey(System.Collections.Generic.IEnumerable<Rect> areas) =>
            string.Join(";", areas.Select(r => (int)Math.Round(r.X) + "," + (int)Math.Round(r.Y) + "," + (int)Math.Round(r.Width) + "," + (int)Math.Round(r.Height)));

        private Rect BoardRect => new Rect(Edge + petPads.Left, Edge + petPads.Top, BoardWidth, BoardHeight);

        // The pets' rectangles (window coordinates, DIPs) minus the board: the calendar and the music bar draw there
        // themselves, so their pixels would make an empty pet look drawn (a pet may overlap the calendar's edge). A pet
        // with less than a quarter of itself outside the calendar is left out: what sticks out is then mostly its
        // transparent margin, which would read "empty" even while the pet shows. A pet the page could not draw (its image
        // failed) or the app could not read is left out too: its empty area says nothing about the view.
        private System.Collections.Generic.List<Rect> PetAreasOutsideBoard()
        {
            var board = BoardRect;
            var result = new System.Collections.Generic.List<Rect>();
            var rects = PetRects();
            for (int i = 0; i < rects.Count; i++)
            {
                var pet = rects[i];
                if (pet.Width < 8 || pet.Height < 8 || undrawnPets.Contains(i)) continue;
                var outside = Subtract(pet, board).Where(part => part.Width >= 4 && part.Height >= 4).ToList();
                if (outside.Sum(part => part.Width * part.Height) >= 0.25 * pet.Width * pet.Height) result.AddRange(outside);
            }
            return result;
        }

        /// <summary>A rectangle minus another: the parts of <paramref name="a"/> above, below, left and right of the overlap.</summary>
        internal static System.Collections.Generic.List<Rect> Subtract(Rect a, Rect b)
        {
            var parts = new System.Collections.Generic.List<Rect>();
            var overlap = Rect.Intersect(a, b);
            if (overlap.IsEmpty || overlap.Width <= 0 || overlap.Height <= 0) { parts.Add(a); return parts; }
            if (overlap.Top > a.Top) parts.Add(new Rect(a.Left, a.Top, a.Width, overlap.Top - a.Top));
            if (overlap.Bottom < a.Bottom) parts.Add(new Rect(a.Left, overlap.Bottom, a.Width, a.Bottom - overlap.Bottom));
            if (overlap.Left > a.Left) parts.Add(new Rect(a.Left, overlap.Top, overlap.Left - a.Left, overlap.Height));
            if (overlap.Right < a.Right) parts.Add(new Rect(overlap.Right, overlap.Top, a.Right - overlap.Right, overlap.Height));
            return parts;
        }

        internal static bool? PetPixelsDrawn(IntPtr hwnd, System.Collections.Generic.IList<Int32Rect> areas) => ReadPetPixels(hwnd, areas, null);

        /// <summary>
        /// What the window shows in these areas (device pixels, window coordinates): true when at least 0.5 % of the sampled
        /// pixels are not pure black, false when they all are, null when it could not look. PrintWindow with
        /// PW_RENDERFULLCONTENT reads the window's own content even while other windows cover it; a layered window's
        /// transparent parts come out exactly black (0,0,0 — alpha is always 255, so it cannot tell), which is why any
        /// non-zero channel counts: a dark pet (outlines of 5,5,5, a 4 % white glow) must not read as empty. Only pure
        /// black cannot be told from nothing. <paramref name="reference"/>: areas that must show something for the look
        /// to count (the calendar); when they read empty too the capture itself is blank and the answer is null.
        /// Also null while DWM cloaks the window (another virtual desktop). Thread-agnostic: called off the UI thread.
        /// </summary>
        internal static bool? ReadPetPixels(IntPtr hwnd, System.Collections.Generic.IList<Int32Rect> areas, System.Collections.Generic.IList<Int32Rect> reference)
        {
            if (hwnd == IntPtr.Zero || areas == null || areas.Count == 0) return null;
            if (NativeMethods.IsCloaked(hwnd)) return null;
            if (!NativeMethods.TryGetWindowSize(hwnd, out int width, out int height) || width <= 0 || height <= 0 || (long)width * height > 16L * 1024 * 1024)
                return null;
            try
            {
                using (var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                    {
                        IntPtr dc = graphics.GetHdc();
                        bool printed;
                        try { printed = NativeMethods.PrintWindow(hwnd, dc, 2); }
                        finally { graphics.ReleaseHdc(dc); }
                        if (!printed) return null;
                    }
                    var bounds = new System.Drawing.Rectangle(0, 0, width, height);
                    var data = bitmap.LockBits(bounds, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    try
                    {
                        var row = new byte[Math.Abs(data.Stride)];
                        // Share of sampled pixels (every step-th row and column) with any colour; -1 when nothing was sampled.
                        double Lit(System.Collections.Generic.IList<Int32Rect> list, int step)
                        {
                            long seen = 0, lit = 0;
                            foreach (var area in list)
                            {
                                var part = System.Drawing.Rectangle.Intersect(new System.Drawing.Rectangle(area.X, area.Y, area.Width, area.Height), bounds);
                                for (int y = part.Top; y < part.Bottom; y += step)
                                {
                                    System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                                    for (int x = part.Left; x < part.Right; x += step)
                                    {
                                        int i = x * 4; // B, G, R, A
                                        seen++;
                                        if (row[i] != 0 || row[i + 1] != 0 || row[i + 2] != 0) lit++;
                                    }
                                }
                            }
                            return seen == 0 ? -1 : (double)lit / seen;
                        }
                        if (reference != null && reference.Count > 0)
                        {
                            double calendar = Lit(reference, 6);
                            if (calendar < 0.005) return null; // not even the calendar shows: the capture itself is empty
                        }
                        double pets = Lit(areas, 2);
                        if (pets < 0) return null;
                        return pets >= 0.005;
                    }
                    finally { bitmap.UnlockBits(data); }
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception ||
                                       ex is System.Runtime.InteropServices.ExternalException || ex is OutOfMemoryException)
            { return null; } // GDI+ reports a bitmap it cannot make as OutOfMemory: just "could not tell"
        }

        // Resend the layout and ask the page to redraw every pet; nudge the view's size so the capture restarts.
        private bool repaintQueued;

        // One RepaintPets after the layout in progress (several resizes in a row give one).
        private void RepaintPetsSoon()
        {
            if (repaintQueued || closed || !IsLoaded) return;
            repaintQueued = true;
            Dispatcher.BeginInvoke(new Action(() => { repaintQueued = false; if (!closed && IsVisible) RepaintPets(); }),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        internal void RepaintPets()
        {
            lastLayoutJson = null;
            SendPetLayout();
            PostToPage(JsonConvert.SerializeObject(new { action = "repaint" }));
            if (pagePostOverride != null || CharacterView.Width <= 1) return;
            double width = CharacterView.Width;
            CharacterView.Width = width + 1;
            Dispatcher.BeginInvoke(new Action(() => { if (!closed && Math.Abs(CharacterView.Width - (width + 1)) < 0.01) CharacterView.Width = width; }),
                System.Windows.Threading.DispatcherPriority.Render);
        }

        /// <summary>
        /// The view's CoreWebView2, or null while it is starting — and also once its browser process has died: from then on
        /// even reading CharacterView.CoreWebView2 throws ("The WebView control is no longer valid because the browser
        /// process crashed"), and layout / music / animation updates run all the time until the new view is built.
        /// </summary>
        private Microsoft.Web.WebView2.Core.CoreWebView2 LiveCore
        {
            get
            {
                try { return CharacterView.CoreWebView2; }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException || ex is System.Runtime.InteropServices.COMException)
                { return null; } // ProcessFailed has already asked for a new view
            }
        }

        private void PostToPage(string json)
        {
            if (pagePostOverride != null) { pagePostOverride(json); return; }
            var core = closed ? null : LiveCore;
            if (core == null) return;
            try { core.PostWebMessageAsJson(json); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.Runtime.InteropServices.COMException || ex is ObjectDisposedException) { }
        }

        // A pet's sharper sheet was made in the background (SpriteSharpener, on its own thread): the page switches that pet
        // over in place — no reload, its action and frame go on — and the stored load carries it for a reload of the page.
        private void OnSharpSheetReady(string imagePath, string address) =>
            Dispatcher.BeginInvoke(new Action(() => UseSharpSheet(imagePath, address)));

        internal void UseSharpSheet(string imagePath, string address)
        {
            if (closed || characterPayload == null) return;
            JObject payload;
            try { payload = JObject.Parse(characterPayload); } catch (JsonException) { return; }
            var list = payload["characters"] as JArray;
            bool any = false;
            for (int i = 0; list != null && i < slotCharacters.Count && i < list.Count; i++)
            {
                if (unreadablePets.Contains(i) || emptyPets.Contains(i) || !SameFile(slotCharacters[i].ImagePath, imagePath) || !(list[i] is JObject entry)) continue;
                entry["sharp"] = address;
                any = true;
                // A page still loading gets it with the load (the stored payload); a message now would reach the old page.
                if (pageReadyAt != DateTime.MinValue) PostToPage(JsonConvert.SerializeObject(new { action = "sheet", index = i, image = address }));
            }
            if (any) characterPayload = payload.ToString(Formatting.None);
        }

        private static bool SameFile(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (CharacterCatalog.IsImportError(ex)) { return false; }
        }

        private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume)
                Dispatcher.BeginInvoke(new Action(() => { PetLog.Write("resume"); ResetPointerForScreenChange(); EnsureViewClickThrough(); ScheduleHealthCheck("resume"); ScreensMayHaveChanged("resume"); }));
        }

        private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
        {
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock || e.Reason == Microsoft.Win32.SessionSwitchReason.ConsoleConnect ||
                e.Reason == Microsoft.Win32.SessionSwitchReason.RemoteConnect)
                Dispatcher.BeginInvoke(new Action(() => { ResetPointerForScreenChange(); EnsureViewClickThrough(); ScheduleHealthCheck("session-" + e.Reason); ScreensMayHaveChanged("session-" + e.Reason); }));
        }

    }
}
