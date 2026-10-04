using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ScheduleWidget
{
    internal static class EmbeddedBrowser
    {
        private static Task<CoreWebView2Environment> environment;
        private static Task<CoreWebView2Environment> musicEnvironment;
        // CoreWebView2s already set up: a page reloaded after its renderer crashed goes through InitializeAsync again, and
        // must not collect a second set of permission / navigation handlers.
        private static readonly ConditionalWeakTable<CoreWebView2, object> configured = new ConditionalWeakTable<CoreWebView2, object>();
        internal const string Origin = "https://schedulewidget.example/";

        /// <summary>
        /// Folder holding the browsers' data: WebView2 (pets, character gallery) and WebView2Music (the YouTube player, with
        /// its own autoplay options — two option sets can't share one folder). The app's data folder; the checks point it at
        /// their run folder, so they never write the real one.
        /// </summary>
        internal static string UserDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScheduleWidget");

        internal static async Task InitializeAsync(IWebView2 browser)
        {
            await InitializeAsync(browser, false);
        }

        /// <summary>
        /// The pets' browser process exited: forget its environment so the next InitializeAsync starts a new one
        /// (a finished-but-dead environment task would otherwise be reused and every new view would fail).
        /// </summary>
        internal static void ResetEnvironment() => environment = null;

        /// <summary>The same for the music player's browser process (its own environment, with autoplay allowed).</summary>
        internal static void ResetMusicEnvironment() => musicEnvironment = null;

        internal static async Task InitializeMusicAsync(IWebView2 browser)
        {
            await InitializeAsync(browser, true);
        }

        private static async Task InitializeAsync(IWebView2 browser, bool allowMusicAutoplay)
        {
            Task<CoreWebView2Environment> selectedEnvironment = allowMusicAutoplay ? musicEnvironment : environment;
            if (selectedEnvironment == null || selectedEnvironment.IsFaulted)
            {
                string userDataFolder = Path.Combine(UserDataRoot, allowMusicAutoplay ? "WebView2Music" : "WebView2");
                // HardwareMediaKeyHandling off: the hidden YouTube player does not register as a Windows media session,
                // so it never shows up as "another app" in the mini player bar (the bar already controls it directly).
                var options = allowMusicAutoplay
                    ? new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required --disable-features=HardwareMediaKeyHandling")
                    : null;
                selectedEnvironment = CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
                if (allowMusicAutoplay) musicEnvironment = selectedEnvironment;
                else environment = selectedEnvironment;
            }
            await browser.EnsureCoreWebView2Async(await selectedEnvironment);
            var core = browser.CoreWebView2;
            if (configured.TryGetValue(core, out _)) return;
            configured.Add(core, new object());
            core.SetVirtualHostNameToFolderMapping("schedulewidget.example",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Player"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            // The app's pages are not browser tabs: no DevTools (F12 / Ctrl+Shift+I could post messages as the app's own
            // origin), no browser shortcuts (Ctrl+P, F5, Ctrl+F …) and no Ctrl+wheel zoom. DevTools stay on in Debug builds.
#if !DEBUG
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.PermissionRequested += (s, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NavigationStarting += (s, e) =>
            {
                if (!e.Uri.StartsWith(Origin, StringComparison.Ordinal)) e.Cancel = true;
            };
        }
    }
}
