using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScheduleWidget
{
    public partial class MiniWindow
    {
        private bool monitorPlacementReady, applyingMonitorPlacement, monitorPlacementPending;
        private string activeMonitorId, monitorTopology, monitorDragTarget;
        private DisplayMonitor monitorApplyTarget;
        private List<DisplayMonitor> placementMonitors = new List<DisplayMonitor>();
        private MonitorStateData monitorSeedCompact, monitorSeedMonth;
        internal Func<List<DisplayMonitor>> monitorSnapshotOverride = null;
        internal Func<Rect> monitorBoardPixelsOverride = null;

        private static MonitorStateData CopyBoard(MonitorStateData board) => board == null ? null : new MonitorStateData
            { Left = board.Left, Top = board.Top, Width = board.Width, Height = board.Height };

        private void CaptureMonitorPlacementSeed()
        {
            monitorSeedCompact = CopyBoard(SavedCompactBoard(data));
            monitorSeedMonth = CopyBoard(data.MiniMonthBoard);
        }

        private List<DisplayMonitor> ReadPlacementMonitors() => (monitorSnapshotOverride?.Invoke() ?? DisplayMonitors.Read())
            .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Id) && !m.WorkPixels.IsEmpty && m.WorkPixels.Width > 0 &&
                m.WorkPixels.Height > 0 && IsFinite(m.ScaleX) && IsFinite(m.ScaleY) && m.ScaleX > 0 && m.ScaleY > 0).ToList();

        private static string MonitorTopology(IEnumerable<DisplayMonitor> monitors) =>
            string.Join("\n", monitors.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).Select(m => m.Signature));

        internal void InitializeMonitorPlacement()
        {
            if (monitorPlacementReady || closed || companion || appearancePreviewOnly) return;
            var monitors = ReadPlacementMonitors();
            if (monitors.Count == 0) return; // display enumeration can be empty during reconnect; preserve all saved data
            CalendarPlacementPolicy.Normalize(data);
            if (data.MiniMonitorLayouts.Count == 0)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                var seed = IsMonthViewOpen && ValidCalendarBoard(monitorSeedMonth) ? monitorSeedMonth : monitorSeedCompact;
                var target = ValidCalendarBoard(seed) ? CalendarPlacementPolicy.Nearest(monitors,
                    new Rect(seed.Left * dpi.DpiScaleX, seed.Top * dpi.DpiScaleY, seed.Width * dpi.DpiScaleX, seed.Height * dpi.DpiScaleY))
                    : CalendarPlacementPolicy.Select(monitors, null);
                var layout = CalendarPlacementPolicy.Defaults(target.Name);
                if (ValidCalendarBoard(monitorSeedCompact)) layout.Compact = CalendarPlacementPolicy.Capture(monitorSeedCompact, target.WorkArea);
                if (ValidCalendarBoard(monitorSeedMonth)) layout.Month = CalendarPlacementPolicy.Capture(monitorSeedMonth, target.WorkArea);
                else
                {
                    var compact = CalendarPlacementPolicy.Resolve(layout.Compact, target.WorkArea, false);
                    var expanded = AnchorCalendarBoard(compact, new MonitorStateData { Height = Math.Max(780, layout.Compact.Height) }, target.WorkArea, true, .5);
                    layout.Month = CalendarPlacementPolicy.Capture(expanded, target.WorkArea);
                    layout.Month.Height = Math.Max(780, layout.Compact.Height);
                }
                layout.Month.Width = layout.Compact.Width;
                layout.Month.Horizontal = layout.Compact.Horizontal;
                layout.ChangedUtc = DateTime.UtcNow;
                data.MiniMonitorLayouts[target.Id] = layout;
                data.MiniPreferredMonitorId = target.Id;
            }
            else if (data.MiniPreferredMonitorId == null)
            {
                var known = monitors.FirstOrDefault(m => data.MiniMonitorLayouts.ContainsKey(m.Id));
                if (known != null) data.MiniPreferredMonitorId = known.Id;
            }
            monitorPlacementReady = true;
            RestoreMonitorPlacement("startup", true);
        }

        private CalendarMonitorLayout LayoutFor(DisplayMonitor monitor, bool create = false)
        {
            if (data.MiniMonitorLayouts.TryGetValue(monitor.Id, out var layout)) return layout;
            layout = CalendarPlacementPolicy.Defaults(monitor.Name);
            if (create) data.MiniMonitorLayouts[monitor.Id] = layout;
            return layout;
        }

        internal bool RestoreMonitorPlacement(string reason, bool force = false)
        {
            if (closed || companion || appearancePreviewOnly || !monitorPlacementReady || applyingMonitorPlacement) return false;
            if (movingWindow || resizingBoard || placingPets || draggingPet >= 0)
            { monitorPlacementPending = true; return false; }
            var monitors = ReadPlacementMonitors();
            if (monitors.Count == 0) { monitorPlacementPending = true; return false; }
            var target = CalendarPlacementPolicy.Select(monitors, data.MiniPreferredMonitorId, activeMonitorId);
            string topology = MonitorTopology(monitors);
            if (!force && !monitorPlacementPending && topology == monitorTopology && CalendarPlacementPolicy.Same(target.Id, activeMonitorId)) return false;
            var previousMonitor = placementMonitors.FirstOrDefault(m => CalendarPlacementPolicy.Same(m.Id, activeMonitorId));
            if (!force && !monitorPlacementPending && CalendarPlacementPolicy.Same(target.Id, activeMonitorId) && previousMonitor?.Signature == target.Signature)
            {
                // A different display changed. Keep the current mode's live placement exactly where the user left it.
                placementMonitors = monitors; monitorTopology = topology;
                return false;
            }
            var layout = LayoutFor(target);
            applyingMonitorPlacement = true;
            monitorApplyTarget = target;
            try
            {
                FinishCalendarModeTransition();
                FinishWeekFlip();
                placementMonitors = monitors;
                activeMonitorId = target.Id;
                calendarVerticalAnchor = null;
                // Only effective/backward-compatible geometry changes here. The preferred layout and monitor stay intact.
                data.MiniBoard = CalendarPlacementPolicy.Resolve(layout.Compact, target.WorkArea, false);
                data.MiniMonthBoard = CalendarPlacementPolicy.Resolve(layout.Month, target.WorkArea, true);
                var board = IsMonthViewOpen ? data.MiniMonthBoard : data.MiniBoard;
                ResizeCalendarBoard(board);
                // Moving between DPI scales can cause Windows to apply its own suggested bounds during the first pass.
                if (new WindowInteropHelper(this).Handle != IntPtr.Zero) ResizeCalendarBoard(board);
                Refresh();
                monitorTopology = topology;
                monitorPlacementPending = false;
            }
            finally { monitorApplyTarget = null; applyingMonitorPlacement = false; }
            SavePosition();
            PetLog.Write("monitor-layout", reason + (CalendarPlacementPolicy.Same(target.Id, data.MiniPreferredMonitorId) ? ": preferred" : ": temporary fallback"));
            return true;
        }

        private MonitorStateData PreferredMonitorModeBoard(bool month)
        {
            if (!monitorPlacementReady) return null;
            var target = placementMonitors.FirstOrDefault(m => CalendarPlacementPolicy.Same(m.Id, activeMonitorId));
            if (target == null) return null;
            var layout = LayoutFor(target);
            var preferred = month ? layout.Month : layout.Compact;
            var board = CalendarPlacementPolicy.Resolve(preferred, target.WorkArea, month);
            board.Width = preferred.Width; board.Height = preferred.Height; // mode fitting may constrain, never redefine, these sizes
            return board;
        }

        // Called only after an explicit user move/resize. A move preserves preferred size even if the screen fitted it smaller.
        internal bool CommitUserMonitorPlacement(bool resized, string targetId = null)
        {
            if (!monitorPlacementReady || applyingMonitorPlacement || closed || companion) return false;
            var monitors = ReadPlacementMonitors();
            if (monitors.Count == 0) return false;
            Rect pixels;
            if (monitorBoardPixelsOverride != null) pixels = monitorBoardPixelsOverride();
            else if (!TryDeviceRect(BoardRect, out pixels))
            {
                var current = CaptureCalendarBoard();
                var dpi = VisualTreeHelper.GetDpi(this);
                pixels = new Rect(current.Left * dpi.DpiScaleX, current.Top * dpi.DpiScaleY, current.Width * dpi.DpiScaleX, current.Height * dpi.DpiScaleY);
            }
            var target = CalendarPlacementPolicy.Nearest(monitors, pixels);
            if (targetId != null && !CalendarPlacementPolicy.Same(target.Id, targetId)) return false;
            var layout = LayoutFor(target, true);
            var preferred = IsMonthViewOpen ? layout.Month : layout.Compact;
            var desired = new MonitorStateData { Left = pixels.Left / target.ScaleX, Top = pixels.Top / target.ScaleY,
                Width = resized ? pixels.Width / target.ScaleX : preferred.Width, Height = resized ? pixels.Height / target.ScaleY : preferred.Height };
            var fitted = FitCalendarBoard(desired, target.WorkArea, IsMonthViewOpen);
            var stored = CalendarPlacementPolicy.Capture(fitted, target.WorkArea);
            if (!resized) { stored.Width = preferred.Width; stored.Height = preferred.Height; }
            if (target.WorkArea.Width - fitted.Width < .001) stored.Horizontal = preferred.Horizontal;
            if (target.WorkArea.Height - fitted.Height < .001) stored.Vertical = preferred.Vertical;
            if (IsMonthViewOpen) { layout.Month = stored; layout.Compact.Width = stored.Width; layout.Compact.Horizontal = stored.Horizontal; }
            else { layout.Compact = stored; layout.Month.Width = stored.Width; layout.Month.Horizontal = stored.Horizontal; }
            // Moving the calendar also moves the other mode's anchor, while its own preferred height remains independent.
            var other = IsMonthViewOpen ? layout.Compact : layout.Month;
            var otherBoard = AnchorCalendarBoard(fitted, new MonitorStateData { Height = other.Height }, target.WorkArea,
                !IsMonthViewOpen, IsMonthViewOpen ? calendarVerticalAnchor ?? .5 : .5);
            if (target.WorkArea.Height - otherBoard.Height > .001)
                other.Vertical = CalendarPlacementPolicy.Capture(otherBoard, target.WorkArea).Vertical;
            layout.Name = target.Name; layout.ChangedUtc = DateTime.UtcNow;
            data.MiniPreferredMonitorId = target.Id; // an intentional move while falling back cancels the return to the old monitor
            activeMonitorId = target.Id;
            monitorDragTarget = null;
            RestoreMonitorPlacement(resized ? "user-resize" : "user-move", true);
            return true;
        }

        private bool CenterPreferredMonitorPlacement()
        {
            if (!monitorPlacementReady || closed || companion) return false;
            var monitors = ReadPlacementMonitors();
            var target = monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault();
            if (target == null) return false;
            var layout = LayoutFor(target, true);
            var preferred = IsMonthViewOpen ? layout.Month : layout.Compact;
            preferred.Vertical = .5;
            layout.Compact.Horizontal = layout.Month.Horizontal = .5;
            (IsMonthViewOpen ? layout.Compact : layout.Month).Vertical = .5;
            layout.ChangedUtc = DateTime.UtcNow;
            data.MiniPreferredMonitorId = target.Id;
            activeMonitorId = target.Id;
            RestoreMonitorPlacement("position-reset", true);
            FlushSave();
            return true;
        }

        private void NoteMonitorDragTarget(System.Drawing.Point cursor)
        {
            if (!monitorPlacementReady || !windowDragging) return;
            monitorDragTarget = CalendarPlacementPolicy.Nearest(placementMonitors, new Rect(cursor.X, cursor.Y, 1, 1))?.Id;
        }

        private string MonitorMoveTargetForDpi()
        {
            if (!windowDragging || monitorDragTarget == null || CalendarPlacementPolicy.Same(monitorDragTarget, activeMonitorId)) return null;
            // The display topology is unchanged: this DPI event belongs to dragging onto the other monitor, not reconnect.
            return MonitorTopology(ReadPlacementMonitors()) == monitorTopology ? monitorDragTarget : null;
        }
    }
}
