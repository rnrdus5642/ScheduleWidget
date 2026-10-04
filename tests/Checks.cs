using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Newtonsoft.Json;

namespace ScheduleWidget.Checks
{
    internal static class Checks
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
            Run("Drive sync only deletes explicitly removed pets", DriveDeletion);
            Run("Default pet manifests and packaged assets load", PackagedAssets);
            Run("Updates reject a modified signed manifest", SignedUpdates);
            Run("Global shortcuts reject unmodified typing keys", Shortcuts);
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
                Require(mini.FindName("WeekCalendar") != null, "Mini calendar was not constructed.");
            }
            finally { app.Shutdown(); }
        }
    }
}
