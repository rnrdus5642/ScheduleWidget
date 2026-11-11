using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private sealed class RetryDriveHandler : HttpMessageHandler
        {
            internal byte[] Package;
            internal string Failure, Modified = "2026-11-11T00:00:00Z";
            internal int Downloads, Requests;
            internal bool FailedOnce, LimitRequests;
            internal RetryDriveHandler(byte[] package) { Package = package; }
            private static HttpResponseMessage Json(JObject body) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToString()) };

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                Requests++;
                if (LimitRequests)
                {
                    if (Failure == "connection") throw new HttpRequestException("Simulated offline connection.");
                    var limited = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{}") };
                    limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(15));
                    return Task.FromResult(limited);
                }
                if (request.RequestUri.Query.Contains("alt=media"))
                {
                    Downloads++;
                    bool fail = !FailedOnce;
                    FailedOnce = true;
                    if (fail && Failure == "timeout")
                    {
                        var pending = new TaskCompletionSource<HttpResponseMessage>(); pending.SetCanceled(); return pending.Task;
                    }
                    if (fail && Failure == "server")
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") });
                    HttpContent content = fail && Failure == "stream" ? (HttpContent)new StreamContent(new FailedDriveStream(Package)) : new ByteArrayContent(Package);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                }
                if (request.RequestUri.AbsolutePath.EndsWith("/retry-folder"))
                    return Task.FromResult(Json(new JObject { ["id"] = "retry-folder", ["trashed"] = false }));
                if (request.RequestUri.Query.Contains("pageSize=10&"))
                    return Task.FromResult(Json(new JObject { ["files"] = new JArray(new JObject { ["id"] = "retry-folder" }) }));
                if (request.RequestUri.Query.Contains("pageSize=1000&"))
                    return Task.FromResult(Json(new JObject { ["files"] = new JArray(new JObject
                    {
                        ["id"] = "retry-file", ["name"] = "Retry pet.zip", ["modifiedTime"] = Modified,
                        ["appProperties"] = new JObject { ["petId"] = "retrypet", ["petName"] = "Retry pet" }
                    }) }));
                throw new InvalidOperationException("Unexpected isolated Drive request: " + request.RequestUri.AbsolutePath);
            }
        }

        private sealed class FailedDriveStream : MemoryStream
        {
            internal FailedDriveStream(byte[] bytes) : base(bytes) { }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellation) =>
                Task.FromException<int>(new IOException("Simulated interrupted response stream."));
        }

        private static byte[] RetryPetPackage()
        {
            string folder = Path.Combine(root, "retry-pet-fixture"); Directory.CreateDirectory(folder);
            string image = Path.Combine(folder, "spritesheet.png");
            using (var bitmap = new System.Drawing.Bitmap(1536, 1872))
            {
                bitmap.SetPixel(0, 0, System.Drawing.Color.Magenta);
                bitmap.Save(image, System.Drawing.Imaging.ImageFormat.Png);
            }
            string manifest = Path.Combine(folder, "pet.json");
            File.WriteAllText(manifest, new JObject { ["displayName"] = "Retry pet", ["spritesheetPath"] = "spritesheet.png", ["spriteVersionNumber"] = 1 }.ToString());
            string zip = Path.Combine(folder, "retry.zip"); CharacterCatalog.Export(CharacterCatalog.Read(manifest), zip);
            return File.ReadAllBytes(zip);
        }

        private static GoogleCalendarService RetryPetService(HttpClient client)
        {
            var service = new GoogleCalendarService(client);
            SetField(service, "accessToken", "isolated-access");
            SetField(service, "accessTokenExpires", DateTime.UtcNow.AddHours(1));
            return service;
        }

        private static void GooglePetRetryAudit()
        {
            byte[] package = RetryPetPackage();
            foreach (string failure in new[] { "timeout", "stream", "server", "import-lock" })
                Run("Drive retries unchanged characters after " + failure, () => RetryTransientPetDownload(package, failure).GetAwaiter().GetResult());
            Run("Invalid Drive packages stay cached until their remote revision changes", () => RetryInvalidPetPackage(package).GetAwaiter().GetResult());
            Run("Legacy failure records do not permanently block a valid Drive character", () => RetryLegacyPetFailure(package).GetAwaiter().GetResult());
        }

        private static async Task RetryTransientPetDownload(byte[] package, string failure)
        {
            string oldRoot = CharacterCatalog.PetRoot;
            CharacterCatalog.PetRoot = Path.Combine(root, "retry-" + failure);
            FileStream locked = null;
            try
            {
                using (var handler = new RetryDriveHandler(package) { Failure = failure })
                using (var client = new HttpClient(handler))
                {
                    var service = RetryPetService(client);
                    var settings = new GoogleCalendarSettings { PetsEnabled = true, PetDeletionsSorted = true };
                    if (failure == "import-lock")
                    {
                        string building = Path.Combine(CharacterCatalog.PetDirectory, "retrypet.importing");
                        Directory.CreateDirectory(building);
                        locked = new FileStream(Path.Combine(building, "busy.tmp"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                    }
                    bool failed = false;
                    try { failed = (await service.SyncPetsAsync(settings)).Failed > 0; }
                    catch (GoogleConnectionException) { failed = true; }
                    Require(failed && handler.Downloads == 1 && settings.FailedPetFiles.Count == 0, "A temporary failure was ignored or cached permanently.");
                    locked?.Dispose(); locked = null;
                    settings = JsonConvert.DeserializeObject<GoogleCalendarSettings>(JsonConvert.SerializeObject(settings));
                    var retried = await service.SyncPetsAsync(settings);
                    Require(retried.Downloaded == 1 && handler.Downloads == 2 && settings.FailedPetFiles.Count == 0,
                        "The same valid revision could not be retried after recovery.");
                }
            }
            finally { locked?.Dispose(); CharacterCatalog.PetRoot = oldRoot; }
        }

        private static async Task RetryInvalidPetPackage(byte[] validPackage)
        {
            string oldRoot = CharacterCatalog.PetRoot;
            CharacterCatalog.PetRoot = Path.Combine(root, "retry-invalid");
            try
            {
                using (var handler = new RetryDriveHandler(Encoding.UTF8.GetBytes("This is not a ZIP.")))
                using (var client = new HttpClient(handler))
                {
                    var service = RetryPetService(client);
                    var settings = new GoogleCalendarSettings { PetsEnabled = true, PetDeletionsSorted = true };
                    Require((await service.SyncPetsAsync(settings)).Failed == 1 && settings.FailedPetFiles.Count == 1, "A malformed ZIP was not remembered.");
                    handler.Package = validPackage;
                    await service.SyncPetsAsync(settings);
                    Require(handler.Downloads == 1, "An unchanged invalid revision was repeatedly downloaded.");
                    handler.Modified = "2026-11-11T00:01:00Z";
                    Require((await service.SyncPetsAsync(settings)).Downloaded == 1 && handler.Downloads == 2 && settings.FailedPetFiles.Count == 0,
                        "A corrected remote package was still blocked by the previous revision.");
                }
            }
            finally { CharacterCatalog.PetRoot = oldRoot; }
        }

        private static async Task RetryLegacyPetFailure(byte[] package)
        {
            string oldRoot = CharacterCatalog.PetRoot;
            CharacterCatalog.PetRoot = Path.Combine(root, "retry-legacy");
            try
            {
                using (var handler = new RetryDriveHandler(package))
                using (var client = new HttpClient(handler))
                {
                    var service = RetryPetService(client);
                    var settings = new GoogleCalendarSettings { PetsEnabled = true, PetDeletionsSorted = true };
                    settings.FailedPetFiles.Add("retry-file|" + handler.Modified);
                    Require((await service.SyncPetsAsync(settings)).Downloaded == 1 && settings.FailedPetFiles.Count == 0,
                        "An old cached timeout still prevented recovery.");
                }
            }
            finally { CharacterCatalog.PetRoot = oldRoot; }
        }

        private static void GooglePetBackoffAudit()
        {
            foreach (string failure in new[] { "rate-limit", "connection" })
                Run("Drive " + failure + " delays the next automatic request", () =>
                {
                    using (var handler = new RetryDriveHandler(new byte[0]) { LimitRequests = true, Failure = failure })
                    using (var client = new HttpClient(handler))
                    {
                        var main = new MainWindow(); ((TrayService)Field(main, "trayService")).Dispose();
                        var timer = new DispatcherTimer();
                        SetField(main, "dataStore", Store("drive-backoff-" + failure));
                        SetField(main, "appData", new AppData { StartupEnabled = false, BringToFrontHotKeyEnabled = false,
                            GoogleCalendar = new GoogleCalendarSettings { PetsEnabled = true, ProtectedRefreshToken = "isolated-placeholder" } });
                        SetField(main, "googleCalendar", RetryPetService(client)); SetField(main, "googleSoonTimer", timer);
                        try
                        {
                            Call(main, "RequestGoogleSync", true);
                            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                            while ((bool)Field(main, "googleSyncing") && DateTime.UtcNow < deadline)
                            { Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle); Thread.Sleep(5); }
                            Require(!(bool)Field(main, "googleSyncing"), "Mock Drive failure did not settle.");
                            double remaining = ((DateTime)Field(main, "googleRetryAt") - DateTime.UtcNow).TotalSeconds;
                            Require(remaining >= (failure == "rate-limit" ? 895 : 25) && (int)Field(main, "googleFailures") == 1 && timer.IsEnabled,
                                "Drive failure did not preserve its retry delay.");
                            Call(main, "RequestGoogleSync", true);
                            Require(handler.Requests == 1 && !string.IsNullOrWhiteSpace((string)Field(main, "googlePetStatus")),
                                "A scheduled request ignored the delay or the Drive warning was lost.");
                        }
                        finally { timer.Stop(); SetField(main, "_dataUnavailable", true); Call(main, "MainWindow_Closed", main, EventArgs.Empty); main.Close(); }
                    }
                });
        }
    }
}
