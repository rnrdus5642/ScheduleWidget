using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Newtonsoft.Json;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static DisplayMonitor Screen(string id, double x, double y, double width, double height, double scale = 1, bool primary = false) =>
            new DisplayMonitor { Id = id, Name = "Same monitor model", DeviceName = id, Primary = primary,
                BoundsPixels = new Rect(x, y, width, height), WorkPixels = new Rect(x, y, width, height), ScaleX = scale, ScaleY = scale };

        private static CalendarMonitorLayout Layout(double width = 800, double height = 300, double x = .8, double y = .9) =>
            new CalendarMonitorLayout { Name = "Saved", ChangedUtc = new DateTime(2026, 1, 1),
                Compact = new CalendarPlacement { Width = width, Height = height, Horizontal = x, Vertical = y },
                Month = new CalendarPlacement { Width = width, Height = 900, Horizontal = x, Vertical = .5 } };

        private static string PreferredLayouts(AppData data) => data.MiniPreferredMonitorId + "|" + JsonConvert.SerializeObject(data.MiniMonitorLayouts);
        private static bool Near(double a, double b) => Math.Abs(a - b) < .01;
        private static void BoardEquals(MonitorStateData actual, MonitorStateData expected, string message) =>
            Require(Near(actual.Left, expected.Left) && Near(actual.Top, expected.Top) && Near(actual.Width, expected.Width) && Near(actual.Height, expected.Height), message);

        private sealed class MonitorCalendar : IDisposable
        {
            internal readonly AppData Data;
            internal readonly MiniWindow Window;
            internal List<DisplayMonitor> Screens;
            internal int Saves;
            internal MonitorCalendar(AppData data, params DisplayMonitor[] screens)
            {
                Data = data; Screens = screens.ToList();
                Window = new MiniWindow(data, () => { Saves++; return true; }, () => { });
                Window.monitorSnapshotOverride = () => Screens;
                Window.monitorBoardPixelsOverride = () =>
                {
                    var board = CalendarBoard(Window);
                    return new Rect(board.Left, board.Top, board.Width, board.Height);
                }; // move/resize fixtures use 100% virtual monitors, independent of the test machine's real DPI
                SetField(Window, "calendarWorkAreaOverride", (Func<Rect>)(() =>
                    CalendarPlacementPolicy.Select(Screens, Data.MiniPreferredMonitorId, (string)Field(Window, "activeMonitorId"))?.WorkArea
                        ?? new Rect(0, 0, 1920, 1040)));
                Window.InitializeMonitorPlacement(); LayoutCalendarMode(Window);
            }
            internal void Change(params DisplayMonitor[] screens)
            {
                Screens = screens.ToList(); Window.RestoreMonitorPlacement("check"); LayoutCalendarMode(Window);
            }
            public void Dispose() { Window.Close(); }
        }

        private static AppData MonitorData(string preferred, CalendarMonitorLayout layout) => new AppData
        {
            MiniCharacterVisible = false, MiniPlayerVisible = false, MiniPreferredMonitorId = preferred,
            MiniMonitorLayouts = new Dictionary<string, CalendarMonitorLayout> { [preferred] = layout }
        };

        private static void MonitorResolutionRestoration()
        {
            var full = Screen("monitor:A", 0, 0, 2560, 1400, primary: true);
            var data = MonitorData(full.Id, Layout(1800, 900));
            using (var calendar = new MonitorCalendar(data, full))
            {
                var original = CalendarBoard(calendar.Window); string preferred = PreferredLayouts(data);
                for (int i = 0; i < 12; i++)
                {
                    calendar.Change(Screen(full.Id, 0, 0, 800, 560, primary: true));
                    var fitted = CalendarBoard(calendar.Window);
                    Require(fitted.Width <= 800 && fitted.Height <= 560 && fitted.Left >= 0 && fitted.Top >= 0,
                        "A low-resolution remote screen did not fit the full calendar.");
                    Call(calendar.Window, "SavePosition"); Call(calendar.Window, "FlushSave");
                    Require(PreferredLayouts(data) == preferred, "Automatic fitting overwrote the user's preferred placement.");
                    calendar.Change(full);
                    BoardEquals(CalendarBoard(calendar.Window), original, "Restoring the original resolution drifted or lost the saved dimensions.");
                }
                calendar.Change(Screen(full.Id, -2560, -200, 2560, 1400, primary: true));
                var moved = CalendarBoard(calendar.Window);
                Require(Near(moved.Left, original.Left - 2560) && Near(moved.Top, original.Top - 200),
                    "Rearranging monitors did not preserve the location within the same monitor.");
                Require(PreferredLayouts(data) == preferred, "Rearranging monitors changed stored preferences.");
            }
        }

        private static void MonitorDisconnectAndRestart()
        {
            var a = Screen("monitor:A", 0, 0, 1920, 1040, primary: true);
            var b = Screen("monitor:B", 1920, 0, 2560, 1400);
            var data = MonitorData(b.Id, Layout(1100, 420));
            data.MiniMonitorLayouts[a.Id] = Layout(700, 260, .1, .2);
            string preferred = PreferredLayouts(data);
            var store = Store("monitor-disconnect");
            using (var calendar = new MonitorCalendar(data, a, b))
            {
                calendar.Change(a);
                BoardEquals(CalendarBoard(calendar.Window), CalendarPlacementPolicy.Resolve(data.MiniMonitorLayouts[a.Id].Compact, a.WorkArea, false),
                    "The fallback monitor did not use its own saved layout.");
                Call(calendar.Window, "SavePosition"); store.SaveData(data);
                Require(PreferredLayouts(data) == preferred, "Disconnecting the preferred monitor selected the fallback permanently.");
            }
            var loaded = store.LoadData().Data;
            using (var restarted = new MonitorCalendar(loaded, a))
            {
                Require(loaded.MiniPreferredMonitorId == b.Id, "Restarting during a remote fallback forgot the preferred monitor.");
                restarted.Change(a, b);
                BoardEquals(CalendarBoard(restarted.Window), CalendarPlacementPolicy.Resolve(loaded.MiniMonitorLayouts[b.Id].Compact, b.WorkArea, false),
                    "Reconnecting did not restore the original monitor's placement.");
                Require(PreferredLayouts(loaded) == preferred, "Fallback or restoration changed stored preferences.");
            }
        }

        private static void MonitorUserIntent()
        {
            var a = Screen("monitor:A", 0, 0, 1920, 1040, primary: true);
            var b = Screen("monitor:B", 1920, 0, 2560, 1400);
            var data = MonitorData(b.Id, Layout(1200, 500));
            string originalB = JsonConvert.SerializeObject(data.MiniMonitorLayouts[b.Id]);
            using (var calendar = new MonitorCalendar(data, a))
            {
                Require(data.MiniMonitorLayouts.Count == 1 && data.MiniPreferredMonitorId == b.Id, "An unfamiliar fallback was saved without user action.");
                calendar.Window.Left = 100; calendar.Window.Top = 80; LayoutCalendarMode(calendar.Window);
                Require(calendar.Window.CommitUserMonitorPlacement(false), "An explicit move on the fallback could not be saved.");
                Require(data.MiniPreferredMonitorId == a.Id && data.MiniMonitorLayouts.Count == 2, "A deliberate move did not choose the current monitor.");
                var chosen = CalendarBoard(calendar.Window);
                calendar.Change(a, b);
                BoardEquals(CalendarBoard(calendar.Window), chosen, "Reconnecting the old monitor undid a later user move.");
                Require(JsonConvert.SerializeObject(data.MiniMonitorLayouts[b.Id]) == originalB, "Using another monitor overwrote the old layout.");

                // Dragging onto B keeps B's preferred dimensions, while the drop position becomes its new location.
                calendar.Window.Left = 2200; calendar.Window.Top = 100; LayoutCalendarMode(calendar.Window);
                Require(calendar.Window.CommitUserMonitorPlacement(false), "Moving to the second monitor failed.");
                Require(data.MiniPreferredMonitorId == b.Id && Near(CalendarBoard(calendar.Window).Width, 1200), "Moving copied the first monitor's size over the second's.");
                calendar.Window.Width = 908; calendar.Window.Height = 368; LayoutCalendarMode(calendar.Window);
                Require(calendar.Window.CommitUserMonitorPlacement(true), "An explicit resize did not commit.");
                var saved = data.MiniMonitorLayouts[b.Id];
                Require(Near(saved.Compact.Width, 900) && Near(saved.Compact.Height, 360) && Near(saved.Month.Width, 900) && Near(saved.Month.Height, 900),
                    "Resizing did not share the width or preserve the other mode's height.");
            }
        }

        private static void MonitorModesAndDpi()
        {
            var monitor = Screen("monitor:A", 0, 0, 2560, 1440, primary: true);
            var data = MonitorData(monitor.Id, Layout(1500, 400));
            using (var calendar = new MonitorCalendar(data, monitor))
            {
                string preferred = PreferredLayouts(data);
                calendar.Change(Screen(monitor.Id, 0, 0, 1920, 1080, 1.5, true));
                var fitted = CalendarBoard(calendar.Window);
                Require(fitted.Width <= 1280 && fitted.Height <= 720, "DPI fitting used physical pixels as logical dimensions.");
                calendar.Window.SetMonthViewOpen(true); LayoutCalendarMode(calendar.Window);
                Require(CalendarBoard(calendar.Window).Height <= 720 && PreferredLayouts(data) == preferred,
                    "Monthly fitting on a high-DPI screen rewrote the preferred height.");
                calendar.Change(monitor);
                Require(Near(CalendarBoard(calendar.Window).Width, 1500) && Near(CalendarBoard(calendar.Window).Height, 900),
                    "Returning to the original DPI lost the full monthly size.");
                calendar.Window.SetMonthViewOpen(false); LayoutCalendarMode(calendar.Window);
                Require(Near(CalendarBoard(calendar.Window).Height, 400) && PreferredLayouts(data) == preferred,
                    "Mode switching lost the preferred compact height.");
            }
        }

        private static void MonitorLegacyAndTransientStates()
        {
            var a = Screen("monitor:A", 0, 0, 1920, 1040, primary: true);
            var b = Screen("monitor:B", -1920, 0, 1920, 1040);
            var legacy = new AppData { MiniCharacterVisible = false, MiniPlayerVisible = false,
                MiniBoard = new MonitorStateData { Left = -1500, Top = 500, Width = 800, Height = 300 },
                MiniMonthBoard = new MonitorStateData { Left = -1500, Top = 80, Width = 800, Height = 850 } };
            using (var calendar = new MonitorCalendar(legacy, a, b))
            {
                Require(legacy.MiniPreferredMonitorId == b.Id && legacy.MiniMonitorLayouts.Count == 1,
                    "Legacy negative-coordinate placement was not migrated to its monitor.");
                BoardEquals(CalendarBoard(calendar.Window), new MonitorStateData { Left = -1500, Top = 500, Width = 800, Height = 300 },
                    "Migration changed the existing calendar placement.");
                string preferred = PreferredLayouts(legacy); var board = CalendarBoard(calendar.Window);
                calendar.Change();
                BoardEquals(CalendarBoard(calendar.Window), board, "An empty display enumeration moved the calendar.");
                Require(PreferredLayouts(legacy) == preferred, "An empty display enumeration erased profiles.");
                SetField(calendar.Window, "movingWindow", true);
                calendar.Change(a);
                BoardEquals(CalendarBoard(calendar.Window), board, "A topology refresh moved the window during a pointer operation.");
                Call(calendar.Window, "ResetPointerForScreenChange");
                calendar.Window.RestoreMonitorPlacement("after-cancel"); LayoutCalendarMode(calendar.Window);
                Require(legacy.MiniPreferredMonitorId == b.Id && PreferredLayouts(legacy) == preferred,
                    "Cancelling a pointer operation during reconnect saved the temporary layout.");
            }
            var legacyMonth = new AppData { MiniCharacterVisible = false, MiniPlayerVisible = false, MiniMonthView = true,
                MiniBoard = new MonitorStateData { Left = -1500, Top = 500, Width = 800, Height = 300 },
                MiniMonthBoard = new MonitorStateData { Left = -1500, Top = 80, Width = 800, Height = 850 } };
            using (var calendar = new MonitorCalendar(legacyMonth, a, b))
            {
                Require(legacyMonth.MiniPreferredMonitorId == b.Id && Near(legacyMonth.MiniMonitorLayouts[b.Id].Month.Height, 850) &&
                    Near(legacyMonth.MiniMonitorLayouts[b.Id].Compact.Height, 300), "Starting in monthly mode lost the unmodified legacy geometry during migration.");
                Require(Near(CalendarBoard(calendar.Window).Height, 850), "Monthly migration used the temporary screen fit as the preferred size.");
            }
        }

        private static void MonitorDataValidation()
        {
            var data = MonitorData("monitor:preferred", Layout());
            data.MiniMonitorLayouts["monitor:preferred"].Compact.Width = double.NaN;
            data.MiniMonitorLayouts["monitor:preferred"].Month.Vertical = double.PositiveInfinity;
            data.MiniMonitorLayouts[" "] = Layout();
            data.MiniMonitorLayouts["null"] = null;
            for (int i = 0; i < 70; i++) data.MiniMonitorLayouts["monitor:" + i] = Layout();
            var store = Store("monitor-validation"); store.SaveData(data);
            var loaded = store.LoadData().Data;
            Require(loaded.MiniMonitorLayouts.Count == 64 && loaded.MiniMonitorLayouts.ContainsKey("monitor:preferred") &&
                loaded.MiniPreferredMonitorId == "monitor:preferred", "Profile cleanup lost the preferred monitor or kept malformed records.");
            Require(loaded.MiniMonitorLayouts["monitor:preferred"].Compact.Width == 792 &&
                loaded.MiniMonitorLayouts["monitor:preferred"].Month.Vertical == .5, "Invalid placement values survived save/reload.");
            Require(CalendarPlacementPolicy.Select(new[] { Screen("one", 0, 0, 1920, 1040), Screen("two", 1920, 0, 1920, 1040) }, "two").Id == "two",
                "Two identically named monitors were confused.");
        }

        private static void MonitorFittedMovesAndOtherDisplays()
        {
            var a = Screen("monitor:A", 0, 0, 2560, 1400, primary: true);
            var b = Screen("monitor:B", 2560, 0, 1920, 1040);
            var data = MonitorData(a.Id, Layout(1800, 900));
            using (var calendar = new MonitorCalendar(data, a, b))
            {
                calendar.Change(Screen(a.Id, 0, 0, 640, 480, primary: true), b);
                Require(calendar.Window.CommitUserMonitorPlacement(false), "Moving a screen-fitted window failed.");
                Require(data.MiniMonitorLayouts[a.Id].Compact.Width == 1800 && data.MiniMonitorLayouts[a.Id].Compact.Height == 900,
                    "Moving a fitted window accidentally saved its temporary small size.");
                calendar.Change(a, b);
                Require(Near(CalendarBoard(calendar.Window).Width, 1800), "The preferred size was not restored after a constrained move.");
                calendar.Window.SetMonthViewOpen(true); LayoutCalendarMode(calendar.Window);
                var shown = CalendarBoard(calendar.Window); string preferred = PreferredLayouts(data);
                calendar.Change(a, Screen(b.Id, 2560, 0, 1280, 720));
                BoardEquals(CalendarBoard(calendar.Window), shown, "Changing a different display disturbed the current mode's live position.");
                Require(PreferredLayouts(data) == preferred, "An unrelated display change rewrote saved profiles.");
                calendar.Window.CenterOnPrimaryScreen();
                var centered = CalendarBoard(calendar.Window);
                Require(data.MiniPreferredMonitorId == a.Id && Near(centered.Left + centered.Width / 2, a.WorkArea.X + a.WorkArea.Width / 2) &&
                    Near(centered.Top + centered.Height / 2, a.WorkArea.Y + a.WorkArea.Height / 2), "Position reset did not save a centered placement on the primary monitor.");
                SetField(calendar.Window, "windowDragging", true);
                Call(calendar.Window, "NoteMonitorDragTarget", new System.Drawing.Point(2700, 100));
                Require(CalendarValue<string>(calendar.Window, "MonitorMoveTargetForDpi") == b.Id,
                    "An explicit cross-monitor drag was not distinguished from automatic screen fitting.");
                calendar.Screens = new List<DisplayMonitor> { Screen(a.Id, 0, 0, 1600, 900, primary: true), b };
                Require(CalendarValue<string>(calendar.Window, "MonitorMoveTargetForDpi") == null,
                    "A topology change during a drag was mistaken for a deliberate monitor selection.");
                SetField(calendar.Window, "windowDragging", false);
            }
        }
    }
}
