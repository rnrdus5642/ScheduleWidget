using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ScheduleWidget
{
    public static partial class AppVolumeService
    {
        public interface IVolumeWatch : IDisposable { float? CurrentVolume { get; } }

        public static Task<IVolumeWatch> WatchAsync(string appId, Action<float?> changed)
        {
            if (string.IsNullOrWhiteSpace(appId)) return Task.FromResult<IVolumeWatch>(null);
            if (changed == null) throw new ArgumentNullException(nameof(changed));
            return Task.Run<IVolumeWatch>(() =>
            {
                var watch = new VolumeWatch(changed);
                try
                {
                    ForEachSession(appId, (volume, info) => { }, watch.Add);
                    if (!watch.HasSessions) { watch.Dispose(); return null; }
                    watch.Ready();
                    return watch;
                }
                catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { watch.Dispose(); return null; }
            });
        }

        private sealed class VolumeWatch : IVolumeWatch
        {
            private sealed class Level
            {
                public readonly float Value;
                public readonly bool Alive;
                public Level(float value, bool alive = true) { Value = value; Alive = alive; }
            }
            private sealed class Entry
            {
                public object ComObject;
                public IAudioSessionControl2 Control;
                public VolumeEvents Events;
                public Level State;
            }
            private readonly object gate = new object();
            private readonly List<Entry> entries = new List<Entry>();
            private readonly Action<float?> changed;
            private int disposed, queued, ready;
            public VolumeWatch(Action<float?> changed) { this.changed = changed; }
            public bool HasSessions { get { lock (gate) return entries.Count > 0; } }
            public float? CurrentVolume
            {
                get
                {
                    if (Volatile.Read(ref disposed) != 0) return null;
                    lock (gate)
                    {
                        var values = entries.Select(e => Volatile.Read(ref e.State)).Where(s => s.Alive).Select(s => (float?)s.Value);
                        return values.DefaultIfEmpty(null).Max();
                    }
                }
            }
            public bool Add(object com, IAudioSessionControl2 control, SessionInfo info)
            {
                var initial = new Level(Audible(info.Volume, info.Muted));
                var entry = new Entry { ComObject = com, Control = control, State = initial };
                entry.Events = new VolumeEvents(
                    (value, muted, own) => { Interlocked.Exchange(ref entry.State, new Level(Audible(value, muted))); if (!own) QueueChanged(); },
                    () => { Interlocked.Exchange(ref entry.State, new Level(0, false)); QueueChanged(); });
                if (control.RegisterAudioSessionNotification(entry.Events) < 0) { entry.Events.Deactivate(); return false; }
                lock (gate) entries.Add(entry);
                // Close the gap between reading the initial value and registering; a newer callback always wins.
                try
                {
                    var volume = (ISimpleAudioVolume)com;
                    if (volume.GetMasterVolume(out float value) >= 0 && volume.GetMute(out bool muted) >= 0)
                        Interlocked.CompareExchange(ref entry.State, new Level(Audible(value, muted)), initial);
                }
                catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
                return true; // this watch owns the enumeration's COM reference until Dispose
            }
            public void Ready() { Volatile.Write(ref ready, 1); QueueChanged(); }
            private void QueueChanged()
            {
                if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref ready) == 0 || Interlocked.Exchange(ref queued, 1) != 0) return;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Interlocked.Exchange(ref queued, 0);
                    if (Volatile.Read(ref disposed) != 0) return;
                    try { changed(CurrentVolume); } catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
                });
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                Entry[] release;
                lock (gate) { release = entries.ToArray(); entries.Clear(); }
                foreach (var entry in release) entry.Events.Deactivate();
                // Never unregister or release WASAPI objects inside an audio callback or on the UI thread.
                Task.Run(() =>
                {
                    foreach (var entry in release)
                    {
                        try { entry.Control.UnregisterAudioSessionNotification(entry.Events); }
                        catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
                        finally { try { Release(entry.ComObject); } catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { } }
                    }
                });
            }
        }

        [ComVisible(true), Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioSessionEvents
        {
            [PreserveSig] int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr context);
            [PreserveSig] int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr context);
            [PreserveSig] int OnSimpleVolumeChanged(float volume, [MarshalAs(UnmanagedType.Bool)] bool muted, IntPtr context);
            [PreserveSig] int OnChannelVolumeChanged(uint count, IntPtr volumes, uint channel, IntPtr context);
            [PreserveSig] int OnGroupingParamChanged(IntPtr grouping, IntPtr context);
            [PreserveSig] int OnStateChanged(int state);
            [PreserveSig] int OnSessionDisconnected(int reason);
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class VolumeEvents : IAudioSessionEvents
        {
            private Action<float, bool, bool> changed;
            private Action disconnected;
            public VolumeEvents(Action<float, bool, bool> changed, Action disconnected) { this.changed = changed; this.disconnected = disconnected; }
            public void Deactivate() { Interlocked.Exchange(ref changed, null); Interlocked.Exchange(ref disconnected, null); }
            public int OnDisplayNameChanged(string name, IntPtr context) => 0;
            public int OnIconPathChanged(string path, IntPtr context) => 0;
            public int OnSimpleVolumeChanged(float volume, bool muted, IntPtr context)
            {
                bool own = context != IntPtr.Zero && Marshal.PtrToStructure<Guid>(context) == VolumeEventContext;
                try { changed?.Invoke(volume, muted, own); } catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { }
                return 0;
            }
            public int OnChannelVolumeChanged(uint count, IntPtr volumes, uint channel, IntPtr context) => 0;
            public int OnGroupingParamChanged(IntPtr grouping, IntPtr context) => 0;
            public int OnStateChanged(int state) { if (state == 2) Disconnected(); return 0; }
            public int OnSessionDisconnected(int reason) { Disconnected(); return 0; }
            private void Disconnected() { try { disconnected?.Invoke(); } catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { } }
        }
    }
}
