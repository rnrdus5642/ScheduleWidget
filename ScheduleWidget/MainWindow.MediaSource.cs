using System;
using System.Collections.Generic;
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
            UpdateExternalPollInterval();
            MusicWindow.RaisePlaybackChanged();
            PollExternalMediaSoon(0);
        }

        private bool ApplyAutomaticMusicSource(DateTime now)
        {
            string next = automaticMediaSource.Select(externalSources, selectedExternalApp,
                musicWindow?.IsPlaying == true, automaticMusicSource, now);
            return SetExternalMusicSource(next, notify: false);
        }

        private bool SetExternalMusicSource(string appId, bool notify = true)
        {
            if (string.Equals(selectedExternalApp, appId, StringComparison.OrdinalIgnoreCase)) return false;
            selectedExternalApp = appId;
            externalVolume = externalVolumeTaken = null;
            externalVolumeApp = null;
            externalVolumeReads = 0;
            UpdateExternalPollInterval();
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
