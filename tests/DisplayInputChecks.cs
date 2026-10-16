using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Newtonsoft.Json;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static void PopupClicksOverPets()
        {
            int saves = 0;
            var data = new AppData { MiniCharacterVisible = true, MiniPlayerVisible = false, MiniCharacterScale = 116,
                MiniBoard = new MonitorStateData { Left = 200, Top = 400, Width = 794, Height = 315 } };
            var mini = new MiniWindow(data, () => { saves++; return true; }, () => { });
            SetField(mini, "calendarWorkAreaOverride", (Func<Rect>)(() => new Rect(0, 0, 1920, 1040)));
            try
            {
                var rescue = typeof(MiniWindow).GetMethod("ShouldRescuePetClick", BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (bool month in new[] { false, true })
                {
                    mini.SetMonthViewOpen(month); LayoutCalendarMode(mini);
                    var hit = Control<Border>(mini, "PetHit0");
                    Require(hit.Visibility == Visibility.Visible && hit.Width > 0 && hit.Height > 0, "The overlap regression needs a visible pet hit box.");
                    var overlap = new Point(Canvas.GetLeft(hit) + hit.Width / 2, Canvas.GetTop(hit) + hit.Height / 2);
                    bool Rescued(DependencyObject source, DependencyObject captured) =>
                        (bool)rescue.Invoke(mini, new object[] { source, captured, overlap });
                    foreach (int day in new[] { 12, 13 })
                    {
                        var date = new DateTime(2026, 10, day); // Monday and Tuesday
                        DayDetails(mini, date);
                        var bubble = Control<FrameworkElement>(mini, "DayBubble");
                        var add = Control<Button>(mini, "NewMiniScheduleButton");
                        Require(!Rescued(add, bubble), "The new-schedule button is diverted to a pet when the popup overlaps it.");
                        foreach (var source in VisualDescendants<FrameworkElement>(bubble))
                            Require(!Rescued(source, bubble), "A popup control still triggers the pet click recovery: " + source.GetType().Name);
                        var nestedCalendar = new Calendar(); // A DatePicker's nested popup has a separate visual tree too.
                        Require(!Rescued(nestedCalendar, nestedCalendar), "The date-picker popup loses its click to a pet.");
                        add.RaiseEvent(ClickArgs());
                        Require(Control<FrameworkElement>(mini, "MiniEditorPanel").Visibility == Visibility.Visible &&
                            Control<DatePicker>(mini, "MiniDateInput").SelectedDate == date, "New schedule did not open with the selected date.");
                        Control<TextBox>(mini, "MiniTitleInput").Text = "Popup overlap check";
                        Call(mini, "SaveMiniSchedule_Click", mini, ClickArgs());
                        Require(data.Schedules.Count(s => s.Period == date.ToString("yyyy-MM-dd")) == (month ? 2 : 1), "Saving a popup schedule used the wrong date.");
                    }
                    Require(Rescued(Control<Grid>(mini, "OuterRoot"), null), "Ordinary pet-edge clicks no longer recover.");
                    Require(Rescued(Control<ItemsControl>(mini, "WeekDays"), Control<ItemsControl>(mini, "WeekDays")), "Stale capture within the calendar no longer recovers.");
                    Require(!Rescued(hit, hit), "Normal pet clicks would be handled twice.");
                }
                Require(saves >= 4 && !mini.IsVisible && !Control<Popup>(mini, "DayPopup").IsOpen, "The regression check did not save or opened a live popup.");
            }
            finally { mini.Close(); }
        }

        private static void DisplayPointerReset()
        {
            var data = Data(); int saves = 0, restarts = 0;
            var mini = new MiniWindow(data, () => { saves++; return true; }, () => { });
            SetField(mini, "recoveryOverride", (Action<bool, string>)((recreate, reason) => restarts++));
            string before = JsonConvert.SerializeObject(data);
            try
            {
                for (int i = 0; i < 18; i++)
                {
                    SetField(mini, "windowDragging", true); SetField(mini, "movingWindow", true);
                    SetField(mini, "beginningDrag", true); SetField(mini, "resizingBoard", true);
                    SetField(mini, "draggingPet", 0); SetField(mini, "pressedPetSlot", 0);
                    SetField(mini, "pressPoint", (Point?)new Point(15, 20));
                    Call(mini, "PrepareCharacterDisplayChange");
                    Require(!(bool)Field(mini, "windowDragging") && !(bool)Field(mini, "movingWindow") &&
                        !(bool)Field(mini, "beginningDrag") && !(bool)Field(mini, "resizingBoard") &&
                        (int)Field(mini, "draggingPet") == -1 && (int)Field(mini, "pressedPetSlot") == -1 && Field(mini, "pressPoint") == null,
                        "Screen transition left a pointer operation latched.");
                    if (i % 3 == 2) Call(mini, "ResumeCharacterAfterDisplayChange");
                }
                Require(restarts == 6 && JsonConvert.SerializeObject(data) == before, "Repeated transitions changed data or failed to coalesce.");
                SetField(mini, "resettingScreenInput", true);
                var completed = new DragCompletedEventArgs(10, 20, true);
                Call(mini, "ResizeGrip_DragCompleted", mini, completed);
                SetField(mini, "resettingScreenInput", false);
                Require(completed.Handled && saves == 0 && JsonConvert.SerializeObject(data) == before,
                    "Cancelled capture saved geometry from the changing screen.");
            }
            finally { SetField(mini, "resettingScreenInput", false); mini.Close(); }
        }
    }
}
