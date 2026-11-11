using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ScheduleWidget
{
    public partial class MiniWindow
    {
        private const double PetEdgeOverlap = 24;
        private MiniPetLayout pendingCalendarPetLayout;
        private MonitorStateData petLayoutBoard;
        private bool petLayoutScreenFitting;

        private static MiniPetLayout CopyPetLayout(IEnumerable<MiniPetSpot> spots, IDictionary<string, MiniPetSpot> memory) =>
            new MiniPetLayout
            {
                Spots = (spots ?? Enumerable.Empty<MiniPetSpot>()).Select(s => s == null ? new MiniPetSpot() : CopySpot(s)).ToList(),
                ByCharacter = (memory ?? new Dictionary<string, MiniPetSpot>()).Where(p => p.Value != null)
                    .ToDictionary(p => p.Key, p => CopySpot(p.Value))
            };

        private void StoreCalendarPetLayout(bool month, MiniPetLayout layout)
        {
            if (month) data.MiniMonthPetLayout = layout;
            else data.MiniCompactPetLayout = layout;
        }

        // This prepares data only: the visible source and its snapshots retain their placement until the handoff.
        private void PrepareCalendarPetMode(MonitorStateData from, MonitorStateData to, bool month)
        {
            if (companion) return;
            if (loadedSlotKeys != null) RememberSpots();
            var before = PetBoardRects(from.Width, from.Height, fitToScreen: false);
            StoreCalendarPetLayout(!month, CopyPetLayout(Spots, SpotMemory));
            var remembered = month ? data.MiniMonthPetLayout : data.MiniCompactPetLayout;
            var target = CopyPetLayout(remembered?.Spots, remembered?.ByCharacter);
            var keys = SlotKeys();
            // Use nominal positions. Screen-edge correction is visual only and must not become the saved placement.
            for (int i = 0; i < keys.Count; i++)
            {
                while (target.Spots.Count <= i) target.Spots.Add(new MiniPetSpot());
                var source = i < Spots.Count ? Spots[i] : null;
                var saved = target.Spots[i];
                if (saved.Key != null && CharacterOfKey(saved.Key) != CharacterOfKey(keys[i])) saved = null;
                if (!IsPlaced(saved)) target.ByCharacter.TryGetValue(keys[i], out saved);
                var rect = i < before.Count ? before[i] : Rect.Empty;
                MiniPetSpot chosen;
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
                {
                    // Hidden pets retain their saved placement and never take room from the visible pets.
                    chosen = saved != null ? CopySpot(saved) : source != null ? CopySpot(source) : new MiniPetSpot();
                }
                else if (IsPlaced(saved))
                {
                    // Each mode owns its saved placement, including edge anchors. Infer a location from the source
                    // only on the first visit; a source edge must never replace a position saved in the target mode.
                    chosen = CopySpot(saved);
                }
                else if (source?.EdgeX == "L" || source?.EdgeX == "R" || rect.Right <= 0 || rect.Left >= from.Width ||
                    rect.Left + rect.Width / 2 <= 0 || rect.Left + rect.Width / 2 >= from.Width)
                {
                    // Side pets keep their screen coordinates, including the vertical coordinate, when the board grows.
                    bool left = source?.EdgeX == "L" || source?.EdgeX != "R" && rect.Left + rect.Width / 2 <= 0;
                    double x = from.Left + rect.Left - to.Left, y = from.Top + rect.Top - to.Top;
                    chosen = new MiniPetSpot { EdgeX = left ? "L" : "R", OffsetX = left ? x + rect.Width : x - to.Width };
                    StoreY(chosen, y, rect.Height, to.Height);
                }
                else if (source?.EdgeY == "T" || rect.Top < 0 && rect.Bottom <= PetEdgeOverlap)
                {
                    // Feet slightly overlapping the top frame still belong to the top, not a negative height percentage.
                    chosen = new MiniPetSpot { EdgeY = "T", OffsetY = rect.Bottom };
                    StoreX(chosen, rect.Left, rect.Width, to.Width);
                }
                else if (source?.EdgeY == "B" || rect.Bottom > from.Height && rect.Top >= from.Height - PetEdgeOverlap)
                {
                    chosen = new MiniPetSpot { EdgeY = "B", OffsetY = rect.Top - from.Height };
                    StoreX(chosen, rect.Left, rect.Width, to.Width);
                }
                else
                {
                    // The first month view keeps an inside pet where it was on screen. On a first collapse after a legacy
                    // monthly-only save, bring it into the smaller board by the minimum distance needed.
                    double x = Clamp(from.Left + rect.Left - to.Left, 0, Math.Max(0, to.Width - rect.Width));
                    double y = Clamp(from.Top + rect.Top - to.Top, 0, Math.Max(0, to.Height - rect.Height));
                    chosen = new MiniPetSpot { Left = x * 100 / to.Width, Top = y * 100 / to.Height };
                }
                chosen.Key = keys[i];
                target.Spots[i] = chosen;
                if (IsPlaced(chosen)) target.ByCharacter[keys[i]] = MemoryCopy(chosen);
            }
            pendingCalendarPetLayout = target;
        }

        private void ActivateCalendarPetMode(bool month)
        {
            if (companion || pendingCalendarPetLayout == null) return;
            data.MiniPetSpots = pendingCalendarPetLayout.Spots;
            data.MiniPetSpotsByCharacter = pendingCalendarPetLayout.ByCharacter;
            StoreCalendarPetLayout(month, CopyPetLayout(data.MiniPetSpots, data.MiniPetSpotsByCharacter));
            pendingCalendarPetLayout = null;
        }

        private void FitCalendarPetsToScreen(List<Rect> rects, double width, double height, MonitorStateData board)
        {
            if (companion || placingPets || board == null && !petLayoutScreenFitting) return;
            board = board ?? petLayoutBoard;
            if (!ValidCalendarBoard(board)) return;
            var area = CalendarWorkArea(board);
            if (area.IsEmpty || !area.IntersectsWith(new Rect(board.Left, board.Top, width, height))) return;
            for (int i = 0; i < rects.Count; i++)
            {
                var rect = rects[i];
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                rect.X = Clamp(board.Left + rect.X, area.Left, area.Right - rect.Width) - board.Left;
                rect.Y = Clamp(board.Top + rect.Y, area.Top, area.Bottom - rect.Height) - board.Top;
                rects[i] = rect;
            }
        }
    }
}
