using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

                bool calendarClosed = false;
                mini.Closed += (sender, args) => calendarClosed = true;
                var todoButton = (System.Windows.Controls.Button)mini.FindName("TodoButton");
                int windowCount = app.Windows.Count;
                todoButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Require(mini.IsAllSchedulesOpen && app.Windows.Count == windowCount && !main.IsVisible, "The agenda opened a separate window.");
                todoButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Require(!mini.IsAllSchedulesOpen && !calendarClosed, "The list button did not fold the agenda back into the calendar.");

                typeof(MainWindow).GetField("miniWindow", flags).SetValue(main, mini);
                var clickData = Data();
                clickData.MiniExtraCharacters.Add(new MiniCharacterSlot { Manifest = "DefaultPets/mochi-blue/pet.json" });
                clickData.MiniExtraCharacters.Add(new MiniCharacterSlot { Manifest = "DefaultPets/mochi-red/pet.json" });
                var clickMini = new MiniWindow(clickData, () => true, () => { });
                windows.Add(clickMini);
                Run("Character double-click selects its settings without opening the picker", () => PetClickRouting(clickMini, clickData));
                Run("Compact calendar shows multiple schedules and scrolls every day independently", CalendarDayLists);
                Run("Embedded agenda lists distant schedules, resizes and reuses schedule editing", AllSchedulesPanel);
                Run("Unified settings navigation, save, cancel and content lifecycle", () => UnifiedSettingsFlow(main, mini, data));
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
                var secondBounds = second.TransformToAncestor(scroll).TransformBounds(new Rect(second.RenderSize));
                Require(secondBounds.Bottom <= scroll.ViewportHeight + 1, "Only one schedule fits in a compact day cell.");
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

        private static void AllSchedulesPanel()
        {
            var data = new AppData { MiniCharacterVisible = false, MiniPlayerVisible = false };
            var earlier = new ScheduleItem { Title = "Earlier", Period = DateTime.Today.AddDays(-2).ToString("yyyy-MM-dd") };
            var distant = new ScheduleItem { Title = "Next year", Period = DateTime.Today.AddYears(1).ToString("yyyy-MM-dd"),
                EndPeriod = DateTime.Today.AddYears(1).AddDays(2).ToString("yyyy-MM-dd"), Time = "14:30", Color = "#336699" };
            data.Schedules.Add(distant);
            data.Schedules.Add(earlier);
            int saves = 0;
            var mini = new MiniWindow(data, () => { saves++; return true; }, () => { });
            try
            {
                var content = (FrameworkElement)mini.Content;
                void Layout(double width)
                {
                    mini.Width = width;
                    content.Measure(new Size(width, 260));
                    content.Arrange(new Rect(0, 0, width, 260));
                    content.UpdateLayout();
                }
                Layout(800);
                mini.SetAllSchedulesOpen(true);
                Layout(800);
                var panel = (FrameworkElement)mini.FindName("AllSchedulesPanel");
                var list = (ItemsControl)mini.FindName("AllSchedulesList");
                ScheduleItem ItemAt(int index) => (ScheduleItem)list.Items[index].GetType().GetProperty("Item").GetValue(list.Items[index]);
                Require(list.Items.Count == 2 && ItemAt(0) == earlier && ItemAt(1) == distant, "The full agenda omitted or misordered distant schedules.");
                Require((string)list.Items[1].GetType().GetProperty("Detail").GetValue(list.Items[1]) is string detail &&
                    detail.Contains(DateTime.Today.AddYears(1).Year.ToString()) && detail.Contains("14:30"), "The agenda lost the year or time.");
                Require(Window.GetWindow(panel) == mini && Grid.GetColumn(panel) == 1, "The agenda is not inside the calendar.");
                var sheet = (FrameworkElement)mini.FindName("CalendarSheet");
                Require(panel.TransformToAncestor(sheet).TransformBounds(new Rect(panel.RenderSize)).Right <= sheet.ActualWidth + 1, "The agenda extends outside the calendar.");
                Layout(360);
                Require(Grid.GetColumn(panel) == 0 && panel.ActualWidth <= sheet.ActualWidth, "The narrow calendar clips its agenda.");
                Call(mini, "PrepareAllScheduleEditor", distant);
                Require(((TextBox)mini.FindName("MiniTitleInput")).Text == distant.Title &&
                    ((DatePicker)mini.FindName("MiniDateInput")).SelectedDate == distant.StartDate &&
                    ((CheckBox)mini.FindName("MiniRangeToggle")).IsChecked == true, "Editing a distant multi-day schedule lost its values.");
                ((TextBox)mini.FindName("MiniTitleInput")).Text = "Updated next year";
                Call(mini, "SaveMiniSchedule_Click", mini, new RoutedEventArgs(Button.ClickEvent));
                Require(saves == 1 && distant.Title == "Updated next year" && ItemAt(1) == distant, "Agenda edits did not save and refresh.");
                SetField(mini, "blockItem", distant);
                Call(mini, "BlockDone_Click", mini, new RoutedEventArgs(Button.ClickEvent));
                Require(distant.IsCompleted && saves == 2, "Agenda completion did not use the existing save flow.");
                Call(mini, "BlockDelete_Click", mini, new RoutedEventArgs(Button.ClickEvent));
                Require(data.Schedules.Contains(distant), "The first delete press removed a schedule.");
                Call(mini, "BlockDelete_Click", mini, new RoutedEventArgs(Button.ClickEvent));
                Require(!data.Schedules.Contains(distant) && list.Items.Count == 1 && saves == 3, "Confirmed deletion did not refresh the agenda.");
                Call(mini, "PrepareAllScheduleEditor", new object[] { null });
                ((TextBox)mini.FindName("MiniTitleInput")).Text = "New from agenda";
                Call(mini, "SaveMiniSchedule_Click", mini, new RoutedEventArgs(Button.ClickEvent));
                Require(list.Items.Count == 2 && data.Schedules.Any(s => s.Title == "New from agenda") && saves == 4, "Adding through the agenda failed.");
                ((Button)mini.FindName("AllSchedulesClose")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!mini.IsAllSchedulesOpen && !mini.IsVisible, "Closing the agenda opened or closed a desktop window.");
                data.Schedules.Clear();
                mini.SetAllSchedulesOpen(true);
                Require(list.Items.Count == 0 && ((FrameworkElement)mini.FindName("AllSchedulesEmpty")).Visibility == Visibility.Visible, "The empty agenda did not refresh.");
            }
            finally { mini.Close(); }
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
