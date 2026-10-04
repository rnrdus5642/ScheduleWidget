using Newtonsoft.Json;

namespace ScheduleWidget
{
    public partial class MiniWindow
    {
        private bool CanDragPet(int index) => placingPets && (placingPet < 0 || placingPet == index);

        // Preserve the other pets' actual positions before a selected pet is reset or resized.
        private void PinOtherPetPositions(int selected)
        {
            var rects = PetBoardRects(BoardWidth, BoardHeight);
            for (int i = 0; i < rects.Count; i++)
            {
                if (i == selected || rects[i].Width <= 0) continue;
                var spot = SpotOf(i);
                if (!HasX(spot)) StoreX(spot, rects[i].X, rects[i].Width, BoardWidth);
                if (!HasY(spot)) StoreY(spot, rects[i].Y, rects[i].Height, BoardHeight);
            }
        }

        public void ResetPetPosition(int index)
        {
            if (!IsPet(index) || closed) return;
            PinOtherPetPositions(index);
            CopyInto(SpotOf(index), null);
            RememberSpots(forgetUnplaced: true);
            ApplyPetLayout();
            SaveSoon();
            MiniSettingsStateChanged?.Invoke();
            PetsChanged?.Invoke();
        }

        public void ResetPetDefaults(int index)
        {
            if (!IsPet(index) || closed) return;
            PinOtherPetPositions(index);
            SetSlotScale(index, 100);
            SetPetHidden(index, false);
            SetPetFlipped(index, false);
            SetSlotAnimation(index, "idle");
            CopyInto(SpotOf(index), null);
            RememberSpots(forgetUnplaced: true);
            UpdateCharacterSize();
            PostToPage(JsonConvert.SerializeObject(new { action = "animation", index, state = "idle" }));
            SendPetFlips();
            UpdateActivePetState();
            SaveSoon();
            MiniSettingsStateChanged?.Invoke();
        }

        public void ResetCharacterPlacement()
        {
            if (closed) return;
            Spots.Clear();
            PlacementSide = "Left";
            PlacementVertical = 100;
            PlacementGap = 8;
            RememberSpots(forgetUnplaced: true);
            ApplyPetLayout();
            SaveSoon();
            MiniSettingsStateChanged?.Invoke();
            PetsChanged?.Invoke();
        }
    }
}
