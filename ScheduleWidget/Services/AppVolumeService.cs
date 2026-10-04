using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScheduleWidget
{
    // Per-app volume through Windows Core Audio (the same levels as the Windows volume mixer), so the mini window's
    // speaker button can drive the app picked in the music bar (Edge/Chrome playing YouTube, Spotify, …).
    // A media session names its app (SourceAppUserModelId: "Chrome", "MSEdge", "Spotify.exe", or a packaged AUMID);
    // audio sessions belong to processes, and browsers play from child processes, so every audio session whose
    // process has that exe name (or whose session id contains the package name) is used together.
    // Any COM failure returns null/false, and the caller keeps its own (widget) volume.
    public static partial class AppVolumeService
    {
        private static readonly Guid VolumeEventContext = Guid.NewGuid();
        public sealed class SessionInfo
        {
            public int ProcessId { get; set; }
            public string ProcessName { get; set; }
            public string Identifier { get; set; }
            public float Volume { get; set; }
            public bool Muted { get; set; }
        }

        /// <summary>The app's volume 0~1 (the loudest of its sessions; a session muted in the volume mixer counts as 0),
        /// or null when it has no audio session / on error.</summary>
        public static float? GetVolume(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId)) return null; // never "every app"
            float? loudest = null;
            ForEachSession(appId, (volume, info) =>
            {
                float heard = Audible(info.Volume, info.Muted);
                if (!loudest.HasValue || heard > loudest.Value) loudest = heard;
            });
            return loudest;
        }

        /// <summary>What a session plays at: its level, or 0 while it is muted (the mixer's mute keeps the level).</summary>
        public static float Audible(float level, bool muted) => muted ? 0f : level;

        /// <summary>
        /// Sets every audio session of the app to the volume; false when none was found or on error. A level above 0 also
        /// lifts the mixer's mute: the widget's 음소거 해제 (or raising the bar) must make the app heard again, not leave it
        /// silent behind a mute flag the widget shows as unmuted.
        /// </summary>
        public static bool SetVolume(string appId, float value)
        {
            if (string.IsNullOrWhiteSpace(appId)) return false; // an empty id would otherwise match every app's session
            value = Math.Max(0f, Math.Min(1f, value));
            bool any = false;
            ForEachSession(appId, (volume, info) =>
            {
                Guid context = VolumeEventContext;
                if (volume.SetMasterVolume(value, ref context) >= 0) any = true;
                if (value > 0f && info.Muted) { Guid unmute = VolumeEventContext; volume.SetMute(false, ref unmute); }
            });
            return any;
        }

        /// <summary>
        /// Applies app volume levels off the UI thread, one at a time and only the newest: dragging the bar produces a
        /// level per pixel, and each Core Audio lookup (every session + its process) costs a few ms, which made the knob
        /// stutter. While one level is being applied, later ones replace each other; only the last is applied next.
        /// <see cref="Applied"/> reports each applied level (app, level, whether a session took it, whether it was the
        /// last one queued) on the caller's context.
        /// </summary>
        public sealed class CoalescingSetter
        {
            private readonly Func<string, float, bool> apply;
            private readonly object gate = new object();
            private string pendingApp;
            private float? pendingLevel;
            private bool running;
            private System.Threading.Tasks.Task drain = CompletedTask();

            public CoalescingSetter(Func<string, float, bool> apply) { this.apply = apply ?? throw new ArgumentNullException(nameof(apply)); }

            public event Action<string, float, bool, bool> Applied;

            /// <summary>True while a level is being applied or waits to be.</summary>
            public bool IsBusy { get { lock (gate) return running; } }

            /// <summary>Queues the level; the returned task completes when everything queued so far has been applied.</summary>
            public System.Threading.Tasks.Task Set(string appId, float level)
            {
                lock (gate)
                {
                    pendingApp = appId;
                    pendingLevel = level;
                    if (running) return drain;
                    running = true;
                    drain = DrainAsync();
                    return drain;
                }
            }

            private async System.Threading.Tasks.Task DrainAsync()
            {
                bool drained = false;
                try
                {
                    while (true)
                    {
                        string app;
                        float level;
                        lock (gate)
                        {
                            // Idle again inside the same lock that saw the queue empty: a Set right after starts a new drain.
                            if (!pendingLevel.HasValue) { running = false; drained = true; return; }
                            app = pendingApp;
                            level = pendingLevel.Value;
                            pendingLevel = null;
                        }
                        bool ok;
                        try { ok = await System.Threading.Tasks.Task.Run(() => apply(app, level)); }
                        catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { ok = false; }
                        bool last;
                        lock (gate) last = !pendingLevel.HasValue;
                        try { Applied?.Invoke(app, level, ok, last); }
                        catch (Exception ex) when (!SystemMediaService.IsFatal(ex)) { } // a listener's failure must not stop the queue
                    }
                }
                finally { if (!drained) lock (gate) running = false; } // only an escaping (fatal) error gets here still running
            }

            private static System.Threading.Tasks.Task CompletedTask()
            {
                var done = new System.Threading.Tasks.TaskCompletionSource<bool>();
                done.SetResult(true);
                return done.Task;
            }
        }

        /// <summary>Every audio session on the default output device (diagnostics / probes).</summary>
        public static List<SessionInfo> ListSessions()
        {
            var all = new List<SessionInfo>();
            ForEachSession(null, (volume, info) => all.Add(info));
            return all;
        }

        // ---- matching ----

        // Browser media sessions report short app ids; map them to the exe names that play the audio.
        private static readonly Dictionary<string, string[]> KnownProcesses = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "chrome", new[] { "chrome" } }, { "msedge", new[] { "msedge" } }, { "firefox", new[] { "firefox" } },
            { "308046B0AF4A39CB", new[] { "firefox" } }, { "whale", new[] { "whale" } }, { "opera", new[] { "opera" } },
            { "brave", new[] { "brave" } }, { "spotify", new[] { "spotify" } }, { "vivaldi", new[] { "vivaldi" } }
        };

        private static bool Matches(string appId, SessionInfo session)
        {
            if (appId == null) return true;
            string id = appId.Trim();
            // The widget's own hidden YouTube player (msedgewebview2) is never "another app".
            if (id.IndexOf("msedgewebview2", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            string process = session.ProcessName ?? string.Empty;
            foreach (var known in KnownProcesses)
                if (id.IndexOf(known.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // "msedge" must not catch msedgewebview2 processes.
                    foreach (string name in known.Value) if (string.Equals(process, name, StringComparison.OrdinalIgnoreCase)) return true;
                    return false;
                }
            // "Something.exe" → something; a packaged AUMID "Family_hash!App" → match the package in the session id.
            string exe = id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetFileNameWithoutExtension(id) : null;
            if (exe != null) return string.Equals(process, exe, StringComparison.OrdinalIgnoreCase);
            string family = id.Contains("!") ? id.Substring(0, id.IndexOf('!')) : id;
            if (family.Length > 0 && (session.Identifier ?? string.Empty).IndexOf(family, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            string tail = id.Contains("!") ? id.Substring(id.LastIndexOf('!') + 1) : id;
            return tail.Length > 0 && string.Equals(process, tail, StringComparison.OrdinalIgnoreCase);
        }

        private static void ForEachSession(string appId, Action<ISimpleAudioVolume, SessionInfo> action,
            Func<object, IAudioSessionControl2, SessionInfo, bool> retain = null)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            object managerObject = null;
            IAudioSessionEnumerator sessions = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out device) < 0 || device == null) return;
                Guid managerId = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref managerId, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out managerObject) < 0 || managerObject == null) return;
                var manager = (IAudioSessionManager2)managerObject;
                if (manager.GetSessionEnumerator(out sessions) < 0 || sessions == null) return;
                if (sessions.GetCount(out int count) < 0) return;
                for (int i = 0; i < count; i++)
                {
                    object sessionObject = null;
                    bool retained = false;
                    try
                    {
                        if (sessions.GetSession(i, out sessionObject) < 0 || sessionObject == null) continue;
                        var control = sessionObject as IAudioSessionControl2;
                        var volume = sessionObject as ISimpleAudioVolume;
                        if (control == null || volume == null) continue;
                        control.GetProcessId(out uint pid);
                        if (pid == 0) continue; // system sounds
                        control.GetSessionIdentifier(out string identifier);
                        var info = new SessionInfo { ProcessId = (int)pid, ProcessName = ProcessName((int)pid), Identifier = identifier };
                        if (!Matches(appId, info)) continue;
                        volume.GetMasterVolume(out float level);
                        volume.GetMute(out bool muted);
                        info.Volume = level;
                        info.Muted = muted;
                        action(volume, info);
                        if (retain != null) retained = retain(sessionObject, control, info);
                    }
                    catch (COMException) { }
                    catch (InvalidCastException) { }
                    finally { if (!retained) Release(sessionObject); }
                }
            }
            catch (COMException) { }
            catch (InvalidCastException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                Release(sessions);
                Release(managerObject);
                Release(device);
                Release(enumerator);
            }
        }

        private static string ProcessName(int pid)
        {
            try { using (var process = Process.GetProcessById(pid)) return process.ProcessName; }
            catch (ArgumentException) { return null; } // exited
            catch (InvalidOperationException) { return null; }
            catch (System.ComponentModel.Win32Exception) { return null; }
        }

        private static void Release(object com)
        {
            if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }

        // ---- Core Audio COM interfaces (only the members used; vtable order kept) ----

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int streamFlags, out IntPtr sessionControl);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int streamFlags, out IntPtr audioVolume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int sessionCount);
            [PreserveSig] int GetSession(int sessionIndex, [MarshalAs(UnmanagedType.IUnknown)] out object session);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            [PreserveSig] int GetGroupingParam(out Guid groupingParam);
            [PreserveSig] int SetGroupingParam(ref Guid overrideValue, ref Guid eventContext);
            [PreserveSig] int RegisterAudioSessionNotification([MarshalAs(UnmanagedType.Interface)] IAudioSessionEvents client);
            [PreserveSig] int UnregisterAudioSessionNotification([MarshalAs(UnmanagedType.Interface)] IAudioSessionEvents client);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
            [PreserveSig] int GetProcessId(out uint processId);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }
    }
}
