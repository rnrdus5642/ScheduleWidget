using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ScheduleWidget
{
    public partial class MainWindow
    {
        private readonly AutomaticMediaSource automaticMediaSource = new AutomaticMediaSource();
        private bool automaticMusicSource = true;
        private long musicSourceRequest;
        // Checks replace read-only OS queries without touching running media applications.
        private Func<Task<List<SystemMediaService.NowPlaying>>> mediaSourcesOverride = null;
        private Func<string, float?> mediaVolumeOverride = null;

        bool IMusicControls.AutomaticSource => automaticMusicSource;

        void IMusicControls.SelectAutomaticSource()
        {
            automaticMusicSource = true;
            musicSourceRequest++;
            automaticMediaSource.Reset();
            SetExternalMusicSource(null, notify: false);
            ApplyAutomaticMusicSource(DateTime.UtcNow);
            UpdateMediaWatchVisibility();
            MusicWindow.RaisePlaybackChanged();
            RequestExternalMediaRefresh(0);
        }

        private bool ApplyAutomaticMusicSource(DateTime now)
        {
            string next = automaticMediaSource.Select(externalSources, selectedExternalApp,
                musicWindow?.IsPlaying == true, automaticMusicSource, now);
            bool changed = SetExternalMusicSource(next, notify: false);
            // A vanished session produces only one event. Finish its brief grace period without periodic polling.
            if (automaticMusicSource && next != null && !externalSources.Any(s => string.Equals(s.AppId, next, StringComparison.OrdinalIgnoreCase)))
                QueueMediaRefresh(MediaChangeKind.Playback, 5100);
            return changed;
        }

        private bool SetExternalMusicSource(string appId, bool notify = true)
        {
            if (string.Equals(selectedExternalApp, appId, StringComparison.OrdinalIgnoreCase)) return false;
            selectedExternalApp = appId;
            externalVolume = externalVolumeTaken = null;
            externalVolumeApp = null;
            ResetExternalVolumeWatch();
            UpdateMediaWatchVisibility();
            if (notify) MusicWindow.RaisePlaybackChanged();
            return true;
        }

        private void OwnMusicPlaybackChanged()
        {
            if (musicWindow?.IsPlaying != true || selectedExternalApp == null) return;
            musicSourceRequest++;
            SetExternalMusicSource(null);
        }
    }
}
