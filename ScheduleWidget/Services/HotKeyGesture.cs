using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace ScheduleWidget
{
    /// <summary>
    /// A global shortcut such as "Ctrl+G" or "Ctrl+Alt+F9": text form (saved in schedules.json, shown in 설정) and the
    /// RegisterHotKey form. F1–F24 work alone; any other key needs Ctrl / Alt / Win so the shortcut never swallows normal typing.
    /// </summary>
    public sealed class HotKeyGesture
    {
        public const string Default = "Ctrl+G";

        public ModifierKeys Modifiers { get; }
        public Key Key { get; }

        public HotKeyGesture(ModifierKeys modifiers, Key key)
        {
            Modifiers = modifiers;
            Key = key;
        }

        // RegisterHotKey modifier flags.
        public uint NativeModifiers =>
            ((Modifiers & ModifierKeys.Alt) != 0 ? 0x0001u : 0) |
            ((Modifiers & ModifierKeys.Control) != 0 ? 0x0002u : 0) |
            ((Modifiers & ModifierKeys.Shift) != 0 ? 0x0004u : 0) |
            ((Modifiers & ModifierKeys.Windows) != 0 ? 0x0008u : 0);

        public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

        public override string ToString()
        {
            var parts = new List<string>();
            if ((Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((Modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
            parts.Add(KeyName(Key));
            return string.Join("+", parts);
        }

        /// <summary>A key press in the 설정 shortcut box; null while only modifiers are held or the combo is not allowed.</summary>
        public static HotKeyGesture FromKeyPress(ModifierKeys modifiers, Key key)
        {
            if (IsModifierKey(key) || !IsAllowedKey(key)) return null;
            bool functionKey = key >= Key.F1 && key <= Key.F24;
            if (!functionKey && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0) return null;
            return new HotKeyGesture(modifiers, key);
        }

        public static bool TryParse(string text, out HotKeyGesture gesture)
        {
            gesture = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var modifiers = ModifierKeys.None;
            Key? key = null;
            foreach (string raw in text.Split('+').Select(p => p.Trim()))
            {
                switch (raw.ToLowerInvariant())
                {
                    case "ctrl": case "control": modifiers |= ModifierKeys.Control; continue;
                    case "alt": modifiers |= ModifierKeys.Alt; continue;
                    case "shift": modifiers |= ModifierKeys.Shift; continue;
                    case "win": case "windows": modifiers |= ModifierKeys.Windows; continue;
                }
                if (key.HasValue) return false;
                Key parsed;
                if (raw.Length == 1 && char.IsDigit(raw[0])) parsed = Key.D0 + (raw[0] - '0');
                else if (!Enum.TryParse(raw, true, out parsed)) return false;
                key = parsed;
            }
            if (!key.HasValue) return false;
            gesture = FromKeyPress(modifiers, key.Value);
            return gesture != null;
        }

        /// <summary>Saved text → a valid shortcut; anything unreadable falls back to Ctrl+G.</summary>
        public static HotKeyGesture ParseOrDefault(string text)
        {
            return TryParse(text, out HotKeyGesture gesture) ? gesture : new HotKeyGesture(ModifierKeys.Control, Key.G);
        }

        private static bool IsModifierKey(Key key) =>
            key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin || key == Key.System;

        private static bool IsAllowedKey(Key key) =>
            (key >= Key.A && key <= Key.Z) || (key >= Key.D0 && key <= Key.D9) || (key >= Key.F1 && key <= Key.F24) ||
            (key >= Key.NumPad0 && key <= Key.NumPad9) || key == Key.Space || key == Key.Home || key == Key.End ||
            key == Key.Insert || key == Key.Delete || key == Key.PageUp || key == Key.PageDown ||
            key == Key.Up || key == Key.Down || key == Key.Left || key == Key.Right ||
            key == Key.OemComma || key == Key.OemPeriod || key == Key.OemMinus || key == Key.OemPlus;

        private static string KeyName(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9) return ((int)(key - Key.D0)).ToString();
            return key.ToString();
        }
    }
}
