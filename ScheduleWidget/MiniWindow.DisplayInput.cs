using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ScheduleWidget
{
    public partial class MiniWindow
    {
        private bool resettingScreenInput, screenInputLayoutPending;

        internal void ResetPointerForScreenChange()
        {
            if (closed || resettingScreenInput) return;
            resettingScreenInput = true;
            try
            {
                FinishCalendarModeTransition();
                // RDP/display transitions can lose MouseUp/DragCompleted. Clear the interaction before releasing
                // capture, so lost-capture callbacks do not save intermediate coordinates from the old monitor.
                bool interrupted = windowDragging || resizingBoard || draggingPet >= 0 || pressPoint.HasValue;
                screenInputLayoutPending |= resizingBoard || draggingPet >= 0;
                windowDragging = movingWindow = beginningDrag = resizingBoard = false;
                draggingPet = pressedPetSlot = previousPetClickSlot = -1;
                pressedPetClickCount = 0;
                pressPoint = null;
                gripStartPets = null;
                var captured = Mouse.Captured as DependencyObject;
                var popups = new[] { PlaylistPopup, VolumePopup, RangePopup, QueuePopup, BlockPopup, DayPopup, UpcomingPopup };
                bool ours = captured != null && (IsWithin(captured, this) || popups.Any(p => p?.Child != null && IsWithin(captured, p.Child)));
                if (ours) Mouse.Capture(null);
                FinishWeekFlip();
                if (interrupted || ours) PetLog.Write("display-input", "interrupted pointer interaction released");
            }
            finally { resettingScreenInput = false; }
        }

        private void RecoverScreenInputLayout()
        {
            if (closed || !screenInputLayoutPending) return;
            screenInputLayoutPending = false;
            UpdateCharacterSize();
            if (IsLoaded) { KeepOnScreen(); SavePosition(); }
        }
    }
}
