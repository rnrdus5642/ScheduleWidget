using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        // Routed key events on unshown controls: no keyboard injection or changes to the user's active IME.
        private sealed class ScheduleKeySource : PresentationSource
        {
            public override Visual RootVisual { get; set; }
            public override bool IsDisposed => false;
            protected override CompositionTarget GetCompositionTargetCore() => null;
        }

        private static KeyEventArgs ScheduleKey(TextBox input, Key key = Key.Enter, bool ime = false, bool repeat = false)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, new ScheduleKeySource(), Environment.TickCount, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            if (ime) typeof(KeyEventArgs).GetMethod("MarkImeProcessed", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(args, null);
            if (repeat) typeof(KeyEventArgs).GetMethod("SetRepeat", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(args, new object[] { true });
            input.RaiseEvent(args);
            return args;
        }

        private static void FinishScheduleKey(MiniWindow mini) =>
            mini.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);

        private static void PrepareNewSchedule(MiniWindow mini, DateTime date)
        {
            DayDetails(mini, date);
            Control<Button>(mini, "NewMiniScheduleButton").RaiseEvent(ClickArgs());
        }

        private static void CalendarScheduleEnter()
        {
            var data = new AppData { MiniCharacterVisible = false, MiniPlayerVisible = false };
            var store = Store("calendar-enter");
            int saves = 0;
            var mini = new MiniWindow(data, () => { store.SaveData(data); saves++; return true; }, () => { });
            try
            {
                var title = Control<TextBox>(mini, "MiniTitleInput");
                var time = Control<TextBox>(mini, "MiniTimeInput");
                foreach (bool month in new[] { false, true })
                {
                    mini.SetMonthViewOpen(month);
                    var date = new DateTime(2026, 11, month ? 12 : 11);
                    PrepareNewSchedule(mini, date);
                    int before = data.Schedules.Count;
                    title.Text = month ? "월간 일정" : "기본 일정";
                    Require(ScheduleKey(title).Handled && data.Schedules.Count == before + 1,
                        "Enter on the title did not add a schedule.");
                    Require(store.LoadData().Data.Schedules.Last().Period == date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        "Enter saved the wrong date or did not persist the schedule.");
                    PrepareNewSchedule(mini, date);
                    title.Text = "시간 일정";
                    ScheduleKey(title, repeat: true);
                    Require(data.Schedules.Count == before + 1, "Holding Enter added a second schedule.");
                    Control<CheckBox>(mini, "MiniTimeToggle").IsChecked = true;
                    time.Text = "25:99";
                    ScheduleKey(time);
                    Require(data.Schedules.Count == before + 1 && Control<TextBlock>(mini, "MiniEditorMessage").Text.Contains("시간"),
                        "Enter bypassed time validation.");
                    time.Text = "09:25";
                    ScheduleKey(time);
                    Require(data.Schedules.Count == before + 2 && data.Schedules.Last().Time == "09:25",
                        "Enter on the time field did not save the corrected time.");
                }
                Require(saves == 4 && !mini.IsVisible && !Control<System.Windows.Controls.Primitives.Popup>(mini, "DayPopup").IsOpen,
                    "Enter saved more than once or the headless check displayed a popup.");
            }
            finally { mini.Close(); }
        }

        private static void KoreanScheduleEnter()
        {
            var data = new AppData { MiniCharacterVisible = false, MiniPlayerVisible = false };
            var store = Store("korean-enter");
            int saves = 0;
            var mini = new MiniWindow(data, () => { store.SaveData(data); saves++; return true; }, () => { });
            try
            {
                var title = Control<TextBox>(mini, "MiniTitleInput");
                PrepareNewSchedule(mini, DateTime.Today);
                title.Text = "마지막 글";
                var enter = ScheduleKey(title, ime: true);
                ScheduleKey(title, ime: true); // duplicate notifications before composition completes
                Require(!enter.Handled && saves == 0, "The Enter handler interrupted IME composition or saved too early.");
                title.Text = "마지막 글자"; // the committed text arrives after the preview key event
                FinishScheduleKey(mini);
                Require(saves == 1 && store.LoadData().Data.Schedules.Single().Title == "마지막 글자",
                    "The committed Korean text was lost or saved more than once.");

                PrepareNewSchedule(mini, DateTime.Today);
                title.Text = "취소할 일정";
                ScheduleKey(title, Key.A, ime: true);
                FinishScheduleKey(mini);
                Require(saves == 1, "An ordinary IME typing key submitted the editor.");
                ScheduleKey(title, ime: true);
                Call(mini, "CancelMiniSchedule_Click", mini, ClickArgs());
                FinishScheduleKey(mini);
                Require(saves == 1, "A pending Enter submitted a cancelled editor.");

                PrepareNewSchedule(mini, DateTime.Today);
                title.Text = "닫힌 창의 일정";
                ScheduleKey(title, ime: true);
                mini.Close(); FinishScheduleKey(mini);
                Require(saves == 1, "A pending Enter submitted after the calendar closed.");
            }
            finally { if (!(bool)Field(mini, "closed")) mini.Close(); }
        }

        private static void ListScheduleEnter()
        {
            var main = new MainWindow();
            ((TrayService)Field(main, "trayService")).Dispose();
            var data = new AppData { StartupEnabled = false, BringToFrontHotKeyEnabled = false, MiniCharacterVisible = false };
            var store = Store("list-enter");
            SetField(main, "dataStore", store); SetField(main, "appData", data);
            try
            {
                Call(main, "ResetDateToToday");
                var title = Control<TextBox>(main, "TitleInput");
                title.Text = "목록에서 추가";
                ScheduleKey(title);
                Require(data.Schedules.Count == 1 && title.Text == "", "The list's title Enter did not add and clear the input.");
                title.Text = "시간 지정";
                Control<CheckBox>(main, "AddTimeToggle").IsChecked = true;
                var time = Control<TextBox>(main, "AddTimeInput"); time.Text = "10:30";
                ScheduleKey(time);
                Require(store.LoadData().Data.Schedules.Last().Time == "10:30", "The list's time Enter did not persist the schedule.");

                var item = data.Schedules[0];
                Call(main, "OpenInlineEdit", item);
                var edit = Control<TextBox>(main, "InlineEditTitleInput");
                edit.Text = "수정 마지";
                ScheduleKey(edit, ime: true);
                edit.Text = "수정 마지막";
                main.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
                Require(data.Schedules.Count == 2 && store.LoadData().Data.Schedules.Single(s => s.Id == item.Id).Title == "수정 마지막",
                    "IME Enter in the list editor lost the final text or inserted a duplicate.");
                Require(!main.IsVisible, "The list Enter check displayed the user's window.");
            }
            finally { SetField(main, "closingApp", true); main.Close(); }
        }
    }
}
