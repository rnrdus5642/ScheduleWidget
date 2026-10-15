using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ScheduleWidget
{
    // A composition browser's native surface can appear after page-ready, or be replaced after a display/session change.
    // Keep only the known noninteractive pet surface click-through; other WebView2 windows retain their normal input.
    internal sealed class BrowserWindowInputGuard : IDisposable
    {
        internal static TimeSpan RecheckInterval = TimeSpan.FromSeconds(5);
        private readonly uint browserProcessId;
        private readonly string titlePart;
        private readonly Dispatcher dispatcher;
        private readonly Action<int> repaired;
        private readonly WinEventCallback callback;
        private readonly DispatcherTimer timer;
        private readonly HashSet<IntPtr> pendingWindows = new HashSet<IntPtr>();
        private IntPtr hook;
        private bool disposed, queued;

        private delegate void WinEventCallback(IntPtr eventHook, uint eventType, IntPtr window, int objectId, int childId, uint threadId, uint time);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventCallback callback, uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);

        internal BrowserWindowInputGuard(uint processId, string titlePart, Dispatcher dispatcher, Action<int> repaired)
        {
            if (processId == 0) throw new ArgumentOutOfRangeException(nameof(processId));
            this.browserProcessId = processId;
            this.titlePart = titlePart;
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.repaired = repaired;
            callback = OnWindowEvent;
            // Out-of-context callbacks run on this thread's message loop, filtered to this browser process.
            hook = SetWinEventHook(0x8000 /* CREATE */, 0x800C /* NAMECHANGE */, IntPtr.Zero, callback, processId, 0, 0);
            timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = RecheckInterval };
            timer.Tick += (s, e) => Verify(); // backstop when the runtime silently resets native window styles
            timer.Start();
            Verify();
        }

        private void OnWindowEvent(IntPtr eventHook, uint eventType, IntPtr window, int objectId, int childId, uint threadId, uint time)
        {
            if (disposed || window == IntPtr.Zero || objectId != 0 || childId != 0 || dispatcher.HasShutdownStarted) return;
            if (!NativeMethods.IsPetBrowserWindow(window, browserProcessId, titlePart)) return;
            pendingWindows.Add(window);
            if (queued) return;
            queued = true;
            dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                queued = false;
                if (!disposed) Verify();
            }));
        }

        internal void Verify()
        {
            if (disposed || dispatcher.HasShutdownStarted) return;
            int changed = NativeMethods.MakeBrowserWindowsClickThrough(browserProcessId, titlePart);
            // The event's exact handle also covers a surface temporarily reparented as a child during recovery.
            var pending = new IntPtr[pendingWindows.Count];
            pendingWindows.CopyTo(pending);
            pendingWindows.Clear();
            foreach (IntPtr window in pending)
                if (NativeMethods.MakePetBrowserWindowClickThrough(window, browserProcessId, titlePart)) changed++;
            if (changed > 0) repaired?.Invoke(changed);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            if (hook != IntPtr.Zero) { UnhookWinEvent(hook); hook = IntPtr.Zero; }
            pendingWindows.Clear();
        }
    }
}
