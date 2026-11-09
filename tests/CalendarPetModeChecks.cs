using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Newtonsoft.Json;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static Rect[] ScreenPets(MiniWindow mini) => CalendarValue<List<Rect>>(mini, "PetRects")
            .Select(r => new Rect(r.X + mini.Left, r.Y + mini.Top, r.Width, r.Height)).ToArray();

        private static Rect[] BoardPets(MiniWindow mini)
        {
            var board = CalendarBoard(mini);
            return ScreenPets(mini).Select(r => new Rect(r.X - board.Left, r.Y - board.Top, r.Width, r.Height)).ToArray();
        }

        private static void PlaceTestPets(MiniWindow mini, AppData data, params MiniPetSpot[] spots)
        {
            var keys = MiniWindow.SlotKeysOf(data);
            data.MiniPetSpots = spots.Select(MiniWindow.CopySpot).ToList();
            for (int i = 0; i < data.MiniPetSpots.Count; i++) data.MiniPetSpots[i].Key = keys[i];
            MiniWindow.RememberMiniSpots(data);
            Call(mini, "ApplyPetLayout", false, CalendarBoard(mini));
            LayoutCalendarMode(mini);
        }

        private static bool NearPet(Rect a, Rect b) => Math.Abs(a.X - b.X) < 1.1 && Math.Abs(a.Y - b.Y) < 1.1 &&
            Math.Abs(a.Width - b.Width) < 1.1 && Math.Abs(a.Height - b.Height) < 1.1;

        private static AppData PetModeData(double top = 100) => new AppData
        {
            MiniCharacterVisible = true, MiniPlayerVisible = false, MiniCharacterScale = 100,
            CharacterManifest = "DefaultPets/mochi-white/pet.json",
            MiniBoard = new MonitorStateData { Left = 500, Top = top, Width = 800, Height = 312 }
        };

        private static void CalendarPetEdgeAnchors()
        {
            foreach (double top in new[] { 200.0, 700.0 })
            {
                var data = PetModeData(top);
                data.MiniExtraCharacters.Add(new MiniCharacterSlot { Manifest = "DefaultPets/mochi-blue/pet.json" });
                data.MiniExtraCharacters.Add(new MiniCharacterSlot { Manifest = "DefaultPets/mochi-red/pet.json" });
                var mini = new MiniWindow(data, () => true, () => { });
                SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1040)));
                try
                {
                    LayoutCalendarMode(mini);
                    double height = ScreenPets(mini)[2].Height;
                    PlaceTestPets(mini, data, new MiniPetSpot { EdgeX = "L", OffsetX = -14, Top = 45 },
                        new MiniPetSpot { EdgeX = "R", OffsetX = 18, Top = 15 },
                        new MiniPetSpot { Left = 42, Top = (12 - height) * 100 / 312 }); // Feet overlap the top frame.
                    var original = ScreenPets(mini); var board = CalendarBoard(mini);
                    double gap = original[2].Bottom - board.Top;
                    for (int round = 0; round < 6; round++)
                    {
                        mini.SetMonthViewOpen(true); LayoutCalendarMode(mini);
                        var month = ScreenPets(mini); var monthBoard = CalendarBoard(mini);
                        Require(NearPet(month[0], original[0]) && NearPet(month[1], original[1]), "A side pet moved or resized during expansion.");
                        Require(Math.Abs(month[2].Bottom - monthBoard.Top - gap) < 1.1 && Math.Abs(month[2].X - original[2].X) < 1.1 &&
                            month[2].Size == original[2].Size, "A pet slightly overlapping the top frame did not follow it at a fixed size/gap.");
                        mini.SetMonthViewOpen(false); LayoutCalendarMode(mini);
                        Require(ScreenPets(mini).Zip(original, NearPet).All(x => x), "Round trips accumulated a placement error.");
                    }
                    // Bottom edge follows the bottom, including a small overlap into the board.
                    SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1440)));
                    PlaceTestPets(mini, data, new MiniPetSpot { EdgeX = "L", OffsetX = -14, Top = 45 },
                        new MiniPetSpot { EdgeX = "R", OffsetX = 18, Top = 15 }, new MiniPetSpot { Left = 42, Top = 300.0 / 312 * 100 });
                    mini.SetMonthViewOpen(true); LayoutCalendarMode(mini);
                    Require(Math.Abs(ScreenPets(mini)[2].Top - (CalendarBoard(mini).Top + CalendarBoard(mini).Height) + 12) < 1.1,
                        "The bottom pet lost its distance from the bottom edge.");
                }
                finally { mini.Close(); }
            }
        }

        private static void CalendarPetModeMemory()
        {
            var data = PetModeData(650);
            data.MiniExtraCharacters.Add(new MiniCharacterSlot { Manifest = data.CharacterManifest }); // Same pet, independent copy.
            var mini = new MiniWindow(data, () => true, () => { });
            SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1040)));
            var store = Store("pet-mode-memory");
            try
            {
                PlaceTestPets(mini, data, new MiniPetSpot { Left = 20, Top = 20 }, new MiniPetSpot { Left = 60, Top = 35 });
                var compact = ScreenPets(mini);
                var compactRelative = BoardPets(mini);
                mini.SetMonthViewOpen(true); LayoutCalendarMode(mini);
                Require(ScreenPets(mini).Zip(compact, NearPet).All(x => x), "The first month view moved an inside pet.");
                string rememberedCompact = JsonConvert.SerializeObject(data.MiniCompactPetLayout);
                PlaceTestPets(mini, data, new MiniPetSpot { Left = 65, Top = 20 }, new MiniPetSpot { Left = 20, Top = 65 });
                var month = ScreenPets(mini);
                var monthRelative = BoardPets(mini);
                Require(JsonConvert.SerializeObject(data.MiniCompactPetLayout) == rememberedCompact, "Moving a monthly pet modified the compact copy.");
                mini.SetMonthViewOpen(false); LayoutCalendarMode(mini);
                Require(ScreenPets(mini).Zip(compact, NearPet).All(x => x), "Compact mode did not restore its own placement.");
                mini.SetMonthViewOpen(true); LayoutCalendarMode(mini);
                Require(ScreenPets(mini).Zip(month, NearPet).All(x => x), "Monthly mode did not restore its own placement.");
                store.SaveData(data);
                var restored = store.LoadData().Data;
                Require(restored.MiniMonthView && restored.MiniCompactPetLayout != null && restored.MiniMonthPetLayout != null,
                    "Mode memories were not persisted.");
                var reopened = new MiniWindow(restored, () => true, () => { });
                SetField(reopened, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1040)));
                try
                {
                    LayoutCalendarMode(reopened);
                    // Startup may fit the board to the actual monitor before the isolated area override is installed.
                    // The saved positions belong to the board, and must follow that legitimate movement together.
                    Require(BoardPets(reopened).Zip(monthRelative, NearPet).All(x => x), "Restart lost the active monthly placement.");
                    reopened.SetMonthViewOpen(false); LayoutCalendarMode(reopened);
                    Require(BoardPets(reopened).Zip(compactRelative, NearPet).All(x => x), "Restart lost the inactive compact placement or mixed up duplicate pets.");
                    Require(restored.MiniPetSpots[0].Key != restored.MiniPetSpots[1].Key, "Duplicate characters share a mode memory key.");
                }
                finally { reopened.Close(); }
            }
            finally { mini.Close(); }
        }

        private static void CalendarPetScreenCorrection()
        {
            var area = new Rect(-1600, 0, 1600, 1040);
            var data = PetModeData(60); data.MiniBoard.Left = -1580;
            data.MiniExtraCharacters.Add(new MiniCharacterSlot { Manifest = "DefaultPets/mochi-blue/pet.json" });
            var mini = new MiniWindow(data, () => true, () => { });
            SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => area));
            try
            {
                PlaceTestPets(mini, data, new MiniPetSpot { Left = 40, EdgeY = "T", OffsetY = 6 },
                    new MiniPetSpot { EdgeX = "L", OffsetX = -30, Top = 40 });
                var sizes = ScreenPets(mini).Select(r => r.Size).ToArray();
                mini.SetMonthViewOpen(true); LayoutCalendarMode(mini);
                var visible = ScreenPets(mini);
                Require(visible.All(area.Contains) && Math.Abs(visible[0].Top - area.Top) < 1.1 && Math.Abs(visible[1].Left - area.Left) < 1.1,
                    "Offscreen pets were not corrected by the minimum distance on a negative-coordinate monitor.");
                Require(visible.Select(r => r.Size).SequenceEqual(sizes), "Screen correction resized a pet.");
                Require(data.MiniPetSpots[0].EdgeY == "T" && data.MiniPetSpots[0].OffsetY == 6 && data.MiniPetSpots[1].OffsetX == -30,
                    "Screen correction overwrote the preferred edge gaps.");
                mini.SetMonthViewOpen(false); LayoutCalendarMode(mini);
                Require(data.MiniPetSpots[0].OffsetY == 6 && data.MiniPetSpots[1].OffsetX == -30, "Returning from a constrained mode lost the original placement.");
                var moved = CalendarBoard(mini); moved.Left += 220; moved.Top += 200;
                Call(mini, "ApplyPetLayout", false, moved); LayoutCalendarMode(mini);
                var unconstrained = ScreenPets(mini);
                Require(Math.Abs(unconstrained[0].Bottom - moved.Top - 6) < 1.1 && Math.Abs(unconstrained[1].Right - moved.Left + 30) < 1.1,
                    "Preferred placement did not return when screen space became available.");
            }
            finally { mini.Close(); }
        }

        private static void CalendarPetInterruptedMode()
        {
            var data = PetModeData(650);
            var mini = new MiniWindow(data, () => true, () => { });
            SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1040)));
            try
            {
                PlaceTestPets(mini, data, new MiniPetSpot { EdgeX = "L", OffsetX = -18, Top = 50 });
                var original = ScreenPets(mini)[0]; var from = CalendarBoard(mini);
                var target = new MonitorStateData { Left = from.Left, Width = from.Width, Height = 780, Top = from.Top - 468 };
                Call(mini, "BeginCalendarModeTransition", true, from, target, new DrawingImage());
                Require(NearPet(ScreenPets(mini)[0], original) && Field(mini, "pendingCalendarPetLayout") == null,
                    "The animated destination used stale positions or never activated its prepared placement.");
                mini.SetMonthViewOpen(false); LayoutCalendarMode(mini);
                Require(NearPet(ScreenPets(mini)[0], original) && !mini.IsMonthViewOpen && !(bool)Field(mini, "changingCalendarMode"),
                    "Interrupting the mode animation moved a side pet or left the mode unsettled.");
                Require(!mini.IsVisible, "Position checks opened the widget.");
            }
            finally { mini.Close(); }
        }

        private static void CalendarPetFractionalPositions()
        {
            var data = PetModeData(650.35);
            data.MiniBoard.Width = 793.882727;
            data.MiniBoard.Height = 315.057062;
            var mini = new MiniWindow(data, () => true, () => { });
            SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1040)));
            try
            {
                PlaceTestPets(mini, data, new MiniPetSpot { EdgeX = "L", OffsetX = -16.35, Top = 47.4 });
                var original = ScreenPets(mini)[0];
                for (int i = 0; i < 24; i++)
                {
                    mini.SetMonthViewOpen(i % 2 == 0); LayoutCalendarMode(mini);
                    var now = ScreenPets(mini)[0];
                    Require(Math.Abs(now.X - original.X) < .01 && Math.Abs(now.Y - original.Y) < .01 && now.Size == original.Size,
                        "Fractional board geometry accumulated pet movement: " + original + " -> " + now);
                }
            }
            finally { mini.Close(); }
        }

        private static void CalendarPetLegacyMonth()
        {
            var data = PetModeData(400);
            data.MiniMonthView = true;
            data.MiniMonthBoard = new MonitorStateData { Left = 500, Top = 24, Width = 800, Height = 780 };
            data.MiniPetSpots.Add(new MiniPetSpot { Left = 40, Top = 30 });
            var mini = new MiniWindow(data, () => true, () => { });
            SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1040)));
            try
            {
                LayoutCalendarMode(mini);
                var original = BoardPets(mini)[0];
                Require(data.MiniCompactPetLayout == null && data.MiniMonthPetLayout == null && data.MiniPetSpots[0].Top == 30,
                    "Loading old monthly data replaced the user's active placement.");
                mini.SetMonthViewOpen(false); LayoutCalendarMode(mini);
                mini.SetMonthViewOpen(true); LayoutCalendarMode(mini);
                Require(NearPet(BoardPets(mini)[0], original), "Legacy monthly placement was not remembered on its first round trip.");
            }
            finally { mini.Close(); }
        }

        private static void CalendarPetSettingsCancellation()
        {
            var data = PetModeData(200);
            var main = new MainWindow();
            ((TrayService)Field(main, "trayService")).Dispose();
            SetField(main, "dataStore", Store("pet-mode-settings"));
            SetField(main, "appData", data);
            var mini = new MiniWindow(data, () => true, () => { });
            SetField(main, "miniWindow", mini);
            SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1040)));
            try
            {
                PlaceTestPets(mini, data, new MiniPetSpot { Left = 20, Top = 30 });
                mini.SetMonthViewOpen(true); LayoutCalendarMode(mini);
                PlaceTestPets(mini, data, new MiniPetSpot { Left = 60, Top = 40 });
                var monthly = ScreenPets(mini)[0];
                mini.SetMonthViewOpen(false); LayoutCalendarMode(mini);
                OpenSettingsForCheck(main);
                mini.SetMonthViewOpen(true); LayoutCalendarMode(mini);
                Call(main, "CloseInlineSettings", false);
                Require(mini.IsMonthViewOpen && NearPet(ScreenPets(mini)[0], monthly), "Cancelling older settings restored compact placement over the monthly mode.");
                mini.SetMonthViewOpen(false); LayoutCalendarMode(mini);
                Require(data.MiniPetSpots[0].Left == 20 && data.MiniPetSpots[0].Top == 30, "Settings cancellation changed the inactive mode's placement.");
            }
            finally
            {
                if (Field(main, "settingsHost") != null) Call(main, "CloseInlineSettings", false);
                main.Close(); mini.Close();
            }
        }
    }
}
