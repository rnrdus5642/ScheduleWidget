using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace ScheduleWidget
{
    [Flags]
    public enum MediaChangeKind { None = 0, Sessions = 1, Metadata = 2, Playback = 4, Timeline = 8, Current = 16, All = 31 }

    public static partial class SystemMediaService
    {
        public static async Task<IDisposable> WatchAsync(Action<MediaChangeKind> changed)
        {
            if (changed == null) throw new ArgumentNullException(nameof(changed));
            try { return new MediaWatch(await ManagerAsync().ConfigureAwait(false), changed); }
            catch (Exception ex) when (!IsFatal(ex)) { return null; }
        }

        // Timeline notifications reuse existing session handles: no title/window/process enumeration.
        public static bool RefreshTimelines(IEnumerable<NowPlaying> sources)
        {
            bool changed = false;
            foreach (var item in sources)
            {
                if (item.Session == null) continue;
                try
                {
                    var timeline = item.Session.GetTimelineProperties();
                    bool playing = item.Session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    TimeSpan? duration = timeline != null && timeline.EndTime > timeline.StartTime ? timeline.EndTime - timeline.StartTime : (TimeSpan?)null;
                    TimeSpan? position = duration.HasValue ? timeline.Position - timeline.StartTime : (TimeSpan?)null;
                    var updated = timeline?.LastUpdatedTime ?? default(DateTimeOffset);
                    changed |= playing != item.IsPlaying || duration != item.Duration || position != item.Position || updated != item.UpdatedAt;
                    item.IsPlaying = playing; item.Duration = duration; item.Position = position; item.UpdatedAt = updated;
                }
                catch (Exception ex) when (!IsFatal(ex)) { } // removal is followed by SessionsChanged
            }
            return changed;
        }

        private sealed class MediaWatch : IDisposable
        {
            private readonly GlobalSystemMediaTransportControlsSessionManager owner;
            private readonly Action<MediaChangeKind> changed;
            private readonly object gate = new object();
            private readonly HashSet<GlobalSystemMediaTransportControlsSession> sessions = new HashSet<GlobalSystemMediaTransportControlsSession>();
            private bool disposed;

            public MediaWatch(GlobalSystemMediaTransportControlsSessionManager owner, Action<MediaChangeKind> changed)
            {
                this.owner = owner; this.changed = changed;
                try
                {
                    owner.SessionsChanged += SessionsChanged;
                    owner.CurrentSessionChanged += CurrentChanged;
                    SynchronizeSessions();
                }
                catch { Dispose(); throw; }
            }

            private void SynchronizeSessions()
            {
                lock (gate)
                {
                    if (disposed) return;
                    var current = new HashSet<GlobalSystemMediaTransportControlsSession>(owner.GetSessions().Where(s => !IsOwn(s)));
                    foreach (var session in sessions.Where(s => !current.Contains(s)).ToList()) { Unsubscribe(session); sessions.Remove(session); }
                    foreach (var session in current.Where(s => !sessions.Contains(s)))
                    {
                        try
                        {
                            session.MediaPropertiesChanged += MetadataChanged;
                            session.PlaybackInfoChanged += PlaybackChanged;
                            session.TimelinePropertiesChanged += TimelineChanged;
                            sessions.Add(session);
                        }
                        catch (Exception ex) when (!IsFatal(ex)) { Unsubscribe(session); }
                    }
                }
            }

            private void Raise(MediaChangeKind kind)
            {
                lock (gate) { if (disposed) return; }
                try { changed(kind); } catch (Exception ex) when (!IsFatal(ex)) { }
            }
            private void SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args)
            {
                try { SynchronizeSessions(); } catch (Exception ex) when (!IsFatal(ex)) { }
                Raise(MediaChangeKind.Sessions);
            }
            private void CurrentChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args) => Raise(MediaChangeKind.Current);
            private void MetadataChanged(GlobalSystemMediaTransportControlsSession sender, object args) => Raise(MediaChangeKind.Metadata);
            private void PlaybackChanged(GlobalSystemMediaTransportControlsSession sender, object args) => Raise(MediaChangeKind.Playback);
            private void TimelineChanged(GlobalSystemMediaTransportControlsSession sender, object args) => Raise(MediaChangeKind.Timeline);
            private void Unsubscribe(GlobalSystemMediaTransportControlsSession session)
            {
                try { session.MediaPropertiesChanged -= MetadataChanged; } catch (Exception ex) when (!IsFatal(ex)) { }
                try { session.PlaybackInfoChanged -= PlaybackChanged; } catch (Exception ex) when (!IsFatal(ex)) { }
                try { session.TimelinePropertiesChanged -= TimelineChanged; } catch (Exception ex) when (!IsFatal(ex)) { }
            }
            public void Dispose()
            {
                lock (gate)
                {
                    if (disposed) return;
                    disposed = true;
                    try { owner.SessionsChanged -= SessionsChanged; } catch (Exception ex) when (!IsFatal(ex)) { }
                    try { owner.CurrentSessionChanged -= CurrentChanged; } catch (Exception ex) when (!IsFatal(ex)) { }
                    foreach (var session in sessions) Unsubscribe(session);
                    sessions.Clear();
                }
            }
        }
    }
}
