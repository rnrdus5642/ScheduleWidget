using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static void DemoProfileChecks()
        {
            Run("Demo profiles isolate every persistent store and disable legacy imports", () =>
            {
                string pet = CharacterCatalog.PetRoot, codex = CodexPets.RootOverride, personal = CodexPets.UserPetsSource;
                string error = ErrorLog.Root, log = PetLog.Root, browser = EmbeddedBrowser.UserDataRoot, updates = UpdateClient.UpdatesRootOverride;
                try
                {
                    CharacterCatalog.PetRoot = CodexPets.RootOverride = CodexPets.UserPetsSource = null;
                    ErrorLog.Root = PetLog.Root = EmbeddedBrowser.UserDataRoot = UpdateClient.UpdatesRootOverride = null;
                    AppProfile.Configure(Array.Empty<string>());
                    string normal = AppProfile.DataDirectory;
                    string normalData = new DataManager().DataFilePath;
                    AppProfile.Configure(new[] { "--demo" });
                    string demo = AppProfile.DataDirectory;
                    Require(demo != normal && Path.GetDirectoryName(demo) == Path.GetDirectoryName(normal), "Demo data overlaps the normal data folder.");
                    var store = new DataManager();
                    string[] paths = { store.DataFilePath, store.BackupFilePath, CharacterCatalog.PetDirectory, CodexPets.Root,
                        ErrorLog.FilePath, PetLog.FilePath, GoogleCalendarService.SavedClientFile, EmbeddedBrowser.ActiveUserDataRoot, UpdateClient.UpdatesDirectory };
                    Require(paths.All(path => path == demo || path.StartsWith(demo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)),
                        "A persisted demo resource still points outside its data folder.");
                    Require(Field(store, "legacyJsonPath") == null && CodexPets.UserPetsDirectory == null,
                        "Demo startup could import normal schedules or the personal Codex library.");
                    AppProfile.Configure(Array.Empty<string>());
                    Require(new DataManager().DataFilePath == normalData && EmbeddedBrowser.ActiveUserDataRoot == normal,
                        "Returning to normal execution did not restore the normal paths.");
                }
                finally
                {
                    AppProfile.Configure(Array.Empty<string>());
                    CharacterCatalog.PetRoot = pet; CodexPets.RootOverride = codex; CodexPets.UserPetsSource = personal;
                    ErrorLog.Root = error; PetLog.Root = log; EmbeddedBrowser.UserDataRoot = browser; UpdateClient.UpdatesRootOverride = updates;
                }
            });

            Run("Demo startup leaves existing Windows startup entries intact", () =>
            {
                string keyPath = "Software\\ScheduleWidgetChecks\\demo-startup-" + Guid.NewGuid().ToString("N");
                var startup = new StartupService(keyPath);
                try
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(keyPath)) key.SetValue("ScheduleWidget", "original-startup-command");
                    AppProfile.Configure(new[] { "--demo" });
                    Require(startup.IsStartupEnabled(out bool enabled, out _) && !enabled, "Demo reports a normal startup entry as enabled.");
                    Require(startup.TryDisableStartup(out _) && !startup.TryEnableStartup(out string warning) && !string.IsNullOrWhiteSpace(warning),
                        "Demo startup registration did not refuse an enable request.");
                    using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                        Require((string)key.GetValue("ScheduleWidget") == "original-startup-command", "Demo changed an existing startup command.");
                }
                finally { AppProfile.Configure(Array.Empty<string>()); Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); }
            });

            Run("Render recovery preserves the demo profile without changing its recovery protocol", () =>
            {
                try
                {
                    AppProfile.Configure(new[] { "--demo" });
                    var start = RenderRecovery.RecoveryStartInfo(Path.Combine(root, "ScheduleWidget.exe"), 123, 456, true);
                    Require(start.Arguments.EndsWith(" restart --demo", StringComparison.Ordinal), "A renderer restart would return to personal data.");
                    string[] parsed = AppProfile.Configure(start.Arguments.Split(' '));
                    Require(AppProfile.IsDemo && parsed.SequenceEqual(new[] { "--recover-render", "123", "456", "restart" }), "Profile parsing corrupts recovery arguments.");
                    AppProfile.Configure(Array.Empty<string>());
                    Require(!RenderRecovery.RecoveryStartInfo(Path.Combine(root, "ScheduleWidget.exe"), 123, 456, true).Arguments.Contains("--demo"),
                        "Normal recovery acquired the demo argument.");
                }
                finally { AppProfile.Configure(Array.Empty<string>()); }
            });

            Run("Demo runtime settings preserve real feature data while isolating shared desktop controls", () =>
            {
                var data = Data("A demonstration schedule");
                data.GoogleCalendar.Enabled = data.GoogleCalendar.PetsEnabled = true;
                data.GoogleCalendar.ProtectedRefreshToken = "fixture-token";
                string original = JsonConvert.SerializeObject(data);
                try
                {
                    AppProfile.Configure(Array.Empty<string>());
                    AppProfile.ApplyRuntimeSettings(data);
                    Require(JsonConvert.SerializeObject(data) == original, "Normal settings were changed by profile handling.");
                    AppProfile.Configure(new[] { "--demo" });
                    AppProfile.ApplyRuntimeSettings(data);
                    Require(!data.StartupEnabled && !data.BringToFrontHotKeyEnabled, "Demo can change shared startup or hotkey state.");
                    Require(data.Schedules.Single().Title == "A demonstration schedule" && data.GoogleCalendar.Enabled && data.GoogleCalendar.PetsEnabled &&
                        data.GoogleCalendar.ProtectedRefreshToken == "fixture-token", "Demo mode changed the actual Google or scheduling feature data.");
                }
                finally { AppProfile.Configure(Array.Empty<string>()); }
            });

            Run("Demo updates stop before accessing a staging folder or launching a helper", () =>
            {
                try
                {
                    AppProfile.Configure(new[] { "--demo" });
                    Throws<InvalidOperationException>(() => UpdateClient.StartUpdater(Path.Combine(root, "must-not-be-used")));
                }
                finally { AppProfile.Configure(Array.Empty<string>()); }
            });
        }
    }
}
