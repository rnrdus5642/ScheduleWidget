using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private sealed class QuotaHandler : HttpMessageHandler
        {
            internal int Requests, Writes;
            internal int Status = 429;
            internal string Reason = "rateLimitExceeded", Block = "all";
            internal TimeSpan? Retry = TimeSpan.FromHours(3);
            internal bool DateHeader, InvalidHeader;
            internal readonly Dictionary<string, JObject> Events = new Dictionary<string, JObject>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                Requests++;
                bool write = request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/events");
                if (write) Writes++;
                if (Block == "all" || Block == "writes" && write)
                {
                    var body = new JObject { ["error"] = new JObject { ["errors"] = new JArray(new JObject { ["reason"] = Reason }) } };
                    var limited = new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent(body.ToString()) };
                    if (InvalidHeader) limited.Headers.TryAddWithoutValidation("Retry-After", "not-a-date-or-duration");
                    else if (Retry.HasValue) limited.Headers.RetryAfter = DateHeader
                        ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.Add(Retry.Value)) : new RetryConditionHeaderValue(Retry.Value);
                    return limited;
                }
                if (write)
                {
                    JObject body = JObject.Parse(await request.Content.ReadAsStringAsync());
                    string id = (string)body["id"];
                    if (Events.ContainsKey(id)) return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{}") };
                    Events.Add(id, body);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToString()) };
                }
                if (request.RequestUri.AbsolutePath.EndsWith("/events"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JObject { ["items"] = new JArray(Events.Values) }.ToString()) };
                throw new InvalidOperationException("Unexpected isolated quota request: " + request.RequestUri.AbsolutePath);
            }
        }

        private static void GoogleQuotaHttpAudit()
        {
            Run("Quota backoff grows, spreads retries and preserves long Retry-After deadlines", () =>
            {
                TimeSpan previous = TimeSpan.Zero;
                for (int attempt = 1; attempt <= 10; attempt++)
                {
                    TimeSpan low = GoogleSyncRetryPolicy.Backoff(attempt, true, false, null, 0);
                    TimeSpan high = GoogleSyncRetryPolicy.Backoff(attempt, true, false, null, 1);
                    Require(low >= previous && high > low && high <= TimeSpan.FromMinutes(19), "Quota retry timing regressed or has no jitter.");
                    previous = low;
                }
                Require(GoogleSyncRetryPolicy.Backoff(1, true, false, TimeSpan.FromHours(6), 0) >= TimeSpan.FromHours(6), "Google's six-hour wait was shortened.");
                Require(GoogleSyncRetryPolicy.Backoff(1, true, true, null, 0) >= TimeSpan.FromHours(1), "Daily quota was treated as a short burst.");
                DateTime now = DateTime.UtcNow;
                Require(GoogleSyncRetryPolicy.AddDelay(now, TimeSpan.MaxValue).Ticks == DateTime.MaxValue.Ticks, "An extreme server delay overflowed.");
                Require(GoogleSyncRetryPolicy.TimerInterval(TimeSpan.FromDays(90)) <= TimeSpan.FromHours(1), "A long server wait overflowed the native timer.");
                Require(GoogleSyncRetryPolicy.StartupDelay(0) < GoogleSyncRetryPolicy.StartupDelay(1) &&
                    GoogleSyncRetryPolicy.PeriodicDelay(0) >= TimeSpan.FromMinutes(4.5) &&
                    GoogleSyncRetryPolicy.PeriodicDelay(1) <= TimeSpan.FromMinutes(5.5), "Normal polling is not spread around five minutes.");
            });

            foreach (string endpoint in new[] { "calendar", "drive", "token" })
                Run("Google " + endpoint + " 429 preserves credentials and Retry-After", () =>
                    QuotaHttpCase(endpoint, 429, "rateLimitExceeded", false, false).GetAwaiter().GetResult());
            Run("Google 403 daily quota is distinct from missing permission", () =>
                QuotaHttpCase("calendar", 403, "dailyLimitExceeded", false, false).GetAwaiter().GetResult());
            Run("Google quota accepts a Retry-After date", () =>
                QuotaHttpCase("calendar", 403, "userRateLimitExceeded", true, false).GetAwaiter().GetResult());
            Run("Malformed Retry-After still preserves the quota classification", () =>
                QuotaHttpCase("calendar", 429, "rateLimitExceeded", false, true).GetAwaiter().GetResult());
            Run("Google permission failures do not become quota waits", () =>
                QuotaHttpCase("calendar", 403, "ACCESS_TOKEN_SCOPE_INSUFFICIENT", false, false).GetAwaiter().GetResult());
        }

        private static async Task QuotaHttpCase(string endpoint, int status, string reason, bool dateHeader, bool invalidHeader)
        {
            var oldClient = GoogleCalendarService.ClientOverride;
            GoogleCalendarService.ClientOverride = Tuple.Create("quota-check-client", "quota-check-secret");
            try
            {
                using (var handler = new QuotaHandler { Status = status, Reason = reason, DateHeader = dateHeader, InvalidHeader = invalidHeader })
                using (var client = new HttpClient(handler))
                {
                    var service = endpoint == "token" ? new GoogleCalendarService(client) : RetryPetService(client);
                    var settings = new GoogleCalendarSettings { Enabled = true, PetsEnabled = true, ProtectedRefreshToken = SecretStore.Protect("quota-refresh") };
                    string refresh = settings.ProtectedRefreshToken;
                    Exception failure = null;
                    try
                    {
                        if (endpoint == "drive") await service.SyncPetsAsync(settings);
                        else await service.SyncAsync(settings, new List<ScheduleItem>());
                    }
                    catch (Exception error) { failure = error; }
                    Require(handler.Requests == 1 && settings.ProtectedRefreshToken == refresh, "A denied request retried immediately or discarded login information.");
                    if (reason == "ACCESS_TOKEN_SCOPE_INSUFFICIENT")
                    {
                        Require(failure is GoogleScopeException, "Missing permission was classified as a quota limit.");
                        return;
                    }
                    var quota = failure as GoogleRateLimitException;
                    Require(quota != null, "Quota response did not reach the backoff controller.");
                    Require(quota.IsDailyLimit == reason.StartsWith("dailyLimit", StringComparison.Ordinal), "Daily quota classification was lost.");
                    Require(invalidHeader ? !quota.RetryAfter.HasValue : quota.RetryAfter >= TimeSpan.FromHours(3) - TimeSpan.FromSeconds(2),
                        "Server retry delay was lost or shortened.");
                }
            }
            finally { GoogleCalendarService.ClientOverride = oldClient; }
        }

        private static MainWindow QuotaWindow(AppData data, HttpClient client, string storeName, out DataManager store, out DispatcherTimer timer)
        {
            var main = new MainWindow(); ((TrayService)Field(main, "trayService")).Dispose();
            store = Store(storeName);
            SetField(main, "dataStore", store); SetField(main, "appData", data);
            SetField(main, "googleCalendar", RetryPetService(client));
            timer = new DispatcherTimer(); SetField(main, "googleSoonTimer", timer);
            return main;
        }

        private static void CloseQuotaWindow(MainWindow main, DispatcherTimer timer)
        {
            timer.Stop(); SetField(main, "_dataUnavailable", true);
            Call(main, "MainWindow_Closed", main, EventArgs.Empty); main.Close();
        }

        private static void SettleQuotaWindow(MainWindow main)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while ((bool)Field(main, "googleSyncing") && DateTime.UtcNow < deadline)
            { Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle); Thread.Sleep(5); }
            Require(!(bool)Field(main, "googleSyncing"), "Isolated quota request did not settle.");
        }

        private static void GoogleQuotaUiAudit()
        {
            Run("Quota cooldown survives manual sync, feature toggles and profile reload", () =>
            {
                var data = new AppData { StartupEnabled = false, BringToFrontHotKeyEnabled = false,
                    GoogleCalendar = new GoogleCalendarSettings { Enabled = true, ProtectedRefreshToken = "quota-placeholder" } };
                using (var handler = new QuotaHandler())
                using (var client = new HttpClient(handler))
                {
                    var main = QuotaWindow(data, client, "quota-cooldown", out DataManager store, out DispatcherTimer timer);
                    try
                    {
                        Call(main, "RequestGoogleSync", true); SettleQuotaWindow(main);
                        Require(handler.Requests == 1 && data.GoogleCalendar.RateLimitRetryAtUtc > DateTime.UtcNow.AddHours(2.9), "The full server deadline was not retained.");
                        DateTime deadline = data.GoogleCalendar.RateLimitRetryAtUtc.Value;
                        Call(main, "GoogleSyncNow_Click", main, new RoutedEventArgs());
                        main.GoogleCalendarSwitch.IsChecked = false; Call(main, "GoogleCalendarSwitch_Click", main, new RoutedEventArgs());
                        main.GoogleCalendarSwitch.IsChecked = true; Call(main, "GoogleCalendarSwitch_Click", main, new RoutedEventArgs());
                        main.GooglePetsSwitch.IsChecked = true; Call(main, "GooglePetsSwitch_Click", main, new RoutedEventArgs());
                        main.GooglePetsSwitch.IsChecked = false; Call(main, "GooglePetsSwitch_Click", main, new RoutedEventArgs());
                        Call(main, "RunGoogleSync");
                        Require(handler.Requests == 1 && data.GoogleCalendar.RateLimitRetryAtUtc == deadline && timer.IsEnabled,
                            "A manual action bypassed or extended the quota deadline.");
                        Require(!main.GoogleSyncNowButton.IsEnabled && main.GoogleCalendarStatus.Text.Contains("자동"), "Quota wait is not visible in settings.");
                        var reloaded = store.LoadData().Data;
                        Require(reloaded.GoogleCalendar.RateLimitRetryAtUtc == deadline && reloaded.GoogleCalendar.RateLimitFailures == 1,
                            "Quota state was not persisted with the profile.");
                        var restored = QuotaWindow(reloaded, client, "quota-reloaded", out DataManager ignored, out DispatcherTimer restoredTimer);
                        try
                        {
                            Call(restored, "RequestGoogleSync", true);
                            Call(restored, "GoogleSyncNow_Click", restored, new RoutedEventArgs());
                            Require(handler.Requests == 1 && restoredTimer.IsEnabled, "Restart bypassed the persisted quota cooldown.");
                        }
                        finally { CloseQuotaWindow(restored, restoredTimer); }
                    }
                    finally { CloseQuotaWindow(main, timer); }
                }
            });

            Run("Locally edited schedules survive quota failures and upload after recovery", () =>
            {
                var data = new AppData { StartupEnabled = false, BringToFrontHotKeyEnabled = false,
                    GoogleCalendar = new GoogleCalendarSettings { Enabled = true, ProtectedRefreshToken = "quota-placeholder" } };
                var item = new ScheduleItem { Title = "Pending quota event", Period = DateTime.Today.ToString("yyyy-MM-dd") };
                data.Schedules.Add(item);
                using (var handler = new QuotaHandler { Block = "writes" })
                using (var client = new HttpClient(handler))
                {
                    var main = QuotaWindow(data, client, "quota-local-edit", out DataManager store, out DispatcherTimer timer);
                    try
                    {
                        store.SaveData(data);
                        Call(main, "RequestGoogleSync", true); SettleQuotaWindow(main);
                        Require(handler.Writes == 1 && data.GoogleCalendar.RateLimitRetryAtUtc.HasValue, "The write was not held by a quota response.");
                        string pendingId = item.GoogleEventId;
                        item.Title = "Edited while quota limited";
                        Call(main, "SaveDataSafely", false);
                        Call(main, "GoogleSyncNow_Click", main, new RoutedEventArgs());
                        var saved = store.LoadData().Data;
                        Require(saved.Schedules.Count == 1 && saved.Schedules[0].Title == item.Title && handler.Writes == 1,
                            "A local edit was lost or triggered a write during cooldown.");
                        handler.Block = "none";
                        data.GoogleCalendar.RateLimitRetryAtUtc = DateTime.UtcNow.AddSeconds(-1);
                        SetField(main, "googleRetryAt", DateTime.MinValue);
                        Call(main, "RequestGoogleSync", true); SettleQuotaWindow(main);
                        Require(handler.Events.Count == 1 && handler.Events.ContainsKey(pendingId) &&
                            (string)handler.Events[pendingId]["summary"] == item.Title, "Recovery lost the pending event identity or its latest edit.");
                        Require(!data.GoogleCalendar.RateLimitRetryAtUtc.HasValue && data.GoogleCalendar.RateLimitFailures == 0 &&
                            main.GoogleSyncNowButton.IsEnabled, "Successful recovery did not clear the quota wait.");
                        Require(store.LoadData().Data.Schedules.Single().Title == item.Title, "Recovered local data was not preserved.");
                    }
                    finally { CloseQuotaWindow(main, timer); }
                }
            });
        }
    }
}
