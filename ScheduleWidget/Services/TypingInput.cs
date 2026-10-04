using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ScheduleWidget
{
    /// <summary>
    /// Where key presses come from for 타이핑 반응. A source calls <c>keyDown</c> once for each new press — with no
    /// argument: which key it was never leaves the source.
    /// </summary>
    public interface ITypingKeySource
    {
        bool Start(Action keyDown);
        void Stop();
    }

    /// <summary>
    /// 타이핑 반응 (keyboard pet): counts key presses anywhere in Windows while at least one shown pet uses that mode, and
    /// hands each pets window the count in small batches. Only "a key went down" is counted — never which key: nothing about
    /// the keys is recorded, saved, sent anywhere or logged. Listening starts when the first such pet shows and stops when
    /// the last one goes (hidden, another action, its window hidden or closed). Raw Input, not a keyboard hook: Windows hands
    /// the input over after the app it is meant for got it, so no typing anywhere waits on this app. Apps running as
    /// administrator do not share their input with this (normal) app: typing there is not seen.
    /// </summary>
    internal static class TypingInput
    {
        private sealed class Listener
        {
            public Func<bool> Wants;
            public Action<int> Keys;
        }

        private static readonly Dictionary<object, Listener> listeners = new Dictionary<object, Listener>();
        private static ITypingKeySource source;
        private static int pending;
        private static DispatcherTimer flushTimer;
        private static DateTime failedAt = DateTime.MinValue;

        /// <summary>The key source; tests put a fake one here (never real input).</summary>
        internal static Func<ITypingKeySource> SourceFactory = () => new RawKeyboardSource();
        /// <summary>Presses are handed on at most this often (a fast typist must not flood the pets' web views).</summary>
        internal static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(25);
        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

        /// <summary>Whether key presses are being listened for now.</summary>
        public static bool Listening => source != null;

        /// <param name="owner">A pets window (the key it is known by).</param>
        /// <param name="wants">Whether it has a shown pet in 타이핑 반응 now.</param>
        /// <param name="keys">Gets the number of presses since the last batch.</param>
        public static void Subscribe(object owner, Func<bool> wants, Action<int> keys)
        {
            if (owner == null || wants == null || keys == null) return;
            listeners[owner] = new Listener { Wants = wants, Keys = keys };
            Refresh();
        }

        public static void Unsubscribe(object owner)
        {
            if (owner != null && listeners.Remove(owner)) Refresh();
        }

        /// <summary>Starts or stops listening as the pets now need it (call after any change to modes, pets or windows).</summary>
        public static void Refresh()
        {
            bool wanted = listeners.Values.ToList().Any(Wants);
            if (wanted && source == null)
            {
                if (DateTime.UtcNow - failedAt < RetryAfterFailure) return; // no new window and registration on every change
                ITypingKeySource started = null;
                try { started = SourceFactory?.Invoke(); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is ExternalException) { }
                if (started != null && started.Start(KeyDown)) { source = started; failedAt = DateTime.MinValue; return; }
                failedAt = DateTime.UtcNow;
                PetLog.Write("typing-input-unavailable", started is RawKeyboardSource raw ? "error " + raw.LastError : "no source");
            }
            else if (!wanted && source != null) Stop();
        }

        private static bool Wants(Listener listener)
        {
            try { return listener.Wants(); }
            catch (InvalidOperationException) { return false; }
        }

        private static void Stop()
        {
            var stopping = source;
            source = null;
            pending = 0;
            flushTimer?.Stop();
            try { stopping.Stop(); } catch (Exception ex) when (ex is InvalidOperationException || ex is ExternalException) { }
        }

        /// <summary>
        /// One key went down (a new press, not a held key repeating). Takes no argument on purpose: which key it was is never
        /// known past the key source.
        /// </summary>
        internal static void KeyDown()
        {
            if (source == null) return;
            pending++;
            if (flushTimer == null)
            {
                flushTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = BatchInterval };
                flushTimer.Tick += (s, e) => Flush();
            }
            if (!flushTimer.IsEnabled) flushTimer.Start();
        }

        /// <summary>Hands the presses counted since the last batch to every pets window that has a pet in 타이핑 반응.</summary>
        internal static void Flush()
        {
            flushTimer?.Stop();
            int count = pending;
            pending = 0;
            if (count <= 0 || source == null) return;
            foreach (var listener in listeners.Values.ToList())
                if (Wants(listener)) listener.Keys(count);
        }
    }

    /// <summary>
    /// Tells a new key press from a held key repeating, so a held key counts once. It holds only the scan codes of the keys
    /// down right now (dropped on release); a key whose release was never seen (it went to an administrator app) counts as
    /// released once it has not repeated for <see cref="StaleAfterMs"/>.
    /// </summary>
    internal sealed class KeyRepeatFilter
    {
        internal const int StaleAfterMs = 1500; // Windows repeats a held key at least every 1 s (the longest repeat delay)
        private const int MaxHeld = 32;
        private readonly Dictionary<int, int> held = new Dictionary<int, int>();

        /// <summary>True for a new press.</summary>
        public bool IsNewPress(int code, bool release, int now)
        {
            if (release) { held.Remove(code); return false; }
            bool repeat = held.TryGetValue(code, out int last) && unchecked(now - last) < StaleAfterMs;
            held[code] = now;
            if (held.Count > MaxHeld)
                foreach (int stale in held.Where(h => unchecked(now - h.Value) >= StaleAfterMs).Select(h => h.Key).ToList()) held.Remove(stale);
            return !repeat;
        }

        public int HeldCount => held.Count;
        public void Clear() => held.Clear();
    }

    /// <summary>
    /// Raw Input from every keyboard (usage page 1, usage 6) with RIDEV_INPUTSINK, to a hidden message-only window: Windows
    /// copies each key event here after the focused app got it. Read are only the event's scan code (to tell a repeat from a
    /// press, see <see cref="KeyRepeatFilter"/>) and whether it is a release; the rest is left alone.
    /// </summary>
    internal sealed class RawKeyboardSource : ITypingKeySource
    {
        private const int WM_INPUT = 0x00FF;
        private const uint RIDEV_REMOVE = 0x00000001, RIDEV_INPUTSINK = 0x00000100;
        private const uint RID_INPUT = 0x10000003, RIM_TYPEKEYBOARD = 1;
        private const ushort RI_KEY_BREAK = 1, RI_KEY_E0 = 2, RI_KEY_E1 = 4, KEYBOARD_OVERRUN_MAKE_CODE = 0xFF;
        private const int BufferSize = 64; // RAWINPUT for a keyboard: 40 bytes (64-bit), 32 (32-bit)

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE { public ushort UsagePage; public ushort Usage; public uint Flags; public IntPtr Target; }
        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER { public uint Type; public uint Size; public IntPtr Device; public IntPtr WParam; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        private static readonly uint HeaderSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
        private HwndSource window;
        private IntPtr buffer;
        private Action keyDown;
        private readonly KeyRepeatFilter filter = new KeyRepeatFilter();

        public int LastError { get; private set; }
        internal IntPtr Handle => window?.Handle ?? IntPtr.Zero;

        public bool Start(Action keyDown)
        {
            if (window != null) return true;
            this.keyDown = keyDown;
            // HWND_MESSAGE (-3): a message-only window — never shown, never in Alt+Tab, gets no broadcasts.
            window = new HwndSource(new HwndSourceParameters("ScheduleWidget typing") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
            window.AddHook(Hook);
            buffer = Marshal.AllocHGlobal(BufferSize);
            if (Register(RIDEV_INPUTSINK, window.Handle)) return true;
            LastError = Marshal.GetLastWin32Error();
            Release();
            return false;
        }

        public void Stop()
        {
            if (window == null) return;
            Register(RIDEV_REMOVE, IntPtr.Zero);
            Release();
        }

        private static bool Register(uint flags, IntPtr target) =>
            RegisterRawInputDevices(new[] { new RAWINPUTDEVICE { UsagePage = 1, Usage = 6, Flags = flags, Target = target } }, 1,
                (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));

        private void Release()
        {
            if (window != null) { window.RemoveHook(Hook); window.Dispose(); window = null; }
            if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; }
            filter.Clear();
            keyDown = null;
        }

        // handled stays false: DefWindowProc then frees the input (as Windows asks for WM_INPUT).
        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_INPUT) HandleInput(lParam);
            return IntPtr.Zero;
        }

        /// <summary>One WM_INPUT: a new key press calls keyDown. Input that cannot be read is ignored.</summary>
        internal void HandleInput(IntPtr rawInput)
        {
            if (buffer == IntPtr.Zero || rawInput == IntPtr.Zero) return;
            uint size = BufferSize;
            uint read = GetRawInputData(rawInput, RID_INPUT, buffer, ref size, HeaderSize);
            if (read == uint.MaxValue || read == 0) return;
            if (TryReadKey(buffer, read, out int code, out bool release) && filter.IsNewPress(code, release, Environment.TickCount) && !release)
                keyDown?.Invoke();
        }

        /// <summary>
        /// A keyboard RAWINPUT's scan code (with its E0 / E1 prefix, so left and right keys differ) and whether the key went up.
        /// </summary>
        internal static bool TryReadKey(IntPtr data, uint length, out int code, out bool release)
        {
            code = 0; release = false;
            if (data == IntPtr.Zero || length < HeaderSize + 8 || (uint)Marshal.ReadInt32(data, 0) != RIM_TYPEKEYBOARD) return false;
            int at = (int)HeaderSize;
            ushort makeCode = unchecked((ushort)Marshal.ReadInt16(data, at)), flags = unchecked((ushort)Marshal.ReadInt16(data, at + 2));
            if (makeCode == KEYBOARD_OVERRUN_MAKE_CODE) return false;
            code = makeCode | (flags & RI_KEY_E0) << 15 | (flags & RI_KEY_E1) << 15;
            release = (flags & RI_KEY_BREAK) != 0;
            return true;
        }

        internal int HeldKeys => filter.HeldCount;
    }
}
