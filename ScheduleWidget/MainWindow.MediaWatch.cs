using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ScheduleWidget
{
    public partial class MainWindow
    {
        private IDisposable mediaSubscription;
        private bool mediaWatchActive, mediaRefreshRunning;
        private int mediaWatchGeneration;
        private MediaChangeKind pendingMediaChanges;
        private int pendingMediaDelay = 100, mediaReadFailures;
        private DispatcherTimer mediaRefreshTimer, mediaSubscriptionRetry, volumeRetryTimer;
        private System.Windows.Controls.Primitives.Popup watchedSourcePopup, watchedVolumePopup;
        private Func<Action<MediaChangeKind>, Task<IDisposable>> mediaWatchFactoryOverride = null;
        private Func<List<SystemMediaService.NowPlaying>, bool> mediaTimelineOverride = null;
        private Func<string, Action<float?>, Task<AppVolumeService.IVolumeWatch>> volumeWatchFactoryOverride = null;

        private async void StartExternalMediaWatch()
        {
            try { await StartExternalMediaWatchAsync(); }
            catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
        }

        private async Task StartExternalMediaWatchAsync()
        {
            if (closingApp || mediaWatchActive) return;
            mediaWatchActive = true;
            int generation = ++mediaWatchGeneration;
            WatchMiniMusicPopups();
            await AttachMediaSubscriptionAsync(generation);
            if (mediaWatchActive && generation == mediaWatchGeneration) QueueMediaRefresh(MediaChangeKind.All, 0);
        }

        private async Task AttachMediaSubscriptionAsync(int generation)
        {
            IDisposable subscription = null;
            try
            {
                var factory = mediaWatchFactoryOverride ?? SystemMediaService.WatchAsync;
                subscription = await factory(kind => ExternalMediaChanged(generation, kind));
            }
            catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
            if (!mediaWatchActive || generation != mediaWatchGeneration) { subscription?.Dispose(); return; }
            mediaSubscription = subscription;
            if (subscription != null) { mediaSubscriptionRetry?.Stop(); return; }
            // Unsupported/unavailable notifications: a slow recovery attempt, never a normal polling loop.
            if (mediaSubscriptionRetry == null)
            {
                mediaSubscriptionRetry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                mediaSubscriptionRetry.Tick += async (s, e) =>
                {
                    mediaSubscriptionRetry.Stop();
                    if (!mediaWatchActive || mediaSubscription != null) return;
                    int current = mediaWatchGeneration;
                    await AttachMediaSubscriptionAsync(current);
                    if (mediaWatchActive && current == mediaWatchGeneration) QueueMediaRefresh(MediaChangeKind.All, 0);
                };
            }
            mediaSubscriptionRetry.Start();
        }

        private void ExternalMediaChanged(int generation, MediaChangeKind kind)
        {
            if (!mediaWatchActive || generation != mediaWatchGeneration || Dispatcher.HasShutdownStarted) return;
            if (Dispatcher.CheckAccess()) QueueMediaRefresh(kind, 100);
            else Dispatcher.BeginInvoke(new Action(() =>
            {
                if (mediaWatchActive && generation == mediaWatchGeneration) QueueMediaRefresh(kind, 100);
            }), DispatcherPriority.Background);
        }

        private void StopExternalMediaWatch(bool clearSources = true)
        {
            mediaWatchActive = false;
            mediaWatchGeneration++;
            pendingMediaChanges = MediaChangeKind.None;
            pendingMediaDelay = 100;
            mediaRefreshTimer?.Stop();
            mediaSubscriptionRetry?.Stop();
            mediaSubscription?.Dispose();
            mediaSubscription = null;
            ResetExternalVolumeWatch();
            if (clearSources) externalSources = new List<SystemMediaService.NowPlaying>();
            WatchMiniMusicPopups(detach: true);
        }

        private void QueueMediaRefresh(MediaChangeKind kind, int delayMs)
        {
            if (!mediaWatchActive || closingApp || Dispatcher.HasShutdownStarted) return;
            if (kind != MediaChangeKind.None)
                pendingMediaDelay = pendingMediaChanges == MediaChangeKind.None ? delayMs : Math.Min(pendingMediaDelay, delayMs);
            pendingMediaChanges |= kind;
            if (pendingMediaChanges == MediaChangeKind.None) return;
            if (mediaRefreshRunning) return;
            if (mediaRefreshTimer == null)
            {
                mediaRefreshTimer = new DispatcherTimer(DispatcherPriority.Background);
                mediaRefreshTimer.Tick += async (s, e) => await FlushMediaRefreshAsync();
            }
            var delay = TimeSpan.FromMilliseconds(Math.Max(0, pendingMediaDelay));
            if (mediaRefreshTimer.IsEnabled && mediaRefreshTimer.Interval <= delay) return;
            mediaRefreshTimer.Stop();
            mediaRefreshTimer.Interval = delay;
            mediaRefreshTimer.Start();
        }

        private void RequestExternalMediaRefresh(int delayMs = 300) => QueueMediaRefresh(MediaChangeKind.All, delayMs);

        private async Task FlushMediaRefreshAsync()
        {
            mediaRefreshTimer?.Stop();
            if (!mediaWatchActive || mediaRefreshRunning) return;
            var changes = pendingMediaChanges;
            pendingMediaChanges = MediaChangeKind.None;
            pendingMediaDelay = 100;
            if (changes == MediaChangeKind.None) return;
            mediaRefreshRunning = true;
            try
            {
                if ((changes & ~MediaChangeKind.Timeline) != 0)
                {
                    if ((changes & MediaChangeKind.Sessions) != 0) ResetExternalVolumeWatch();
                    await RefreshExternalMediaAsync();
                }
                else
                {
                    string Before() => string.Join("|", externalSources.Select(s => s.AppId + ":" + s.IsPlaying + ":" + s.Duration));
                    string before = Before();
                    bool updated = mediaTimelineOverride != null ? mediaTimelineOverride(externalSources) : SystemMediaService.RefreshTimelines(externalSources);
                    bool sourceChanged = ApplyAutomaticMusicSource(DateTime.UtcNow);
                    if (sourceChanged || before != Before()) MusicWindow.RaisePlaybackChanged();
                    else if (updated && appData?.MiniPlayerVisible == true) miniWindow?.UpdateSeek();
                    if (sourceChanged) await EnsureExternalVolumeWatchAsync();
                }
            }
            catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
            finally
            {
                mediaRefreshRunning = false;
                if (mediaWatchActive && pendingMediaChanges != MediaChangeKind.None) QueueMediaRefresh(MediaChangeKind.None, 100);
            }
        }

        private void WatchMiniMusicPopups(bool detach = false)
        {
            var source = detach ? null : miniWindow?.FindName("PlaylistPopup") as System.Windows.Controls.Primitives.Popup;
            var volume = detach ? null : miniWindow?.FindName("VolumePopup") as System.Windows.Controls.Primitives.Popup;
            if (source != watchedSourcePopup)
            {
                if (watchedSourcePopup != null) watchedSourcePopup.Opened -= SourcePopup_Opened;
                watchedSourcePopup = source;
                if (source != null) source.Opened += SourcePopup_Opened;
            }
            if (volume != watchedVolumePopup)
            {
                if (watchedVolumePopup != null) watchedVolumePopup.Opened -= VolumePopup_Opened;
                watchedVolumePopup = volume;
                if (volume != null) volume.Opened += VolumePopup_Opened;
            }
        }
        private void SourcePopup_Opened(object sender, EventArgs e) => RequestExternalMediaRefresh(0);
        private void VolumePopup_Opened(object sender, EventArgs e) => UpdateMediaWatchVisibility();

        private void UpdateMediaWatchVisibility()
        {
            if (appData?.MiniPlayerVisible == false) { ResetExternalVolumeWatch(); return; }
            if (mediaWatchActive) EnsureExternalVolumeWatchSoon();
        }

        private AppVolumeService.IVolumeWatch externalVolumeWatch;
        private string volumeOpeningApp;
        private int volumeWatchGeneration, volumeRetryCount;

        private void ResetExternalVolumeWatch()
        {
            volumeWatchGeneration++;
            volumeOpeningApp = null;
            volumeRetryCount = 0;
            volumeRetryTimer?.Stop();
            externalVolumeWatch?.Dispose();
            externalVolumeWatch = null;
        }

        private async void EnsureExternalVolumeWatchSoon()
        {
            try { await EnsureExternalVolumeWatchAsync(); }
            catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
        }

        private async Task EnsureExternalVolumeWatchAsync()
        {
            if (!UseExternal || appData?.MiniPlayerVisible == false) { ResetExternalVolumeWatch(); return; }
            string app = selectedExternalApp;
            if (volumeOpeningApp == app) return;
            if (externalVolumeWatch != null && externalVolumeApp == app && externalVolumeWatch.CurrentVolume.HasValue) return;
            int generation = ++volumeWatchGeneration;
            volumeOpeningApp = app;
            AppVolumeService.IVolumeWatch watch = null;
            try
            {
                Action<float?> changed = level => VolumeNotification(generation, app, level);
                if (volumeWatchFactoryOverride != null) watch = await volumeWatchFactoryOverride(app, changed);
                else if (mediaVolumeOverride != null) watch = new SnapshotVolumeWatch(mediaVolumeOverride(app));
                else watch = await AppVolumeService.WatchAsync(app, changed);
            }
            catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
            if (generation != volumeWatchGeneration || app != selectedExternalApp) { watch?.Dispose(); return; }
            volumeOpeningApp = null;
            externalVolumeWatch?.Dispose();
            externalVolumeWatch = watch;
            externalVolumeApp = app;
            if (externalVolumeSetter?.IsBusy != true) externalVolume = watch?.CurrentVolume;
            if (watch == null) ScheduleVolumeRetry();
            MusicWindow.RaisePlaybackChanged();
        }

        private void VolumeNotification(int generation, string app, float? level)
        {
            if (generation != volumeWatchGeneration || Dispatcher.HasShutdownStarted) return;
            void Apply()
            {
                if (generation != volumeWatchGeneration || app != selectedExternalApp || externalVolumeSetter?.IsBusy == true) return;
                bool changed = !externalVolume.HasValue || !level.HasValue || Math.Abs(externalVolume.Value - level.Value) > .004;
                externalVolumeApp = app; externalVolume = level;
                if (!level.HasValue) { externalVolumeWatch?.Dispose(); externalVolumeWatch = null; ScheduleVolumeRetry(); }
                if (changed) MusicWindow.RaisePlaybackChanged();
            }
            if (Dispatcher.CheckAccess()) Apply();
            else Dispatcher.BeginInvoke(new Action(Apply), DispatcherPriority.Background);
        }

        private void ScheduleVolumeRetry()
        {
            if (!mediaWatchActive || volumeRetryCount >= 2 || volumeRetryTimer?.IsEnabled == true) return;
            if (volumeRetryTimer == null)
            {
                volumeRetryTimer = new DispatcherTimer();
                volumeRetryTimer.Tick += (s, e) => { volumeRetryTimer.Stop(); if (mediaWatchActive) EnsureExternalVolumeWatchSoon(); };
            }
            volumeRetryCount++;
            volumeRetryTimer.Interval = TimeSpan.FromSeconds(volumeRetryCount);
            volumeRetryTimer.Start(); // at most two retries while a newly playing app creates its audio session
        }

        private sealed class SnapshotVolumeWatch : AppVolumeService.IVolumeWatch
        {
            public float? CurrentVolume { get; }
            public SnapshotVolumeWatch(float? value) { CurrentVolume = value; }
            public void Dispose() { }
        }
    }
}
