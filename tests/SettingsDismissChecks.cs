using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Windows.Controls;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static void SettingsDismissalAudit()
        {
            Run("Settings retain child controls and owned pickers but dismiss for the calendar and other windows", () =>
            {
                using (var windows = new SettingsFocusWindows())
                {
                    var calendar = windows.Create();
                    var settings = windows.Create(calendar);
                    var control = windows.Create(settings, child: true);
                    var popup = windows.Create(settings);
                    var picker = windows.Create(popup);
                    var pickerControl = windows.Create(picker, child: true);
                    var unrelated = windows.Create();
                    var sibling = windows.Create(calendar);
                    foreach (var related in new[] { settings, control, popup, picker, pickerControl })
                        Require(!AuxiliaryWindowDismissal.ShouldDismissForForeground(settings, related), "A settings control, dropdown or nested picker closed its owner.");
                    foreach (var outside in new[] { calendar, unrelated, sibling })
                        Require(AuxiliaryWindowDismissal.ShouldDismissForForeground(settings, outside), "An outside window in the same app was mistaken for a settings dialog.");
                }
            });

            Run("Modal dialogs and transient foreground loss do not tear down settings", () =>
            {
                using (var windows = new SettingsFocusWindows())
                {
                    var settings = windows.Create();
                    var outside = windows.Create();
                    Require(!AuxiliaryWindowDismissal.ShouldDismissForForeground(settings, IntPtr.Zero), "A transient activation gap dismissed settings.");
                    SettingsFocusWindows.EnableWindow(settings, false);
                    Require(!AuxiliaryWindowDismissal.ShouldDismissForForeground(settings, outside), "A modal dialog's disabled owner was dismissed.");
                    SettingsFocusWindows.EnableWindow(settings, true);
                    Require(AuxiliaryWindowDismissal.ShouldDismissForForeground(settings, outside), "Settings never resumed dismissal after the modal dialog ended.");
                    Require(!AuxiliaryWindowDismissal.ShouldDismissForForeground(IntPtr.Zero, outside), "A missing settings window was accepted.");
                }
            });

            Run("Settings activation cancels delayed dismissal and closing stops future checks", () =>
            {
                var settings = new SettingsWindow();
                var dismissal = (AuxiliaryWindowDismissal)Field(settings, "dismissal");
                var timer = (DispatcherTimer)Field(dismissal, "timer");
                bool outside = false;
                int requests = 0;
                dismissal.CanDismissOverride = () => outside;
                var frame = new DispatcherFrame();
                var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                deadline.Tick += (s, e) => frame.Continue = false;
                settings.CloseRequested += () => { requests++; settings.Close(); frame.Continue = false; };
                try
                {
                    Call(dismissal, "Window_Deactivated", settings, EventArgs.Empty);
                    Call(dismissal, "RecheckDismissal", null, EventArgs.Empty);
                    Require(requests == 0 && timer.IsEnabled, "Settings closed while a related popup held focus.");
                    outside = true;
                    Call(dismissal, "Window_Activated", settings, EventArgs.Empty);
                    Call(dismissal, "RecheckDismissal", null, EventArgs.Empty);
                    Require(requests == 0 && !timer.IsEnabled, "Returning to settings did not cancel the queued dismissal.");
                    Call(dismissal, "Window_Deactivated", settings, EventArgs.Empty);
                    Require(requests == 0, "Settings closed inside the deactivation callback.");
                    deadline.Start();
                    Dispatcher.PushFrame(frame); // Real delayed callback, no visible windows or foreground changes.
                    Require(requests == 1 && !timer.IsEnabled, "Outside focus did not dismiss exactly once and release its timer.");
                    Call(dismissal, "Window_Deactivated", settings, EventArgs.Empty);
                    Call(dismissal, "RecheckDismissal", null, EventArgs.Empty);
                    Require(requests == 1 && !timer.IsEnabled, "A late activation event revived a closed settings session.");
                    Require(!settings.IsVisible, "The isolated settings check displayed a window.");
                }
                finally { deadline.Stop(); settings.Close(); }
            });

            Run("Personal character settings dismiss outside without undoing automatically saved changes", () =>
            {
                var data = new AppData { MiniCharacterVisible = true };
                data.MiniExtraCharacters.Add(new MiniCharacterSlot { Manifest = "DefaultPets/mochi-blue/pet.json" });
                var store = Store("pet-settings-dismissal");
                var mini = new MiniWindow(data, () => { store.SaveData(data); return true; }, () => { });
                var pet = new PetSettingsWindow(mini, 1);
                var embedded = new PetSettingsWindow(mini, 0, embedded: true);
                int closed = 0;
                pet.Closed += (s, e) => closed++;
                try
                {
                    var dismissal = (AuxiliaryWindowDismissal)Field(pet, "dismissal");
                    var timer = (DispatcherTimer)Field(dismissal, "timer");
                    Require(Field(embedded, "dismissal") == null, "Embedded character settings installed a second focus watcher.");
                    Control<Slider>(pet, "ScaleSlider").Value = 170;
                    Control<CheckBox>(pet, "FlipSwitch").IsChecked = true;
                    Call(pet, "Flip_Click", pet, ClickArgs());
                    dismissal.CanDismissOverride = () => false;
                    Call(dismissal, "Window_Deactivated", pet, EventArgs.Empty);
                    Call(dismissal, "RecheckDismissal", null, EventArgs.Empty);
                    Require(closed == 0 && timer.IsEnabled, "A related control dismissed the character editor.");
                    dismissal.CanDismissOverride = () => true;
                    Call(dismissal, "RecheckDismissal", null, EventArgs.Empty);
                    Require(closed == 1 && !timer.IsEnabled, "Outside activity did not close the character editor.");
                    Call(mini, "FlushSave");
                    var saved = store.LoadData().Data;
                    Require(saved.MiniExtraCharacters[0].Scale == 170 && saved.MiniExtraCharacters[0].Flipped && saved.MiniCharacterScale == 100,
                        "Automatic dismissal undid an immediate change or changed another character.");
                    Call(dismissal, "Window_Deactivated", pet, EventArgs.Empty);
                    Call(dismissal, "RecheckDismissal", null, EventArgs.Empty);
                    Require(closed == 1 && !timer.IsEnabled && !pet.IsVisible, "Closing revived the character editor's timer or displayed a window.");
                }
                finally { pet.Close(); embedded.Close(); mini.Close(); }
            });

            Run("Personal move closes the settings editor and enables dragging only that character", () =>
            {
                var data = new AppData { MiniCharacterVisible = true };
                data.MiniExtraCharacters.Add(new MiniCharacterSlot { Manifest = "DefaultPets/mochi-blue/pet.json" });
                var mini = new MiniWindow(data, () => true, () => { });
                var pet = new PetSettingsWindow(mini, 1);
                bool closed = false;
                pet.Closed += (s, e) => closed = true;
                try
                {
                    Control<Button>(pet, "PlaceButton").RaiseEvent(ClickArgs());
                    var canDrag = typeof(MiniWindow).GetMethod("CanDragPet", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    Require(closed && (bool)canDrag.Invoke(mini, new object[] { 1 }) && !(bool)canDrag.Invoke(mini, new object[] { 0 }),
                        "Moving one character did not close its editor or enabled dragging the wrong character.");
                    var dismissal = (AuxiliaryWindowDismissal)Field(pet, "dismissal");
                    Require(!((DispatcherTimer)Field(dismissal, "timer")).IsEnabled, "Starting placement left a dismissal timer running.");
                    mini.EndPetPlacement();
                }
                finally { pet.Close(); mini.Close(); }
            });
        }

        private sealed class SettingsFocusWindows : IDisposable
        {
            private readonly List<IntPtr> windows = new List<IntPtr>();
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string title, uint style,
                int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
            [DllImport("user32.dll")] internal static extern bool EnableWindow(IntPtr window, bool enabled);
            [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);

            internal IntPtr Create(IntPtr owner = default(IntPtr), bool child = false)
            {
                var window = CreateWindowEx(0, "STATIC", "Isolated settings focus check", child ? 0x40000000u : 0x80000000u,
                    -32000, -32000, 80, 60, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Require(window != IntPtr.Zero, "Could not create a hidden focus-check window.");
                windows.Add(window);
                return window;
            }

            public void Dispose()
            {
                for (int i = windows.Count - 1; i >= 0; i--) DestroyWindow(windows[i]);
            }
        }
    }
}
