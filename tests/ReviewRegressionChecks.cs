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
using System.Windows.Threading;
using Newtonsoft.Json.Linq;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private sealed class DeferredGoogleHandler : HttpMessageHandler
        {
            internal readonly TaskCompletionSource<bool> Pending = new TaskCompletionSource<bool>();
            internal readonly TaskCompletionSource<HttpResponseMessage> Reply = new TaskCompletionSource<HttpResponseMessage>();
            internal CancellationToken RequestCancellation;
            internal string Hold = "read";
            internal int Reads;
            private static HttpResponseMessage Answer(string json) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            internal void Release(string json) => Reply.SetResult(Answer(json));
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                string path = request.RequestUri.AbsolutePath;
                if (path == "/revoke") return Task.FromResult(Answer("{}"));
                bool hold = path == "/token" && Hold == "token" || path.EndsWith("/files") && Hold == "drive";
                if (path.EndsWith("/events")) hold = ++Reads == 2 && Hold == "read";
                if (hold)
                {
                    RequestCancellation = cancellation; Pending.TrySetResult(true);
                    return Reply.Task; // Deliberately ignores cancellation: stale successful replies must still be rejected.
                }
                if (path.EndsWith("/events")) return Task.FromResult(Answer("{\"items\":[]}"));
                throw new InvalidOperationException("Unexpected mock Google request: " + path);
            }
        }

        private static async Task AwaitGoogleRequest(Task signal)
        {
            if (await Task.WhenAny(signal, Task.Delay(5000)) != signal) throw new TimeoutException("Mock request did not arrive.");
            await signal;
        }

        private static void GoogleCancellationRegression()
        {
            foreach (string stage in new[] { "read", "token", "drive" })
                foreach (bool signOut in new[] { false, true })
                    Run("Google " + stage + " rejects late responses after " + (signOut ? "sign-out" : "disabling sync"), () =>
                        GoogleCancellationCase(stage, signOut).GetAwaiter().GetResult());
        }

        private static async Task GoogleCancellationCase(string stage, bool signOut)
        {
            var previousClient = GoogleCalendarService.ClientOverride;
            GoogleCalendarService.ClientOverride = Tuple.Create("checks-client", "checks-secret");
            try
            {
                using (var handler = new DeferredGoogleHandler { Hold = stage })
                using (var client = new HttpClient(handler))
                {
                    var service = new GoogleCalendarService(client);
                    var settings = new GoogleCalendarSettings { Enabled = true, PetsEnabled = true,
                        Account = "checks@example.invalid", ProtectedRefreshToken = SecretStore.Protect("checks-refresh") };
                    if (stage != "token")
                    {
                        SetField(service, "accessToken", "checks-access");
                        SetField(service, "accessTokenExpires", DateTime.UtcNow.AddHours(1));
                    }
                    var schedules = new List<ScheduleItem>();
                    Task running = stage == "drive" ? (Task)service.SyncPetsAsync(settings) : service.SyncAsync(settings, schedules);
                    await AwaitGoogleRequest(handler.Pending.Task);
                    if (signOut) await service.SignOutAsync(settings);
                    else { settings.Enabled = settings.PetsEnabled = false; service.CancelPendingOperations(); }
                    Require(handler.RequestCancellation.IsCancellationRequested, "Cancellation was not passed to the HTTP request.");
                    string savedRefresh = settings.ProtectedRefreshToken;
                    object savedAccess = Field(service, "accessToken");
                    if (stage == "read")
                    {
                        // A new operation can finish while the old HTTP handler still has its response queued.
                        SetField(service, "accessToken", "new-session-access");
                        SetField(service, "accessTokenExpires", DateTime.UtcNow.AddHours(1));
                        await service.SyncAsync(settings, schedules);
                        savedAccess = Field(service, "accessToken");
                    }
                    DateTime day = DateTime.Today.AddDays(70);
                    string response = stage == "token" ? "{\"access_token\":\"stale-access\",\"refresh_token\":\"stale-refresh\",\"expires_in\":3600}"
                        : stage == "drive" ? "{\"files\":[{\"id\":\"stale-folder\"}]}"
                        : new JObject { ["items"] = new JArray(new JObject { ["id"] = "stale-event", ["summary"] = "Late event",
                            ["start"] = new JObject { ["date"] = day.ToString("yyyy-MM-dd") },
                            ["end"] = new JObject { ["date"] = day.AddDays(1).ToString("yyyy-MM-dd") } }) }.ToString();
                    var lastSync = settings.LastSync;
                    handler.Release(response);
                    bool cancelled = false;
                    try { await running; } catch (OperationCanceledException) { cancelled = true; }
                    Require(cancelled, "A cancelled operation completed successfully.");
                    Require(schedules.Count == 0 && settings.ProtectedRefreshToken == savedRefresh && Equals(Field(service, "accessToken"), savedAccess),
                        "A stale operation changed schedules or credentials.");
                    Require(settings.LastSync == lastSync && Field(service, "petFolderId") == null,
                        "A stale operation changed session metadata.");
                }
            }
            finally { GoogleCalendarService.ClientOverride = previousClient; }
        }

        private sealed class ReminderAcceptHandler : HttpMessageHandler
        {
            internal int Calls;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                Calls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result_code\":0}") });
            }
        }

        private static void ReminderLengthRegression()
        {
            foreach (string title in new[] { new string('A', 150), new string('가', 250), string.Concat(Enumerable.Repeat("😊가\u0301", 100)) })
            {
                var item = new ScheduleItem { Title = title, Period = DateTime.Today.ToString("yyyy-MM-dd"),
                    EndPeriod = DateTime.Today.AddDays(3).ToString("yyyy-MM-dd"), Time = "09:00" };
                string message = FeatureRules.ReminderMessage(item, CommunicationService.KakaoMessageLimit);
                Require(message.Length <= 200 && message.Contains("09:00") && message.Contains(item.EndPeriod) && message.Contains(item.Period),
                    "The channel limit dropped the deadline or left an oversized message.");
                for (int i = 0; i < message.Length; i++)
                    if (char.IsSurrogate(message[i])) { Require(char.IsSurrogatePair(message, i), "A title was cut inside an emoji."); i++; }
                using (var handler = new ReminderAcceptHandler())
                using (var client = new HttpClient(handler))
                {
                    new CommunicationService(client).SendKakaoAsync(new CommunicationSettings { ProtectedKakaoToken = SecretStore.Protect("checks-access"),
                        KakaoLinkUrl = "https://example.invalid/" }, message, true).GetAwaiter().GetResult();
                    Require(handler.Calls == 1, "The formatted reminder was rejected before sending.");
                }
            }
        }

        private sealed class RetryScheduleStore : IAppDataStore
        {
            internal bool Reject = true;
            private readonly IAppDataStore disk;
            internal RetryScheduleStore(IAppDataStore disk) { this.disk = disk; }
            public DataLoadResult LoadData() => disk.LoadData();
            public void SaveData(AppData data)
            {
                if (Reject) throw new DataStorageException("Checks disk failure", new IOException());
                disk.SaveData(data);
            }
        }

        private static void ScheduleSaveRetryRegression()
        {
            var main = new MainWindow();
            ((TrayService)Field(main, "trayService")).Dispose();
            var data = new AppData { MiniCharacterVisible = false, StartupEnabled = false, BringToFrontHotKeyEnabled = false };
            var store = new RetryScheduleStore(Store("schedule-save-retry"));
            SetField(main, "appData", data); SetField(main, "dataStore", store);
            try
            {
                var input = Control<TextBox>(main, "TitleInput"); input.Text = "보존할 입력";
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    Call(main, "AddButton_Click", main, ClickArgs());
                    Require(data.Schedules.Count == 0 && input.Text == "보존할 입력" && Control<TextBlock>(main, "AddSaveError").Visibility == Visibility.Visible,
                        "A failed add lost its draft or left a duplicate/unsaved schedule in live data.");
                }
                store.Reject = false; Call(main, "AddButton_Click", main, ClickArgs());
                Require(data.Schedules.Count == 1 && store.LoadData().Data.Schedules.Count == 1 && input.Text.Length == 0, "Retry did not save exactly one schedule.");
                var item = data.Schedules.Single();
                Call(main, "OpenInlineEdit", item);
                Control<TextBox>(main, "InlineEditTitleInput").Text = "수정 초안";
                store.Reject = true; Call(main, "InlineEditSaveButton_Click", main, ClickArgs());
                Require(item.Title == "보존할 입력" && Control<TextBox>(main, "InlineEditTitleInput").Text == "수정 초안" &&
                    Control<FrameworkElement>(main, "InlineEditPanel").Visibility == Visibility.Visible && Field(main, "_inlineEditId") != null,
                    "Failed edit changed live data or closed the editor.");
                Call(main, "InlineEditCancelButton_Click", main, ClickArgs());
                Require(item.Title == "보존할 입력", "Cancelling a failed edit retained unsaved changes.");
                Call(main, "OpenInlineEdit", item); Control<TextBox>(main, "InlineEditTitleInput").Text = "저장한 수정";
                store.Reject = false; Call(main, "InlineEditSaveButton_Click", main, ClickArgs());
                Require(store.LoadData().Data.Schedules.Single().Title == "저장한 수정" &&
                    Control<FrameworkElement>(main, "InlineEditPanel").Visibility == Visibility.Collapsed, "A successful retry did not close and persist the edit.");
                Require(!main.IsVisible, "Retry checks displayed a desktop window.");
            }
            finally { SetField(main, "_dataUnavailable", true); main.Close(); }
        }

        private static void AuxiliaryPlacementRegression()
        {
            var window = new Window { Width = 1000, Height = 760, MinWidth = 760, MinHeight = 500 };
            var placement = new AuxiliaryWindowPlacement(window);
            try
            {
                var small = new Rect(-640, -100, 640, 480);
                placement.Apply(small, new Rect(-300, 0, 100, 100), true);
                Rect bounds() => new Rect(window.Left, window.Top, window.Width, window.Height);
                Require(small.Contains(bounds()) && window.Height == 448 && window.MinHeight <= 448, "Small secondary monitor clipped the dialog.");
                var larger = new Rect(0, 0, 1920, 1040);
                placement.Apply(larger, bounds(), false);
                Require(larger.Contains(bounds()) && window.Width == 1000 && window.Height == 760, "A temporary fit replaced the preferred dialog size.");
                // Work areas already expressed in the destination monitor's DIP coordinates, including 150% scaling.
                var scaled = new Rect(1280, 0, 1280d / 1.5, 720d / 1.5);
                placement.Apply(scaled, bounds(), false);
                Require(scaled.Contains(bounds()), "Moving to another DPI left the dialog outside its work area.");
                window.Close(); var closed = bounds(); placement.Apply(larger, closed, false);
                Require(bounds() == closed, "A late display event repositioned a closed editor.");
            }
            finally { window.Close(); }
        }

        private static void AuxiliaryEditorLayoutRegression()
        {
            var settings = new SettingsWindow();
            var music = new MusicWindow(new MusicSettings(), () => true, () => { }, embedded: true);
            var editor = music.CreatePlaylistEditor(true);
            try
            {
                settings.SetPage(SettingsPage.Music, music.TakeSettingsContent(settings));
                settings.SelectPage(SettingsPage.Music);
                foreach (var window in new Window[] { settings, editor })
                {
                    var placement = (AuxiliaryWindowPlacement)Field(window, "placement");
                    placement.Apply(new Rect(-640, 0, 640, 480), Rect.Empty, true);
                    var content = (FrameworkElement)window.Content;
                    var size = new Size(window.Width, window.Height);
                    content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                    string[] actions = window == settings ? new[] { "SettingsSaveButton", "SettingsDismissButton" } : new[] { "SaveButton" };
                    foreach (string name in actions)
                    {
                        var button = Control<Button>(window, name);
                        var bounds = button.TransformToAncestor(content).TransformBounds(new Rect(button.RenderSize));
                        Require(bounds.Width > 0 && bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= size.Width + 1 && bounds.Bottom <= size.Height + 1,
                            name + " is clipped on a small secondary monitor.");
                    }
                    Require(!window.IsVisible, "Layout checks showed a desktop editor.");
                }
            }
            finally
            {
                settings.SetPage(SettingsPage.Music, new Grid()); music.ReturnSettingsContent(keepPlaying: false);
                editor.Close(); settings.Close(); music.Close();
            }
        }

        private static void GoogleWindowCancellationRegression()
        {
            foreach (bool closing in new[] { false, true })
            {
                var main = new MainWindow();
                ((TrayService)Field(main, "trayService")).Dispose();
                var data = new AppData { MiniCharacterVisible = false, StartupEnabled = false, BringToFrontHotKeyEnabled = false };
                data.GoogleCalendar.Enabled = true;
                data.GoogleCalendar.ProtectedRefreshToken = SecretStore.Protect("checks-refresh");
                SetField(main, "appData", data); SetField(main, "dataStore", Store("google-cancel-window-" + closing));
                var previousContext = SynchronizationContext.Current;
                using (var handler = new DeferredGoogleHandler())
                using (var client = new HttpClient(handler))
                {
                    var service = new GoogleCalendarService(client);
                    SetField(service, "accessToken", "checks-access"); SetField(service, "accessTokenExpires", DateTime.UtcNow.AddHours(1));
                    SetField(main, "googleCalendar", service);
                    try
                    {
                        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(main.Dispatcher));
                        Call(main, "RunGoogleSync");
                        Require(handler.Pending.Task.Wait(5000), "Window sync did not reach the mocked read.");
                        if (closing) { SetField(main, "_dataUnavailable", true); main.PrepareForExit(); }
                        else
                        {
                            Control<System.Windows.Controls.Primitives.ToggleButton>(main, "GoogleCalendarSwitch").IsChecked = false;
                            Call(main, "GoogleCalendarSwitch_Click", main, ClickArgs());
                        }
                        Require(handler.RequestCancellation.IsCancellationRequested, "The window action did not cancel its running sync (closing=" + closing + ").");
                        handler.Release("{\"items\":[]}");
                        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                        while ((bool)Field(main, "googleSyncing") && DateTime.UtcNow < deadline)
                        {
                            main.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
                            Thread.Sleep(5);
                        }
                        Require(!(bool)Field(main, "googleSyncing") && (int)Field(main, "googleFailures") == 0 && Field(main, "googleLastError") == null,
                            "Cancellation was reported as an error or left syncing stuck.");
                        Require(data.GoogleCalendar.LastSync == null, "A window's cancelled sync updated its completion state.");
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(previousContext);
                        SetField(main, "_dataUnavailable", true);
                        Call(main, "MainWindow_Closed", main, EventArgs.Empty);
                        main.Close();
                    }
                }
            }
        }
    }
}
