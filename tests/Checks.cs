using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static string root;
        private static int passed, failed;

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            if (args.Length != 1) { Console.Error.WriteLine("Usage: ScheduleWidget.Checks.exe <new-data-directory>"); return 2; }
            root = Path.GetFullPath(args[0]);
            if (Directory.Exists(root)) { Console.Error.WriteLine("Checks require a new, empty data directory."); return 2; }
            Directory.CreateDirectory(root);
            // All filesystem-facing services use this run's folders. App.OnStartup is never called.
            CharacterCatalog.PetRoot = Path.Combine(root, "Pet");
            CodexPets.RootOverride = Path.Combine(root, "CodexPets");
            CodexPets.InstallFinder = () => null;
            ErrorLog.Root = PetLog.Root = root;
            var browserType = typeof(App).Assembly.GetType("ScheduleWidget.EmbeddedBrowser", true);
            browserType.GetField("UserDataRoot", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, root);
            typeof(UpdateClient).GetProperty("GitHubRepositoryOverride", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, "");

            Run("Legacy data migration and distinct schedule IDs", LegacyMigration);
            Run("Extended settings survive save and reload", ExtendedRoundTrip);
            Run("Unchanged saves preserve the previous backup", UnchangedSave);
            Run("Corrupt primary recovers the previous backup", CorruptRecovery);
            Run("Locked data never falls back to stale schedules", LockedData);
            Run("Multi-day schedules and completed D-day", ScheduleRanges);
            Run("Timed reminders respect completion and deadlines", Reminders);
            Run("YouTube links reject unrelated and malformed URLs", YouTubeLinks);
            Run("Google sync only deletes owned unshared events", GoogleDeletion);
            Run("Google sync resolves local and remote edits", GoogleEdits);
            Run("Google account display works without Calendar metadata permission", GoogleAccountEmail);
            Run("Drive sync only deletes explicitly removed pets", DriveDeletion);
            Run("Default pet manifests and packaged assets load", PackagedAssets);
            Run("Updates reject a modified signed manifest", SignedUpdates);
            Run("Global shortcuts reject unmodified typing keys", Shortcuts);
            Run("Automatic media policy follows active apps and preserves manual choices", AutomaticMediaSelection);
            Run("Fatal rendering errors are distinguished from recoverable COM errors", RenderingFailureClassification);
            Run("WPF resources and six window layouts construct", WindowResources);

            Console.WriteLine("{0} passed, {1} failed.", passed, failed);
            return failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action check)
        {
            try { check(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + Environment.NewLine + ex); }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name);
        }

        private static DataManager Store(string name) => new DataManager(Path.Combine(root, name), null);
        private static ScheduleItem Item(string title = "Sample") => new ScheduleItem { Title = title, Period = "2026-10-08" };
        private static AppData Data(string title = "Sample") => new AppData { Schedules = new List<ScheduleItem> { Item(title) } };

        private static void LegacyMigration()
        {
            string legacy = Path.Combine(root, "legacy.json");
            File.WriteAllText(legacy, "{\"Schedules\":[{\"Title\":\"Same\",\"Period\":\"2026-10-08\"},{\"Title\":\"Same\",\"Period\":\"2026-10-08\"}]}");
            var store = new DataManager(Path.Combine(root, "migrated"), legacy);
            var loaded = store.LoadData().Data;
            Require(loaded.Schedules.Count == 2, "Migration lost schedules.");
            Require(loaded.Schedules.All(s => s.Id != Guid.Empty) && loaded.Schedules.Select(s => s.Id).Distinct().Count() == 2, "Schedule IDs are not unique.");
            Require(!File.Exists(legacy) && File.Exists(store.DataFilePath), "Legacy file was not migrated.");
            Require(store.LoadData().Data.Schedules.Select(s => s.Id).SequenceEqual(loaded.Schedules.Select(s => s.Id)), "IDs changed on reload.");
        }

        private static void ExtendedRoundTrip()
        {
            var data = Data();
            var item = data.Schedules[0];
            item.Time = "09:05"; item.EndPeriod = "2026-10-10"; item.Color = "#336699"; item.IsCompleted = true;
            item.GoogleEventId = "sampleevent";
            data.MiniDayCount = 4; data.MiniMode = true; data.AlwaysOnTop = true;
            data.GoogleCalendar.Enabled = true;
            data.Music.Playlists.Add(new MusicPlaylist { Name = "Saved", Tracks = new List<MusicTrack> { new MusicTrack { Title = "Track", Source = "https://www.youtube.com/watch?v=dQw4w9WgXcQ" } } });
            var store = Store("roundtrip");
            store.SaveData(data);
            var loaded = store.LoadData().Data;
            var saved = loaded.Schedules.Single();
            Require(saved.Id == item.Id && saved.Time == "09:05" && saved.EndPeriod == "2026-10-10" && saved.IsCompleted && saved.Color == "#336699", "Schedule fields were lost.");
            Require(saved.GoogleEventId == "sampleevent" && loaded.GoogleCalendar.Enabled, "Google link was lost.");
            Require(loaded.MiniMode && loaded.MiniDayCount == 4 && loaded.AlwaysOnTop, "Mini settings were lost.");
            Require(loaded.Music.Playlists.Single().Tracks.Single().IsYouTube, "Playlist was lost.");
        }

        private static void UnchangedSave()
        {
            var store = Store("unchanged");
            store.SaveData(Data("Earlier"));
            var data = Data("Latest");
            store.SaveData(data);
            string backup = File.ReadAllText(store.BackupFilePath);
            store.SaveData(data);
            Require(File.ReadAllText(store.BackupFilePath) == backup, "A repeated save rotated away the backup.");
            Require(JsonConvert.DeserializeObject<AppData>(backup).Schedules.Single().Title == "Earlier", "Backup contains the wrong version.");
        }

        private static void CorruptRecovery()
        {
            var store = Store("corrupt");
            store.SaveData(Data("Earlier")); store.SaveData(Data("Latest"));
            File.WriteAllText(store.DataFilePath, "{broken");
            var loaded = store.LoadData();
            Require(loaded.Data.Schedules.Single().Title == "Earlier" && !string.IsNullOrEmpty(loaded.WarningMessage), "Backup recovery was not reported.");
            Require(Directory.GetFiles(Path.GetDirectoryName(store.DataFilePath), "*corrupt*").Length > 0, "Corrupt file was not preserved.");
        }

        private static void LockedData()
        {
            var store = Store("locked");
            store.SaveData(Data("Earlier")); store.SaveData(Data("Latest"));
            string before = File.ReadAllText(store.DataFilePath);
            store.BusyWait = TimeSpan.Zero;
            using (File.Open(store.DataFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Throws<DataStorageException>(() => store.LoadData());
            Require(File.ReadAllText(store.DataFilePath) == before, "Locked data was replaced.");
            Require(Directory.GetFiles(Path.GetDirectoryName(store.DataFilePath), "*corrupt*").Length == 0, "Locked data was treated as corrupt.");
        }

        private static void ScheduleRanges()
        {
            var day = DateTime.Today;
            var item = new ScheduleItem { Title = "Range", Period = day.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), EndPeriod = day.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            Require(item.Covers(day) && item.DDay == "D-day" && !item.Covers(day.AddDays(2)), "Range coverage or D-day is wrong.");
            item.IsCompleted = true;
            Require(item.DDay == "완료", "Completed label is missing.");
            Require(!ScheduleItem.TryRange(day, day.AddDays(-1), out _, out _), "Reversed range accepted.");
            Require(!ScheduleItem.TryRange(day, day.AddDays(367), out _, out _), "Overlong range accepted.");
        }

        private static void Reminders()
        {
            Require(FeatureRules.TryScheduleTime("9:05", out string normalized) && normalized == "09:05", "Time normalization failed.");
            Require(!FeatureRules.TryScheduleTime("24:00", out _) && !FeatureRules.TryScheduleTime("12:60", out _), "Invalid time accepted.");
            var item = Item(); item.Time = "09:05";
            var settings = new ReminderSettings { Enabled = true, DaysBefore = 1 };
            Require(!FeatureRules.IsReminderDue(item, settings, new DateTime(2026, 10, 7, 9, 4, 0)), "Reminder fired early.");
            Require(FeatureRules.IsReminderDue(item, settings, new DateTime(2026, 10, 7, 9, 5, 0)), "Reminder did not become due.");
            item.IsCompleted = true;
            Require(!FeatureRules.IsReminderDue(item, settings, new DateTime(2026, 10, 7, 10, 0, 0)), "Completed schedule generated a reminder.");
        }

        private static void YouTubeLinks()
        {
            Require(FeatureRules.TryYouTube("https://youtu.be/dQw4w9WgXcQ", out string video, out _) && video == "dQw4w9WgXcQ", "Short video URL failed.");
            Require(FeatureRules.TryYouTube("https://www.youtube.com/playlist?list=PL1234567890", out _, out string playlist) && playlist == "PL1234567890", "Playlist URL failed.");
            Require(!FeatureRules.TryYouTube("https://youtube.com.example.org/watch?v=dQw4w9WgXcQ", out _, out _), "Unrelated domain accepted.");
            Require(!FeatureRules.TryYouTube("https://youtube.com/watch?v=short", out _, out _), "Malformed video ID accepted.");
        }

        private static void GoogleDeletion()
        {
            var remote = new List<GoogleEvent> {
                new GoogleEvent { Id = "owned", Period = "2026-10-08" },
                new GoogleEvent { Id = "shared", Period = "2026-10-08", Shared = true },
                new GoogleEvent { Id = "external", Period = "2026-10-08" }
            };
            var plan = GoogleCalendarSync.Plan(new List<ScheduleItem>(), remote, new[] { "owned", "shared", "external" }, new DateTime(2026, 10, 4), new[] { "owned", "shared" });
            Require(plan.DeleteRemote.SequenceEqual(new[] { "owned" }), "Sync would delete an unowned or shared event.");
            Require(plan.Unlink.ContainsKey("shared") && plan.Unlink.ContainsKey("external") && plan.AddLocal.Count == 0, "Removed external events would reappear.");
        }

        private static void GoogleEdits()
        {
            var local = Item("Before"); local.GoogleEventId = "linked";
            local.GoogleSyncedHash = GoogleCalendarSync.Hash(local);
            var remote = new GoogleEvent { Id = "linked", Title = "Remote edit", Period = local.Period };
            var plan = GoogleCalendarSync.Plan(new[] { local }, new[] { remote }, new[] { "linked" }, new DateTime(2026, 10, 4));
            Require(plan.UpdateLocal.Count == 1 && plan.Patch.Count == 0, "Remote edit was not brought in.");
            local.Title = "Local edit";
            plan = GoogleCalendarSync.Plan(new[] { local }, new[] { remote }, new[] { "linked" }, new DateTime(2026, 10, 4));
            Require(plan.Patch.Count == 1 && plan.UpdateLocal.Count == 0, "Local edit lost conflict resolution.");
        }

        private sealed class GoogleAccountResponseHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;
            public GoogleAccountResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) { this.respond = respond; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(respond(request));
        }

        private static void GoogleAccountEmail()
        {
            int requests = 0;
            string Read(HttpStatusCode status, string body, bool offline = false)
            {
                using (var client = new HttpClient(new GoogleAccountResponseHandler(request =>
                {
                    requests++;
                    Require(request.Method == HttpMethod.Get && request.RequestUri.AbsoluteUri == "https://www.googleapis.com/oauth2/v2/userinfo",
                        "Account display attempted an API outside the email permission.");
                    Require(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "test-access-token",
                        "UserInfo did not use the authorized account's token.");
                    if (offline) throw new HttpRequestException("Simulated offline account lookup.");
                    return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                })))
                {
                    var service = new GoogleCalendarService(client);
                    typeof(GoogleCalendarService).GetField("accessToken", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(service, "test-access-token");
                    var read = typeof(GoogleCalendarService).GetMethod("ReadAccountAsync", BindingFlags.NonPublic | BindingFlags.Instance);
                    return ((Task<string>)read.Invoke(service, null)).GetAwaiter().GetResult();
                }
            }
            Require(Read(HttpStatusCode.OK, "{\"email\":\"  owner@example.com  \"}") == "owner@example.com", "Granted email could not be shown without Calendar metadata access.");
            Require(Read(HttpStatusCode.OK, "{}") == null, "Missing email was treated as an account.");
            Require(Read(HttpStatusCode.OK, "{\"email\":123}") == null, "Non-text email was treated as an account.");
            Require(Read(HttpStatusCode.OK, "{\"email\":\"not-an-email\"}") == null, "Invalid email was treated as an account.");
            Require(Read(HttpStatusCode.Forbidden, "{\"error\":{\"code\":403,\"message\":\"insufficient authentication scopes\"}}") == null,
                "Declining email permission prevented optional account lookup from completing.");
            Require(Read(HttpStatusCode.OK, "{}", offline: true) == null, "Temporary email lookup failure escaped sign-in.");
            Require(requests == 6, "Account lookup made unexpected fallback requests.");
        }

        private static void DriveDeletion()
        {
            var remote = new Dictionary<string, string> { ["missing"] = "Missing locally", ["removed"] = "Explicitly deleted" };
            var plan = PetSyncPlan.Build(new Dictionary<string, string>(), remote, remote.Keys, deletedIds: new[] { "removed" });
            Require(plan.DeleteRemote.SequenceEqual(new[] { "removed" }), "A missing local pet would delete its backup.");
        }

        private static void PackagedAssets()
        {
            foreach (string id in CharacterCatalog.DefaultIds)
            {
                var pet = CharacterCatalog.Read("DefaultPets/" + id + "/pet.json");
                Require(File.Exists(pet.ImagePath) && pet.Rows > 0, "Default pet is incomplete: " + id);
            }
            foreach (string relative in new[] { "Player/character.html", "Player/player.html", "ScheduleWidget.Updater.exe", "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.Wpf.dll", "runtimes/win-x64/native/WebView2Loader.dll", "runtimes/win-x86/native/WebView2Loader.dll" })
                Require(File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relative)), "Build output is missing " + relative);
        }

        private static void SignedUpdates()
        {
            var manifest = new UpdateManifest { App = "ScheduleWidget", Version = "2026.1004.1200", File = "ScheduleWidget.zip", Size = 123, Sha256 = new string('a', 64) };
            using (var key = new RSACng(2048))
            {
                manifest.Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(UpdateClient.CanonicalString(manifest)), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                var parsed = UpdateClient.ParseManifest(JsonConvert.SerializeObject(manifest));
                Require(UpdateClient.Evaluate(parsed, key.ToXmlString(false), new Version(0, 0)).IsNewer, "Valid update was rejected.");
                parsed.Size++;
                Require(!UpdateClient.VerifySignature(parsed, key.ToXmlString(false)), "Modified update passed signature verification.");
                Throws<InvalidOperationException>(() => UpdateClient.Evaluate(parsed, key.ToXmlString(false), new Version(0, 0)));
            }
        }

        private static void Shortcuts()
        {
            Require(HotKeyGesture.TryParse("Ctrl+G", out _) && HotKeyGesture.TryParse("F8", out _), "Valid shortcut rejected.");
            Require(!HotKeyGesture.TryParse("G", out _) && !HotKeyGesture.TryParse("Shift+G", out _), "Typing key accepted as a global shortcut.");
        }

        private static void RenderingFailureClassification()
        {
            var failure = new System.Runtime.InteropServices.COMException("Localized graphics failure", unchecked((int)0x88980406));
            Require(App.IsRenderThreadFailure(failure), "The recorded render failure was missed.");
            Require(App.IsRenderThreadFailure(new TargetInvocationException(failure)), "A wrapped render failure was missed.");
            Require(!App.IsRenderThreadFailure(null), "A missing exception was treated as fatal.");
            Require(!App.IsRenderThreadFailure(new System.Runtime.InteropServices.COMException("Unrelated browser failure", unchecked((int)0x80004005))), "An unrelated COM failure was treated as fatal.");
            Require(!App.IsRenderThreadFailure(new Exception("UCEERR_RENDERTHREADFAILURE (0x88980406)")), "Message text was mistaken for the actual HRESULT.");
        }

        private static void CharacterDisplayChanges()
        {
            var data = Data();
            var mini = new MiniWindow(data, () => true, () => { });
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var prepare = typeof(MiniWindow).GetMethod("PrepareCharacterDisplayChange", flags);
            var resume = typeof(MiniWindow).GetMethod("ResumeCharacterAfterDisplayChange", flags);
            var recovery = typeof(MiniWindow).GetField("recoveryOverride", flags);
            try
            {
                string before = JsonConvert.SerializeObject(data);
                var old = (FrameworkElement)mini.FindName("CharacterView");
                double width = old.Width, height = old.Height;
                Canvas.SetLeft(old, 24); Canvas.SetTop(old, 12);
                int resumes = 0;
                recovery.SetValue(mini, (Action<bool, string>)((recreate, reason) => {
                    Require(recreate && reason == "display-change", "Display recovery lost its reason.");
                    resumes++;
                }));
                var pendingAnswer = new TaskCompletionSource<bool>();
                typeof(MiniWindow).GetField("healthCheckOverride", flags).SetValue(mini, (Func<Task<bool>>)(() => pendingAnswer.Task));
                Task pendingCheck;
                var previousContext = SynchronizationContext.Current;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(null);
                    pendingCheck = (Task)typeof(MiniWindow).GetMethod("RunHealthCheckAsync", flags).Invoke(mini, new object[] { "before-display-change" });
                }
                finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
                prepare.Invoke(mini, null);
                var fresh = (FrameworkElement)mini.FindName("CharacterView");
                Require(fresh != old && fresh.Width == width && fresh.Height == height && Canvas.GetLeft(fresh) == 24 && Canvas.GetTop(fresh) == 12,
                    "Display change failed to replace the graphics view in place.");
                prepare.Invoke(mini, null);
                Require(ReferenceEquals(fresh, mini.FindName("CharacterView")) && resumes == 0, "Repeated display messages recreated or resumed the browser too early.");
                Require(!(bool)typeof(MiniWindow).GetField("viewReady", flags).GetValue(mini), "A stale view remained ready.");
                resume.Invoke(mini, null);
                resume.Invoke(mini, null);
                Require(resumes == 1, "Display recovery did not resume exactly once.");
                pendingAnswer.SetResult(false);
                pendingCheck.GetAwaiter().GetResult();
                var retry = (System.Windows.Threading.DispatcherTimer)typeof(MiniWindow).GetField("recoveryTimer", flags).GetValue(mini);
                Require(retry == null || !retry.IsEnabled, "A late failure from the old view queued recovery of the replacement.");
                Require(JsonConvert.SerializeObject(data) == before, "Display recovery changed schedules or pet settings.");
                mini.Close();
                prepare.Invoke(mini, null);
                resume.Invoke(mini, null);
                Require(ReferenceEquals(fresh, mini.FindName("CharacterView")) && resumes == 1, "A closed widget resumed its browser.");
            }
            finally { mini.Close(); }
        }

        private static void UpdateConfirmationLayout()
        {
            string notes = "### 변경 사항\n\n- **긴 일정**을 `달력`에서 확인합니다.\n  이어지는 설명도 유지됩니다.\n" +
                string.Join("\n", Enumerable.Range(1, 16).Select(n => "- 변경 " + n + ": 여러 날의 일정과 설정이 유지되며 긴 설명도 끝까지 읽을 수 있습니다.")) +
                "\n\n### 추가 안내\n\n마지막 변경 내용까지 보입니다.";
            var manifest = new UpdateManifest { App = "ScheduleWidget", Version = "2099.0101.0001", File = "update.zip", Size = 1,
                Sha256 = new string('a', 64), Notes = notes };
            UpdateCheckResult check;
            using (var key = new RSACng(2048))
            {
                manifest.Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(UpdateClient.CanonicalString(manifest)), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                check = UpdateClient.Evaluate(manifest, key.ToXmlString(false), new Version(2026, 1015, 2312));
            }
            string originalTheme = AuxTheme.Current;
            var dialog = new UpdateConfirmationWindow(check);
            try
            {
                foreach (string theme in new[] { "Light", "Dark" })
                {
                    AuxTheme.SetTheme(theme);
                    foreach (var area in new[] { new Rect(0, 0, 1920, 1040), new Rect(-700, 20, 420, 540) })
                    {
                        typeof(UpdateConfirmationWindow).GetMethod("FitWithin", BindingFlags.Instance | BindingFlags.NonPublic)
                            .Invoke(dialog, new object[] { area, new Rect(area.Left + 10, area.Bottom - 80, 300, 60) });
                        var bounds = new Rect(dialog.Left, dialog.Top, dialog.Width, dialog.Height);
                        Require(area.Contains(bounds), "Update confirmation escaped the owner's work area.");
                        var content = (FrameworkElement)dialog.Content;
                        content.Measure(new Size(dialog.Width, dialog.Height));
                        content.Arrange(new Rect(0, 0, dialog.Width, dialog.Height));
                        content.UpdateLayout();
                        string rendered = string.Join("\n", VisualDescendants<TextBlock>(content).Select(t => t.Text));
                        Require(rendered.Contains("긴 일정을 달력에서 확인합니다. 이어지는 설명도 유지됩니다.") && rendered.Contains("마지막 변경 내용까지 보입니다."),
                            "Long release notes were truncated or lost wrapped content.");
                        Require(!rendered.Contains("###") && !rendered.Contains("**긴 일정**") && !rendered.Contains("`달력`"), "Raw Markdown leaked into the dialog.");
                        var scroll = (ScrollViewer)dialog.FindName("NotesScroll");
                        Require(scroll.ViewportHeight > 40 && scroll.ScrollableHeight > 0, "Long release notes have no usable scroll viewport.");
                        foreach (string name in new[] { "LaterButton", "InstallButton" })
                        {
                            var button = (Button)dialog.FindName(name);
                            var buttonBounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
                            Require(new Rect(content.RenderSize).Contains(buttonBounds) && button.ActualHeight >= 40, "Update actions were clipped: " + name);
                        }
                        scroll.ScrollToEnd();
                        content.UpdateLayout();
                        Require(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, "The end of the release notes cannot be reached.");
                    }
                }
                Require(!dialog.IsVisible && dialog.DialogResult != true, "Measuring the dialog displayed it or accepted installation.");
            }
            finally { dialog.Close(); AuxTheme.SetTheme(originalTheme); }
        }

        private static void WindowResources()
        {
            var app = new App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var windows = new List<Window>();
            try
            {
                var main = new MainWindow();
                // The window stays unshown: Loaded, timers and external-service work never run.
                // Still replace its store before cleanup, so future closing changes cannot touch real data.
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(MainWindow).GetField("dataStore", flags).SetValue(main, Store("window"));
                ((TrayService)typeof(MainWindow).GetField("trayService", flags).GetValue(main)).Dispose();
                windows.Add(main);
                var data = Data();
                var mini = new MiniWindow(data, () => true, () => { });
                windows.Add(mini);
                windows.Add(new MusicWindow(data.Music, () => true, () => { }));
                windows.Add(new ContactWindow(data, new CommunicationService(), () => true));
                windows.Add(new CharacterWindow(CharacterCatalog.DefaultManifest));
                windows.Add(new PetSettingsWindow(mini, 0));
                foreach (Window window in windows)
                {
                    Require(window.Content is FrameworkElement, "Window content missing: " + window.GetType().Name);
                    var content = (FrameworkElement)window.Content;
                    content.Measure(new Size(800, 600));
                    content.Arrange(new Rect(0, 0, 800, 600));
                }
                Run("Update confirmation keeps long notes readable and installation actions on screen", UpdateConfirmationLayout);
                Require(mini.FindName("WeekCalendar") != null, "Mini calendar was not constructed.");

                bool calendarClosed = false;
                mini.Closed += (sender, args) => calendarClosed = true;
                var todoButton = (System.Windows.Controls.Button)mini.FindName("MonthViewButton");
                int windowCount = app.Windows.Count;
                todoButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Require(mini.IsMonthViewOpen && app.Windows.Count == windowCount && !main.IsVisible, "The month view opened a separate window.");
                todoButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Require(!mini.IsMonthViewOpen && !calendarClosed, "The month button did not restore the original range.");

                typeof(MainWindow).GetField("miniWindow", flags).SetValue(main, mini);
                var clickData = Data();
                clickData.MiniExtraCharacters.Add(new MiniCharacterSlot { Manifest = "DefaultPets/mochi-blue/pet.json" });
                clickData.MiniExtraCharacters.Add(new MiniCharacterSlot { Manifest = "DefaultPets/mochi-red/pet.json" });
                var clickMini = new MiniWindow(clickData, () => true, () => { });
                windows.Add(clickMini);
                Run("Character double-click selects its settings without opening the picker", () => PetClickRouting(clickMini, clickData));
                Run("Display changes release pet graphics, coalesce and preserve user data", CharacterDisplayChanges);
                Run("Repeated screen changes cancel stale pointer states without saving transitional geometry", DisplayPointerReset);
                Run("Overlapping pets cannot steal new-schedule or nested popup clicks", PopupClicksOverPets);
                Run("Compact calendar shows multiple schedules and scrolls every day independently", CalendarDayLists);
                Run("Month calendar covers whole months, boundaries and schedule editing", MonthCalendar);
                Run("Calendar modes resize, fit the screen and restore independent geometry", CalendarModeGeometry);
                Run("Calendar keeps both horizontal edges fixed and uses free space above or below", CalendarAnchoredExpansion);
                Run("Monthly schedule titles, times, colors and overflow remain accessible", MonthScheduleVisibility);
                Run("Monthly mode preserves the basic calendar's pet scale", CalendarModePetScale);
                Run("Range bars span dates, wrap at week boundaries and keep overlaps separate", CalendarRangeBars);
                Run("Range overflow, editing and completion preserve the original schedule", CalendarRangeActions);
                Run("Calendar mode animation keeps intermediate geometry out of saved settings", CalendarModeTransition);
                Run("Mode snapshots preserve live pixels and motion settles at the rendered destination", CalendarModePixelHandoff);
                Run("Global shortcut toggles foreground and desktop without hiding the calendar", () => HotKeyToggle(main, data));
                Run("Music bar keeps one row across playback states and narrow widths", FixedMusicBar);
                Run("External media automatically updates the bar without controlling other apps", AutomaticMediaRouting);
                Run("Media events coalesce and keep idle metadata and volume queries at zero", EventDrivenMediaWatch);
                Run("Late media subscription completion cannot restart hidden work", LateMediaSubscription);
                Run("Audio volume events preserve mute and stop after disposal", VolumeEventCallbacks);
                Run("Unified settings navigation, save, cancel and content lifecycle", () => UnifiedSettingsFlow(main, mini, data));
                SettingsAudit();
            }
            finally { app.Shutdown(); }
        }

        private static void CalendarDayLists()
        {
            var data = new AppData { MiniCharacterVisible = false, MiniPlayerVisible = false };
            DateTime monday = new DateTime(2026, 10, 5);
            for (int day = 0; day < 2; day++)
                for (int i = 0; i < 12; i++)
                    data.Schedules.Add(new ScheduleItem { Title = "Task " + i.ToString("00"), Period = monday.AddDays(day).ToString("yyyy-MM-dd") });
            for (int i = 0; i < 12; i++)
                data.Schedules.Add(new ScheduleItem { Title = "Next " + i.ToString("00"), Period = monday.AddDays(7).ToString("yyyy-MM-dd") });
            var mini = new MiniWindow(data, () => true, () => { });
            try
            {
                mini.ShowRangeFrom(monday);
                var content = (FrameworkElement)mini.Content;
                content.Measure(new Size(800, 220));
                content.Arrange(new Rect(0, 0, 800, 220));
                content.UpdateLayout();
                var days = (ItemsControl)mini.FindName("WeekDays");
                var firstDay = (FrameworkElement)days.ItemContainerGenerator.ContainerFromIndex(0);
                var nextDay = (FrameworkElement)days.ItemContainerGenerator.ContainerFromIndex(1);
                var firstList = VisualDescendants<ItemsControl>(firstDay).Single(c => c.Name == "DaySchedules");
                var nextList = VisualDescendants<ItemsControl>(nextDay).Single(c => c.Name == "DaySchedules");
                var scroll = VisualDescendants<ScrollViewer>(firstList).Single();
                var nextScroll = VisualDescendants<ScrollViewer>(nextList).Single();
                Require(firstList.Items.Count == 12 && nextList.Items.Count == 12, "A small calendar dropped schedules.");
                var second = (FrameworkElement)firstList.ItemContainerGenerator.ContainerFromIndex(1);
                Require(second != null, "The second schedule was not rendered.");
                // The container includes the gap after its card; at fractional DPI that blank gap can extend past
                // the viewport while both complete cards are still visible. Measure the card itself.
                var secondCard = VisualDescendants<Border>(second).First();
                var secondBounds = secondCard.TransformToAncestor(scroll).TransformBounds(new Rect(secondCard.RenderSize));
                Require(secondBounds.Bottom <= scroll.ViewportHeight + 1, "Only one complete schedule card fits in a compact day cell.");
                Require(scroll.ScrollableHeight > 0, "Overflow schedules cannot be scrolled.");
                scroll.ScrollToEnd();
                content.UpdateLayout();
                var last = (FrameworkElement)firstList.ItemContainerGenerator.ContainerFromIndex(11);
                Require(last != null && scroll.VerticalOffset > 0, "The final schedule is unreachable.");
                var lastBounds = last.TransformToAncestor(scroll).TransformBounds(new Rect(last.RenderSize));
                Require(lastBounds.Top >= -1 && lastBounds.Bottom <= scroll.ViewportHeight + 1, "The final schedule is clipped after scrolling.");
                Require(nextScroll.VerticalOffset == 0, "Scrolling one date also scrolled another date.");
                // A resize must preserve all items, including dates that have more than the old eight-item cap.
                content.Measure(new Size(800, 190));
                content.Arrange(new Rect(0, 0, 800, 190));
                content.UpdateLayout();
                Require(firstList.Items.Count == 12, "Resizing hid schedules again.");
                mini.ShowRangeFrom(monday.AddDays(7));
                content.UpdateLayout();
                Require(scroll.VerticalOffset == 0, "A different week inherited the previous date's scroll position.");
                Require(mini.FindName("ActionsPopup") == null && !mini.IsVisible, "The removed settings menu remains or a check displayed a window.");
            }
            finally { mini.Close(); }
        }

        private static void MonthCalendar()
        {
            var data = new AppData { MiniCharacterVisible = false, MiniPlayerVisible = false };
            var trip = new ScheduleItem { Title = "Three-day trip", Period = "2026-10-05", EndPeriod = "2026-10-07", Time = "14:30" };
            data.Schedules.Add(trip);
            data.Schedules.Add(new ScheduleItem { Title = "Other month", Period = "2026-09-30" });
            int saves = 0;
            var mini = new MiniWindow(data, () => { saves++; return true; }, () => { });
            try
            {
                mini.ShowRangeFrom(new DateTime(2026, 10, 19));
                DateTime originalStart = (DateTime)Field(mini, "weekStart");
                mini.SetMonthViewOpen(true);
                var grid = Control<ItemsControl>(mini, "MonthDays");
                List<MiniWindow.MonthDayCell> Cells() => grid.Items.Cast<MiniWindow.MonthDayCell>().ToList();
                Require(Cells().Count == 35 && Cells()[0].Date == new DateTime(2026, 9, 28) && Cells()[34].Date == new DateTime(2026, 11, 1), "October's full month grid is wrong.");
                Require(Cells().Count(c => c.IsCurrentMonth) == 31 && Cells().Count(c => c.Count == 1) == 4, "Multi-day or adjacent-month schedules were omitted.");
                foreach (double width in new[] { 240.0, 360.0, 800.0, 1200.0 })
                {
                    var content = (FrameworkElement)mini.Content;
                    mini.Width = width;
                    content.Measure(new Size(width, 260)); content.Arrange(new Rect(0, 0, width, 260)); content.UpdateLayout();
                    var panel = Control<FrameworkElement>(mini, "MonthCalendarPanel");
                    var sheet = Control<FrameworkElement>(mini, "CalendarSheet");
                    Require(Control<FrameworkElement>(mini, "WeekDays").Visibility == Visibility.Hidden && panel.ActualWidth >= sheet.ActualWidth - 18,
                        "Month view does not cover the calendar body.");
                    var buttons = VisualDescendants<Button>(grid).ToArray();
                    Require(buttons.Length == 35 && buttons.All(button => button.ActualHeight >= 20), "A month day is missing or clipped vertically.");
                    Require(buttons.Last().TransformToAncestor(grid).TransformBounds(new Rect(buttons.Last().RenderSize)).Bottom <= grid.ActualHeight + 1,
                        "The last week falls outside the calendar.");
                }
                Call(mini, "PrepareDayDetails", new DateTime(2026, 10, 6));
                Require(Control<ItemsControl>(mini, "DaySchedules").Items.Cast<ScheduleItem>().Single() == trip, "Date details lost a continuing multi-day event.");
                Call(mini, "EditMiniSchedule_Click", new Button { DataContext = trip }, ClickArgs());
                Require(Control<TextBox>(mini, "MiniTitleInput").Text == trip.Title && Control<DatePicker>(mini, "MiniDateInput").SelectedDate == trip.StartDate &&
                    Control<CheckBox>(mini, "MiniRangeToggle").IsChecked == true, "Month editing lost the original range.");
                Control<TextBox>(mini, "MiniTitleInput").Text = "Updated trip";
                SetField(mini, "selectedDay", null); // do not show a popup during headless checks
                Call(mini, "SaveMiniSchedule_Click", mini, ClickArgs());
                Require(saves == 1 && trip.Title == "Updated trip" && Cells().Single(c => c.Date == new DateTime(2026, 10, 6)).Hint.Contains("Updated trip"), "Saving did not refresh the month.");
                data.Schedules.Remove(trip); mini.Refresh();
                Require(Cells().Single(c => c.Date == new DateTime(2026, 10, 6)).Count == 0, "Removed schedules remain on the month.");
                mini.ShowRangeFrom(new DateTime(2028, 2, 1));
                Require(Cells().Count(c => c.IsCurrentMonth) == 29 && Cells().Any(c => c.Date == new DateTime(2028, 2, 29)), "Leap day missing.");
                mini.ShowRangeFrom(new DateTime(2026, 3, 1));
                Require(Cells().Count == 42, "A six-week month was cut short.");
                mini.ShowRangeFrom(new DateTime(2027, 2, 1));
                Require(Cells().Count == 28, "A four-week month has unnecessary rows.");
                mini.ShowRangeFrom(new DateTime(2026, 12, 1));
                Control<Button>(mini, "NextWeek").RaiseEvent(ClickArgs());
                Require((DateTime)Field(mini, "displayedMonth") == new DateTime(2027, 1, 1), "Next month failed across the year.");
                Control<Button>(mini, "PreviousWeek").RaiseEvent(ClickArgs());
                Require((DateTime)Field(mini, "displayedMonth") == new DateTime(2026, 12, 1), "Previous month failed across the year.");
                foreach (var limit in new[] { DateTime.MinValue, DateTime.MaxValue })
                {
                    mini.ShowRangeFrom(limit);
                    Require(Cells().Count(c => c.IsCurrentMonth) == 31, "Boundary month is incomplete.");
                    Require(limit.Year == 1 ? !Control<Button>(mini, "PreviousWeek").IsEnabled : !Control<Button>(mini, "NextWeek").IsEnabled, "Boundary navigation overflows.");
                }
                Call(mini, "Today_Click", mini, ClickArgs());
                Require(Cells().Count(c => c.IsToday) == 1 && !Control<Button>(mini, "TodayButton").IsEnabled, "Today does not return to the current month.");
                mini.SetMonthViewOpen(false);
                Require((DateTime)Field(mini, "weekStart") == originalStart && Control<FrameworkElement>(mini, "WeekDays").Visibility == Visibility.Visible && !mini.IsVisible,
                    "Closing month view changed the original week or showed a new window.");
            }
            finally { mini.Close(); }
        }

        private static void HotKeyToggle(MainWindow main, AppData data)
        {
            var actions = new List<bool>();
            var state = Tuple.Create(false, false, false);
            SetField(main, "hotKeyWindowStateOverride", (Func<Tuple<bool, bool, bool>>)(() => state));
            SetField(main, "hotKeyVisibilityOverride", (Action<bool>)actions.Add);
            SetField(main, "appData", data);
            void Press()
            {
                object[] args = { IntPtr.Zero, 0x0312, new IntPtr(0x5347), IntPtr.Zero, false };
                typeof(MainWindow).GetMethod("BringToFrontHotKeyHook", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(main, args);
                Require((bool)args[4], "The registered shortcut message was not consumed.");
            }
            try
            {
                Press(); // hidden -> raise
                state = Tuple.Create(true, true, false); Press(); // active -> lower
                state = Tuple.Create(true, false, false); Press(); // background -> raise
                Press(); // switched to another app since raising -> raise again
                state = Tuple.Create(true, false, true); Press(); // pinned by this shortcut -> lower
                state = Tuple.Create(false, true, false); Press(); // minimized -> restore
                Require(actions.SequenceEqual(new[] { true, false, true, true, false, true }), "Shortcut raised/lowered the wrong state.");
                SetField(main, "closingApp", true); Press();
                Require(actions.Count == 6, "Shortcut reopened an exiting app.");
                Call(main, "UnregisterBringToFrontHotKey");
                Require(!(bool)Field(main, "hotKeyRaised"), "Reconfiguring the shortcut kept stale raise state.");
            }
            finally
            {
                SetField(main, "closingApp", false);
                SetField(main, "hotKeyWindowStateOverride", null);
                SetField(main, "hotKeyVisibilityOverride", null);
            }
        }

        private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var descendant in VisualDescendants<T>(child)) yield return descendant;
            }
        }

        private static void PetClickRouting(MiniWindow mini, AppData data)
        {
            var requests = new List<int>();
            int pickerRequests = 0;
            mini.CharacterSettingsRequested += requests.Add;
            SetField(mini, "pickerOverride", (Action)(() => pickerRequests++));
            Call(mini, "HandlePetClick", 1, 1);
            Require(requests.Count == 0 && pickerRequests == 0, "The first click opened a window.");
            Call(mini, "HandlePetClick", 1, 2);
            Require(requests.SequenceEqual(new[] { 1 }), "Double-click did not target the second character.");
            Call(mini, "HandlePetClick", 2, 1);
            Call(mini, "HandlePetClick", 2, 2);
            Require(requests.SequenceEqual(new[] { 1, 2 }), "Double-click did not target the third character.");
            Call(mini, "HandlePetClick", 0, 1);
            Call(mini, "HandlePetClick", 1, 2);
            Require(requests.Count == 2, "Clicks on different characters became a double-click.");
            Call(mini, "HandlePetClick", 0, 1);
            SetField(mini, "pressPoint", (Point?)new Point(0, 0));
            Call(mini, "PetPressMoved", new Point(100, 100), true);
            Call(mini, "HandlePetClick", 0, 2);
            Require(requests.Count == 2, "Dragging a character opened settings.");
            data.MiniExtraCharacters[1].Hidden = true;
            Call(mini, "HandlePetClick", 2, 1);
            Call(mini, "HandlePetClick", 2, 2);
            Require(requests.Count == 2 && pickerRequests == 0, "A hidden character or a double-click opened the picker.");
            ((IPetSettingsHost)mini).ChangePet(1);
            Require(pickerRequests == 1, "The explicit change-character action no longer opens the picker.");
        }

        private static object Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(owner);
        private static void SetField(object owner, string name, object value) => owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(owner, value);
        private static void Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(owner, args);

        private static void UnifiedSettingsFlow(MainWindow main, MiniWindow mini, AppData data)
        {
            data.StartupEnabled = false;
            data.BringToFrontHotKeyEnabled = false;
            SetField(main, "appData", data);
            SetField(main, "miniWindow", mini);
            // Disabling startup uses a nonexistent per-run key and never touches Windows' Run key.
            SetField(main, "startupService", new StartupService("Software\\ScheduleWidgetChecks\\" + Guid.NewGuid().ToString("N")));
            Require(main.FindName("ContactButton") == null, "Schedule shortcut buttons were not removed.");
            for (int round = 0; round < 2; round++)
            {
                Call(main, "BeginSettingsEdit");
                var settings = new SettingsWindow();
                SetField(main, "settingsHost", settings);
                Call(main, "BuildUnifiedSettingsPages", settings);
                var navigation = (System.Windows.Controls.ListBox)settings.FindName("SettingsNavigation");
                Require(navigation.Items.Count == 7, "Unified settings must have seven categories.");
                foreach (SettingsPage page in Enum.GetValues(typeof(SettingsPage)))
                {
                    settings.SelectPage(page);
                    Call(main, "PrepareSettingsPage", page);
                    ((FrameworkElement)settings.Content).Measure(new Size(1000, 760));
                    ((FrameworkElement)settings.Content).Arrange(new Rect(0, 0, 1000, 760));
                    Require(settings.SelectedPage == page, "Settings navigation did not select " + page);
                }

                settings.SelectPage(SettingsPage.Music);
                Call(main, "PrepareSettingsPage", SettingsPage.Music);
                var music = (MusicWindow)Field(main, "musicWindow");
                var editor = music.FindName("MusicSettingsContent") as FrameworkElement;
                Require(music.IsSettingsHosted && editor.Parent != null, "Music editor did not join settings.");
                var track = new MusicTrack { Title = "Silent settings check", Source = SilentWave() };
                music.CurrentPlaylist.Tracks.Add(track);
                music.SetVolume(0);
                music.PlayFromListAsync(track).GetAwaiter().GetResult();
                Require(music.IsPlaying && music.PlayingTrack == track, "The isolated local track did not start.");
                settings.SelectPage(SettingsPage.General);
                Call(main, "PrepareSettingsPage", SettingsPage.General);
                Require(!music.IsSettingsHosted && ReferenceEquals(music.Content, editor), "Leaving music did not return its existing player view.");
                Require(music.IsPlaying && music.PlayingTrack == track, "Changing settings pages stopped playback.");

                var contact = (ContactWindow)Field(main, "contactWindow");
                var chat = (System.Windows.Controls.TextBox)contact.FindName("TelegramChat");
                chat.Text = "checks-chat-" + round;
                Require(contact.HasPendingChanges, "Embedded connection edits were not tracked.");
                var themes = (System.Windows.Controls.ComboBox)main.FindName("InlinePresetCombo");
                themes.SelectedIndex = 1; // preview Dark
                if (round == 0)
                {
                    Call(main, "CloseInlineSettings", false);
                    Require(data.Communication.TelegramChatId == null && data.Appearance.ThemePreset == "Light", "Closing settings saved pending connection or theme edits.");
                    Require(AuxTheme.Current == "Light", "Closing settings did not undo the theme preview.");
                }
                else
                {
                    Call(main, "InlineSettingsApplyButton_Click", settings, new RoutedEventArgs());
                    Require(data.Communication.TelegramChatId == "checks-chat-1" && data.Appearance.ThemePreset == "Dark", "Settings save did not preserve all edited pages.");
                    Require(Store("window").LoadData().Data.Communication.TelegramChatId == "checks-chat-1", "Connection settings were not written to the isolated store.");
                }
                Require(Field(main, "settingsHost") == null && Field(main, "contactWindow") == null, "Settings editors were not released on close.");
                Require(!music.IsSettingsHosted && music.Content != null, "Closing settings lost the music editor.");
                Require(music.IsPlaying && music.PlayingTrack == track, "Closing settings stopped playback.");
                Call(music, "StopPlayback");
                Require(main.FindName("InlinePresetCombo") == themes && themes.Parent != null, "Named controls were not restored for the next settings session.");
            }
        }

        private static string SilentWave()
        {
            string path = Path.Combine(root, "settings-silence.wav");
            if (File.Exists(path)) return path;
            const int bytes = 8000 * 2 * 30;
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000);
                writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(bytes); writer.Write(new byte[bytes]);
            }
            return path;
        }
    }
}
