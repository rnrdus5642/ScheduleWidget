using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using Newtonsoft.Json;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
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
