using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static void ScheduleTransactionHardening()
        {
            foreach (bool edit in new[] { false, true })
            {
                var data = new AppData { MiniCharacterVisible = false };
                var original = new ScheduleItem { Title = "원본", Period = DateTime.Today.ToString("yyyy-MM-dd"), GoogleEventId = "owned-event" };
                original.GoogleSyncedHash = GoogleCalendarSync.Hash(original);
                if (edit) data.Schedules.Add(original);
                var disk = Store("mini-draft-rollback-" + edit); disk.SaveData(data);
                var store = new RetryScheduleStore(disk);
                MiniWindow mini = null;
                mini = new MiniWindow(data, () =>
                {
                    bool saved = true;
                    try { store.SaveData(data); } catch (DataStorageException) { saved = false; }
                    mini?.Refresh(); // Exercise the real callback's redraw before rollback as well.
                    return saved;
                }, () => { });
                try
                {
                    Control<Popup>(mini, "DayPopup").Child = null; // No visible popup HWND in headless checks.
                    string[] observedTitles = null;
                    mini.ScheduleChangeCompleted += () => observedTitles = data.Schedules.Select(s => s.Title).ToArray();
                    PrepareNewSchedule(mini, DateTime.Today);
                    if (edit) SetField(mini, "miniEditId", (Guid?)original.Id);
                    Control<TextBox>(mini, "MiniTitleInput").Text = "취소할 초안";
                    Call(mini, "SaveMiniSchedule_Click", mini, ClickArgs());
                    Require(Control<TextBox>(mini, "MiniTitleInput").Text == "취소할 초안" &&
                        data.Schedules.Count == (edit ? 1 : 0) && (!edit || original.Title == "원본"), "Failed mini save mutated live data or lost its input.");
                    Require(observedTitles != null && !observedTitles.Contains("취소할 초안"), "A companion list was notified before the rollback finished.");
                    Call(mini, "CancelMiniSchedule_Click", mini, ClickArgs());
                    disk.SaveData(data);
                    Require(disk.LoadData().Data.Schedules.All(s => s.Title != "취소할 초안"), "Cancel persisted a rejected draft through a later save.");
                    var plan = GoogleCalendarSync.Plan(data.Schedules, new List<GoogleEvent>(), new List<string>(), DateTime.Today);
                    Require(plan.Insert.Count == 0 && plan.Patch.Count == 0, "A cancelled draft would be uploaded to Google.");

                    PrepareNewSchedule(mini, DateTime.Today);
                    Control<TextBox>(mini, "MiniTitleInput").Text = "다시 추가";
                    Call(mini, "SaveMiniSchedule_Click", mini, ClickArgs());
                    Control<Popup>(mini, "DayPopup").IsOpen = false;
                    PrepareNewSchedule(mini, DateTime.Today);
                    Control<TextBox>(mini, "MiniTitleInput").Text = "다시 추가";
                    store.Reject = false;
                    Call(mini, "SaveMiniSchedule_Click", mini, ClickArgs());
                    Require(disk.LoadData().Data.Schedules.Count(s => s.Title == "다시 추가") == 1, "Closing a rejected draft and retrying made duplicates.");
                    Require(!mini.IsVisible && !mini.PersistingScheduleChange, "Schedule commit leaked a window or transaction state.");
                }
                finally { mini.Close(); }
            }
        }

        private sealed class ReadOnlyCalendarHandler : HttpMessageHandler
        {
            internal int Writes;
            internal ScheduleItem Item;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                if (request.Method != HttpMethod.Get) { Writes++; throw new InvalidOperationException("Unexpected calendar write after failed local save."); }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JObject {
                    ["items"] = new JArray(new JObject { ["id"] = Item.GoogleEventId, ["summary"] = Item.Title,
                        ["start"] = new JObject { ["date"] = Item.Period },
                        ["end"] = new JObject { ["date"] = DateTime.Parse(Item.Period).AddDays(1).ToString("yyyy-MM-dd") },
                        ["extendedProperties"] = new JObject { ["private"] = new JObject { ["scheduleWidget"] = "1", ["scheduleWidgetCompleted"] = "0" } } })
                }.ToString()) });
            }
        }

        private static void RejectedScheduleActionsNeverSync()
        {
            foreach (string action in new[] { "list-delete", "list-complete", "block-delete", "block-complete", "day-delete", "day-complete" })
            {
                var data = new AppData { MiniCharacterVisible = false, StartupEnabled = false, BringToFrontHotKeyEnabled = false };
                var item = new ScheduleItem { Title = "보존할 일정", Period = DateTime.Today.ToString("yyyy-MM-dd"), GoogleEventId = "owned-event", GoogleEndMinutes = 1440 };
                item.GoogleSyncedHash = GoogleCalendarSync.Hash(item); data.Schedules.Add(item);
                data.GoogleCalendar.SyncedEventIds.Add(item.GoogleEventId); data.GoogleCalendar.OwnedEventIds.Add(item.GoogleEventId); data.GoogleCalendar.OwnershipVersion = 1;
                var main = new MainWindow(); ((TrayService)Field(main, "trayService")).Dispose();
                SetField(main, "appData", data); SetField(main, "dataStore", new RetryScheduleStore(Store("rejected-action-" + action)));
                var mini = new MiniWindow(data, () => false, () => { });
                try
                {
                    Control<Popup>(mini, "DayPopup").Child = null; Control<Popup>(mini, "BlockPopup").Child = null;
                    if (action == "list-delete")
                    {
                        SetField(main, "_pendingRemovalId", (Guid?)item.Id);
                        Control<FrameworkElement>(main, "RemoveConfirmPanel").Visibility = Visibility.Visible;
                        Call(main, "RemoveConfirmApplyButton_Click", main, ClickArgs());
                        Require(Control<FrameworkElement>(main, "RemoveConfirmPanel").Visibility == Visibility.Visible, "Rejected delete lost its retry panel.");
                    }
                    else if (action == "list-complete") Call(main, "CompleteSchedule_Click", new MenuItem { DataContext = item }, ClickArgs());
                    else if (action.StartsWith("block"))
                    {
                        SetField(mini, "blockItem", item); Control<Button>(mini, "BlockDelete").Tag = "armed";
                        Call(mini, action.EndsWith("delete") ? "BlockDelete_Click" : "BlockDone_Click", mini, ClickArgs());
                    }
                    else
                    {
                        Call(mini, "PrepareDayDetails", DateTime.Today);
                        Call(mini, action.EndsWith("delete") ? "DeleteMiniSchedule_Click" : "CompleteMiniSchedule_Click",
                            new Button { DataContext = item, Tag = "armed" }, ClickArgs());
                    }
                    Require(data.Schedules.Count == 1 && ReferenceEquals(data.Schedules[0], item) && !item.IsCompleted, action + " was not rolled back.");
                    using (var handler = new ReadOnlyCalendarHandler { Item = item })
                    using (var client = new HttpClient(handler))
                    {
                        var google = new GoogleCalendarService(client); SetField(google, "accessToken", "checks-access"); SetField(google, "accessTokenExpires", DateTime.UtcNow.AddHours(1));
                        var previous = SynchronizationContext.Current;
                        try { SynchronizationContext.SetSynchronizationContext(null); google.SyncAsync(data.GoogleCalendar, data.Schedules).GetAwaiter().GetResult(); }
                        finally { SynchronizationContext.SetSynchronizationContext(previous); }
                        Require(handler.Writes == 0, "A rejected schedule action sent a Google write.");
                    }
                }
                finally { mini.Close(); SetField(main, "_dataUnavailable", true); Call(main, "MainWindow_Closed", main, EventArgs.Empty); main.Close(); }
            }
        }

        private sealed class RevokeHandler : HttpMessageHandler
        {
            internal bool Succeed;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                Require(request.RequestUri.AbsolutePath == "/revoke", "Unexpected request in sign-out check.");
                if (!Succeed) throw new HttpRequestException("Synthetic offline revoke");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            }
        }

        private static void GooglePersistenceWarnings()
        {
            foreach (string action in new[] { "switches", "offline-signout", "revoked-signout" })
            {
                var main = new MainWindow(); ((TrayService)Field(main, "trayService")).Dispose();
                var data = new AppData { MiniCharacterVisible = false, StartupEnabled = false, BringToFrontHotKeyEnabled = false };
                data.GoogleCalendar.Enabled = data.GoogleCalendar.PetsEnabled = true;
                data.GoogleCalendar.ProtectedRefreshToken = SecretStore.Protect("checks-refresh");
                var disk = Store("google-persistence-" + action); disk.SaveData(data);
                var store = new RetryScheduleStore(disk);
                SetField(main, "appData", data); SetField(main, "dataStore", store);
                var mini = new MiniWindow(data, () => true, () => { }); SetField(main, "miniWindow", mini);
                var settings = OpenSettingsForCheck(main);
                using (var handler = new RevokeHandler { Succeed = action == "revoked-signout" })
                using (var client = new HttpClient(handler))
                {
                    SetField(main, "googleCalendar", new GoogleCalendarService(client));
                    try
                    {
                        if (action == "switches")
                        {
                            foreach (string channel in new[] { "GoogleCalendar", "GooglePets" })
                            {
                                Control<ToggleButton>(main, channel + "Switch").IsChecked = false;
                                Call(main, channel + "Switch_Click", main, ClickArgs());
                            }
                        }
                        else CompleteUiTask(main.CompleteGoogleSignOutAsync());
                        Require(!data.GoogleCalendar.Enabled && !data.GoogleCalendar.PetsEnabled && disk.LoadData().Data.GoogleCalendar.Enabled,
                            "A failed settings save re-enabled the current session or changed the disk.");
                        Require(Control<FrameworkElement>(settings, "PersistentSaveWarningPanel").Visibility == Visibility.Visible &&
                            Control<FrameworkElement>(mini, "SettingsWarningBadge").Visibility == Visibility.Visible, "Unsaved Google settings were silent.");
                        string warning = Control<TextBlock>(settings, "PersistentSaveWarning").Text;
                        if (action == "offline-signout") Require(warning.Contains("재시작하면 다시 연동") && data.GoogleCalendar.ProtectedRefreshToken == null, "Offline unsaved sign-out did not explain the restart risk.");
                        if (action == "revoked-signout") Require(warning.Contains("권한은 해제"), "Successful revoke was reported as a failure.");
                        Call(main, "CloseInlineSettings", false);
                        settings = OpenSettingsForCheck(main);
                        Require(Control<FrameworkElement>(settings, "PersistentSaveWarningPanel").Visibility == Visibility.Visible,
                            "Reopening settings lost the unsaved preference warning.");
                        settings.SelectPage(SettingsPage.General);
                        Control<Button>(settings, "RetrySaveButton").RaiseEvent(ClickArgs());
                        Require(Control<FrameworkElement>(settings, "PersistentSaveWarningPanel").Visibility == Visibility.Visible, "Navigation or a failed retry cleared the warning.");
                        store.Reject = false;
                        Control<Button>(settings, "RetrySaveButton").RaiseEvent(ClickArgs());
                        var saved = disk.LoadData().Data.GoogleCalendar;
                        Require(!saved.Enabled && !saved.PetsEnabled && Control<FrameworkElement>(settings, "PersistentSaveWarningPanel").Visibility == Visibility.Collapsed,
                            "Retry did not persist the stop or clear its warning.");
                        if (action != "switches") Require(saved.ProtectedRefreshToken == null, "Retry did not erase the persisted credential.");
                        Require(Control<FrameworkElement>(mini, "SettingsWarningBadge").Visibility == Visibility.Collapsed, "Successful retry left a stale badge.");
                    }
                    finally
                    {
                        Call(main, "CloseInlineSettings", false); SetField(main, "_dataUnavailable", true);
                        Call(main, "MainWindow_Closed", main, EventArgs.Empty); mini.Close(); main.Close();
                    }
                }
            }
        }

        private static void CompleteUiTask(Task task)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
                Thread.Sleep(5);
            }
            Require(task.IsCompleted, "Mock UI operation did not complete.");
            task.GetAwaiter().GetResult();
        }

        private static void UpdaterPartialCopyRollback()
        {
            var updater = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ScheduleWidget.Updater.exe"));
            var type = updater.GetType("ScheduleWidget.Updater.Program+UpdateTransaction", true);
            var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(string), typeof(string), typeof(Action<string, string>) }, null);
            foreach (string mode in new[] { "existing", "new", "locked-unchanged" })
            {
                string baseDir = Path.Combine(root, "updater-partial-" + mode), appDir = Path.Combine(baseDir, "app"), staging = Path.Combine(baseDir, "staging");
                Directory.CreateDirectory(appDir); Directory.CreateDirectory(staging);
                string target = Path.Combine(appDir, "ScheduleWidget.exe");
                if (mode != "new") File.WriteAllText(target, "previous executable bytes");
                File.WriteAllText(Path.Combine(staging, "ScheduleWidget.exe"), "new executable bytes");
                File.WriteAllText(Path.Combine(appDir, "library.dll"), "previous library"); File.WriteAllText(Path.Combine(staging, "library.dll"), "new library");
                Action<string, string> copy = (src, dst) =>
                {
                    if (!dst.EndsWith("ScheduleWidget.exe", StringComparison.Ordinal)) { File.Copy(src, dst, true); return; }
                    if (mode != "locked-unchanged") File.WriteAllText(dst, "partial");
                    throw new IOException("Injected final copy failure after retry exhaustion");
                };
                var transaction = constructor.Invoke(new object[] { appDir, staging, copy });
                type.GetMethod("Backup").Invoke(transaction, new object[] { Path.Combine(baseDir, "backup") });
                using (var held = mode == "locked-unchanged" ? new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read) : null)
                {
                    bool failed = false;
                    try { type.GetMethod("Apply").Invoke(transaction, null); }
                    catch (TargetInvocationException error) when (error.InnerException is IOException) { failed = true; }
                    Require(failed && (bool)type.GetMethod("Rollback").Invoke(transaction, null), "Rollback failed or a copy failure was not exercised.");
                }
                Require(File.ReadAllText(Path.Combine(appDir, "library.dll")) == "previous library", "An earlier completed write was not restored.");
                Require(mode == "new" ? !File.Exists(target) : File.ReadAllText(target) == "previous executable bytes",
                    "The currently failing target was left partial, missing, or untracked.");
            }
        }

        private static void CancelledCharacterPublish()
        {
            string destination = Path.Combine(root, "cancelled-import"), building = destination + ".importing";
            Directory.CreateDirectory(building); File.WriteAllText(Path.Combine(building, "pet.json"), "prepared");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Throws<OperationCanceledException>(() => CharacterCatalog.PublishImport(building, destination, cancellation.Token));
                Require(!Directory.Exists(destination) && Directory.Exists(building), "A cancelled prepared import was published.");
                int version = CharacterCatalog.Version;
                Throws<OperationCanceledException>(() => CharacterCatalog.Import("never-read.zip", root, "cancelled-before-start", cancellation.Token));
                Require(CharacterCatalog.Version == version && !Directory.Exists(Path.Combine(root, "cancelled-before-start.importing")), "A cancelled import emitted a change or created staging data.");
            }
            CharacterCatalog.PublishImport(building, destination, CancellationToken.None);
            Require(Directory.Exists(destination) && !Directory.Exists(building), "An active import did not publish its prepared directory atomically.");
        }
    }
}
