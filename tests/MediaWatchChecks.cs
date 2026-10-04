using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private sealed class TestSubscription : IDisposable
        {
            public int Disposals;
            public void Dispose() { Disposals++; }
        }
        private sealed class TestVolumeWatch : AppVolumeService.IVolumeWatch
        {
            private readonly Action<float?> changed;
            public float? CurrentVolume { get; private set; } = .4f;
            public int Disposals;
            public TestVolumeWatch(Action<float?> changed) { this.changed = changed; }
            public void Emit(float? value) { CurrentVolume = value; changed(value); }
            public void Dispose() { Disposals++; }
        }
        private static Task MediaTask(MainWindow main, string method) =>
            (Task)typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(main, null);

        private static MainWindow MediaWatchHost(string folder)
        {
            var main = new MainWindow();
            ((TrayService)Field(main, "trayService")).Dispose();
            SetField(main, "appData", new AppData { StartupEnabled = false, BringToFrontHotKeyEnabled = false, MiniPlayerVisible = true });
            SetField(main, "dataStore", Store(folder));
            return main;
        }

        private static void EventDrivenMediaWatch()
        {
            var main = MediaWatchHost("media-event-watch");
            var controls = (IMusicControls)main;
            Action<MediaChangeKind> notify = null;
            var subscriptions = new List<TestSubscription>();
            var volumes = new List<TestVolumeWatch>();
            int snapshots = 0, timelines = 0;
            bool appAvailable = true;
            SetField(main, "mediaWatchFactoryOverride", (Func<Action<MediaChangeKind>, Task<IDisposable>>)(callback =>
            {
                notify = callback;
                var subscription = new TestSubscription(); subscriptions.Add(subscription);
                return Task.FromResult<IDisposable>(subscription);
            }));
            SetField(main, "mediaSourcesOverride", (Func<Task<List<SystemMediaService.NowPlaying>>>)(() =>
            {
                snapshots++;
                if (!appAvailable) return Task.FromResult(new List<SystemMediaService.NowPlaying>());
                return Task.FromResult(new List<SystemMediaService.NowPlaying> { Media("Chrome", "Event track") });
            }));
            SetField(main, "mediaTimelineOverride", (Func<List<SystemMediaService.NowPlaying>, bool>)(sources =>
            {
                timelines++; sources[0].Position = TimeSpan.FromSeconds(50); return true;
            }));
            SetField(main, "volumeWatchFactoryOverride", (Func<string, Action<float?>, Task<AppVolumeService.IVolumeWatch>>)((app, callback) =>
            {
                var volume = new TestVolumeWatch(callback); volumes.Add(volume);
                return Task.FromResult<AppVolumeService.IVolumeWatch>(volume);
            }));
            try
            {
                MediaTask(main, "StartExternalMediaWatchAsync").GetAwaiter().GetResult();
                MediaTask(main, "FlushMediaRefreshAsync").GetAwaiter().GetResult();
                Require(snapshots == 1 && volumes.Count == 1 && controls.SelectedExternal == "Chrome", "Initial event watch did not take one snapshot.");
                Require(!(Field(main, "mediaRefreshTimer") as DispatcherTimer).IsEnabled && Field(main, "mediaSubscriptionRetry") == null, "Healthy watching left a polling timer active.");
                for (int i = 0; i < 20; i++) notify(MediaChangeKind.Metadata);
                Require(snapshots == 1, "Each notification performed an immediate full read.");
                MediaTask(main, "FlushMediaRefreshAsync").GetAwaiter().GetResult();
                Require(snapshots == 2 && volumes.Count == 1, "A burst was not coalesced or re-enumerated audio sessions.");
                notify(MediaChangeKind.Timeline);
                MediaTask(main, "FlushMediaRefreshAsync").GetAwaiter().GetResult();
                Require(timelines == 1 && snapshots == 2 && volumes.Count == 1, "A timeline event re-read metadata or audio sessions.");
                volumes[0].Emit(.7f);
                for (int i = 0; i < 100; i++) Require(Math.Abs(controls.Volume - .7) < .001, "Cached volume getter missed its notification.");
                Require(snapshots == 2 && volumes.Count == 1, "Reading volume caused a new query.");
                var oldCallback = notify;
                Call(main, "StopExternalMediaWatch", false);
                Require(subscriptions[0].Disposals == 1 && volumes[0].Disposals == 1, "Hiding did not release OS subscriptions.");
                oldCallback(MediaChangeKind.All);
                volumes[0].Emit(.2f);
                Require((MediaChangeKind)Field(main, "pendingMediaChanges") == MediaChangeKind.None && Math.Abs(controls.Volume - .7) < .001, "Late disposed notifications changed state.");
                MediaTask(main, "StartExternalMediaWatchAsync").GetAwaiter().GetResult();
                MediaTask(main, "FlushMediaRefreshAsync").GetAwaiter().GetResult();
                Require(subscriptions.Count == 2 && snapshots == 3 && volumes.Count == 2, "Showing did not restore one fresh subscription and snapshot.");
                appAvailable = false;
                notify(MediaChangeKind.Sessions);
                MediaTask(main, "FlushMediaRefreshAsync").GetAwaiter().GetResult();
                Require(controls.SelectedExternal == "Chrome" && ((DispatcherTimer)Field(main, "mediaRefreshTimer")).IsEnabled, "A removed session has no single grace-period retry.");
                SetField(Field(main, "automaticMediaSource"), "missingSince", DateTime.UtcNow.AddSeconds(-6));
                MediaTask(main, "FlushMediaRefreshAsync").GetAwaiter().GetResult();
                Require(controls.SelectedExternal == null && !((DispatcherTimer)Field(main, "mediaRefreshTimer")).IsEnabled, "A removed session left the auto source stuck or started polling.");
            }
            finally { Call(main, "StopExternalMediaWatch", true); }
        }

        private static void LateMediaSubscription()
        {
            var main = MediaWatchHost("media-late-watch");
            var pending = new TaskCompletionSource<IDisposable>();
            var subscription = new TestSubscription();
            SetField(main, "mediaWatchFactoryOverride", (Func<Action<MediaChangeKind>, Task<IDisposable>>)(_ => pending.Task));
            var opening = MediaTask(main, "StartExternalMediaWatchAsync");
            Call(main, "StopExternalMediaWatch", true);
            pending.SetResult(subscription);
            opening.GetAwaiter().GetResult();
            Require(subscription.Disposals == 1 && Field(main, "mediaSubscription") == null &&
                (MediaChangeKind)Field(main, "pendingMediaChanges") == MediaChangeKind.None, "A late subscription restarted hidden work.");
        }

        private static void VolumeEventCallbacks()
        {
            int changes = 0, disconnected = 0;
            float heard = -1;
            var callback = new AppVolumeService.VolumeEvents((value, mute, own) => { changes++; heard = AppVolumeService.Audible(value, mute); }, () => disconnected++);
            callback.OnSimpleVolumeChanged(.6f, false, IntPtr.Zero);
            Require(changes == 1 && Math.Abs(heard - .6) < .001, "Volume event value was lost.");
            callback.OnSimpleVolumeChanged(.6f, true, IntPtr.Zero);
            Require(heard == 0, "Mute event did not report silence.");
            callback.OnStateChanged(0);
            Require(disconnected == 0, "Pausing expired an audio subscription.");
            callback.OnStateChanged(2);
            Require(disconnected == 1, "An expired session did not notify its owner.");
            callback.Deactivate();
            callback.OnSimpleVolumeChanged(.2f, false, IntPtr.Zero);
            callback.OnSessionDisconnected(0);
            Require(changes == 2 && disconnected == 1, "Released audio callbacks remained active.");
        }
    }
}
