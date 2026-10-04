using System;
using System.Collections.Generic;
using System.Linq;

namespace ScheduleWidget
{
    // Observing another app never starts, pauses or changes its volume.
    public sealed class AutomaticMediaSource
    {
        private HashSet<string> previouslyPlaying = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string missingApp;
        private DateTime missingSince;

        public void Reset()
        {
            previouslyPlaying.Clear();
            missingApp = null;
        }

        public string Select(IEnumerable<SystemMediaService.NowPlaying> sources, string selectedApp, bool ownPlayerPlaying, bool automatic, DateTime now)
        {
            var available = (sources ?? Enumerable.Empty<SystemMediaService.NowPlaying>())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.AppId)).ToList();
            var playing = available.Where(s => s.IsPlaying).OrderByDescending(s => s.IsCurrent).ToList();
            var started = playing.FirstOrDefault(s => !previouslyPlaying.Contains(s.AppId) && !Same(s.AppId, selectedApp));
            previouslyPlaying = new HashSet<string>(playing.Select(s => s.AppId), StringComparer.OrdinalIgnoreCase);
            if (!automatic) { missingApp = null; return selectedApp; }
            if (ownPlayerPlaying) { missingApp = null; return null; }

            var current = available.Where(s => Same(s.AppId, selectedApp)).OrderByDescending(s => s.IsPlaying).FirstOrDefault();
            if (current != null)
            {
                missingApp = null;
                if (started != null) return started.AppId;
                if (current.IsPlaying) return selectedApp;
                return playing.FirstOrDefault()?.AppId ?? selectedApp;
            }
            if (playing.Count > 0) { missingApp = null; return playing[0].AppId; }
            if (selectedApp == null) { missingApp = null; return null; }
            if (!Same(missingApp, selectedApp)) { missingApp = selectedApp; missingSince = now; }
            if (now - missingSince < TimeSpan.FromSeconds(5)) return selectedApp;
            missingApp = null;
            return null;
        }

        private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
