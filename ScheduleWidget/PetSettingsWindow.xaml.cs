using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScheduleWidget
{
    /// <summary>What the pet settings window needs from the mini window (MiniWindow implements it).</summary>
    public interface IPetSettingsHost
    {
        int PetCount { get; }
        int MaxPets { get; }
        string PetName(int index);
        string PetAnimation(int index);
        bool PetAnimates(int index);             // false for a still image (no sprite rows): its actions do nothing
        IReadOnlyList<(string Key, string Label)> PetAnimationOptions(int index);
        void SetPetAnimation(int index, string key);
        void ChangePet(int index);   // opens the character picker for that pet
        void AddPet();               // opens the picker for a new pet
        void RemovePet(int index);
        int PetScale(int index);                  // each pet's own size, 50~300 %
        void SetPetScale(int index, int percent);
        bool PetVisible(int index);              // 보이기 per pet (a hidden pet stays in the list)
        void SetPetVisible(int index, bool visible);
        bool PetFlipped(int index);              // 좌우 반전 per pet (the drawing is mirrored; its spot stays)
        void SetPetFlipped(int index, bool flipped);
        bool CharactersVisible { get; }
        void SetCharacterVisible(bool visible);
        bool BesideTodoWindow { get; }           // the TODO window's pets (👤): they stand around that window, not a calendar
        string CharacterSide { get; }
        int CharacterVertical { get; }
        int CharacterGap { get; }
        void SetCharacterPlacement(string side, int vertical, int gap);
        void StartPetPlacement();
        void StartPetPlacement(int index);
        void OpenPetSettings(int index);
        void ResetPetPosition(int index);
        void ResetPetDefaults(int index);
        void ResetCharacterPlacement();
        void ResetPetDefaults();                 // 초기화: every pet shown, 100 %, default spots (left, bottom, 8 px), 대기
        event Action PetsChanged;
    }

    // 캐릭터 설정: every pet setting of the mini window in one small window. Changes apply (and save) at once.
    public partial class PetSettingsWindow : Window
    {
        private readonly IPetSettingsHost host;
        private readonly bool globalSettings;
        private int selected;
        private bool shut; // closed (e.g. 드래그로 위치 설정 chosen in the picker): nothing left to refresh
        private bool loading = true; // until the window is filled: XAML sets slider minimums while loading, which must not reach the mini window

        public PetSettingsWindow(IPetSettingsHost host, int selectedPet, bool embedded = false)
        {
            this.host = host;
            globalSettings = embedded;
            selected = selectedPet;
            InitializeComponent();
            if (!embedded) ChromelessWindow.Apply(this, addCloseButton: false, rounded: true);
            else { PetEditorHeader.Visibility = Visibility.Collapsed; AuxTheme.ApplyTo(this); ShowInTaskbar = false; }
            host.PetsChanged += Reload;
            loading = false;
            Closed += (s, e) => { shut = true; host.PetsChanged -= Reload; };
            Reload();
        }

        public int SelectedPet => selected;
        public event Action CloseRequested;

        public FrameworkElement TakeSettingsContent()
        {
            var view = (FrameworkElement)Content;
            Content = null;
            view.Resources.MergedDictionaries.Add(Resources);
            return view;
        }

        private void RequestClose() { if (CloseRequested != null) CloseRequested(); else Close(); }

        // 타이핑 반응 counts key presses only (TypingInput): said where the mode is offered.
        internal const string TypingPrivacyNote = "어떤 키를 눌렀는지는 읽지 않고, 눌렀다는 사실만 셉니다.";

        /// <summary>Shows the settings for this pet (the window stays open if it already is).</summary>
        public void Select(int pet)
        {
            selected = pet;
            Reload();
        }

        private void Reload()
        {
            resetArmed = false;
            if (ResetButton != null) ResetButton.Content = globalSettings ? "전체 배치 초기화" : "이 캐릭터 초기화";
            loading = true;
            try
            {
                int count = host.PetCount;
                if (selected >= count || selected < 0) selected = 0;
                // The TODO window's pets stand around that window: the same settings, named for it.
                string board = host.BesideTodoWindow ? "일정" : "달력";
                HeaderHint.Text = "이 캐릭터에만 즉시 적용하고 저장합니다.";
                PetRosterSection.Visibility = GlobalPlacementPanel.Visibility = VisibleToggle.Visibility = globalSettings ? Visibility.Visible : Visibility.Collapsed;
                ActionSection.Visibility = PersonalAppearancePanel.Visibility = PositionResetButton.Visibility = globalSettings ? Visibility.Collapsed : Visibility.Visible;
                VisibleToggle.Content = host.BesideTodoWindow ? "일정 옆에 캐릭터 표시" : "달력에 캐릭터 표시";
                PlacementTitle.Text = "위치 (" + board + " 기준)";
                SideLeftItem.Content = board + " 왼쪽";
                SideRightItem.Content = board + " 오른쪽";
                GapTitle.Text = board + "과 간격";
                AutomationProperties.SetName(GapSlider, "캐릭터와 " + board + " 사이 간격");
                PlaceButton.Content = globalSettings ? "전체 캐릭터 배치" : "이 캐릭터 이동";
                PlaceHint.Text = globalSettings ? "설정창을 닫고 캐릭터들을 각각 끌어 배치합니다." : "창을 닫고 이 캐릭터만 끌어 이동합니다. 다른 캐릭터의 자리는 유지합니다.";
                ResetHint.Text = globalSettings ? "전체 위치를 " + board + " 왼쪽·아래·간격 8px로 되돌립니다. 각 캐릭터의 크기와 동작은 유지합니다."
                    : "이 캐릭터만 크기 100%, 대기 동작, 반전 끔, 보이기와 기본 위치로 되돌립니다.";
                // The TODO window's pets have no calendar to right-click: the last pet showing there cannot be hidden, or
                // nothing would be left to open 캐릭터 설정 from.
                int shownPets = Enumerable.Range(0, count).Count(host.PetVisible);
                PetList.ItemsSource = Enumerable.Range(0, count).Select(i => new
                {
                    Index = i, Label = (i + 1) + ". " + host.PetName(i), Selected = i == selected, CanRemove = count > 1, Shown = host.PetVisible(i),
                    CanHide = !(host.BesideTodoWindow && shownPets <= 1 && host.PetVisible(i))
                }).ToList();
                AddPetButton.IsEnabled = count < host.MaxPets;
                // The header names the pet being edited; the sections just follow it.
                string petName = host.PetName(selected);
                HeaderTitle.Text = Title = globalSettings ? "캐릭터 전체 설정" : "캐릭터 설정 · " + petName;
                ActionTitle.Text = "동작";
                ScaleTitle.Text = "크기";
                FlipTitle.Text = "좌우 반전";
                FlipSwitch.IsChecked = host.PetFlipped(selected);
                IndividualVisibleToggle.IsChecked = host.PetVisible(selected);
                IndividualVisibleToggle.IsEnabled = !(host.BesideTodoWindow && shownPets <= 1 && host.PetVisible(selected));
                string current = host.PetAnimation(selected);
                var options = host.PetAnimationOptions(selected);
                ActionButtons.ItemsSource = options
                    .Select(o => new { o.Key, o.Label, Selected = o.Key == current, Tip = o.Key == MiniWindow.TypingMode ? TypingPrivacyNote : null }).ToList();
                TypingHint.Visibility = options.Any(o => o.Key == MiniWindow.TypingMode) ? Visibility.Visible : Visibility.Collapsed;
                VisibleToggle.IsChecked = host.CharactersVisible;
                ScaleSlider.Value = host.PetScale(selected);
                SideCombo.SelectedIndex = string.Equals(host.CharacterSide, "Right", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                VerticalSlider.Value = host.CharacterVertical;
                GapSlider.Value = host.CharacterGap;
                UpdateLabels();
                bool shown = host.CharactersVisible;
                ActionButtons.IsEnabled = host.PetAnimates(selected);
                // Nothing to drag when every pet is hidden.
                PlaceButton.IsEnabled = shown && (globalSettings ? Enumerable.Range(0, count).Any(host.PetVisible) : host.PetVisible(selected));
            }
            finally { loading = false; }
        }

        private void UpdateLabels()
        {
            if (ScaleText == null || ScaleSlider == null || VerticalSlider == null || GapSlider == null || VerticalText == null || GapText == null) return; // still loading
            ScaleText.Text = (int)Math.Round(ScaleSlider.Value) + "%";
            int vertical = (int)Math.Round(VerticalSlider.Value), gap = (int)Math.Round(GapSlider.Value);
            VerticalText.Text = vertical >= 100 ? "아래" : vertical <= 0 ? "위" : vertical == 50 ? "가운데" : vertical + "%";
            GapText.Text = gap < 0 ? "겹침 " + (-gap) + "px" : gap + "px";
        }

        // The pet a list row stands for, or -1 when the list is out of date (a pet was removed meanwhile): then the list
        // is rebuilt instead of acting on a pet that is no longer there.
        private int RowPet(object sender)
        {
            if (!(((FrameworkElement)sender).Tag is int index)) return -1;
            if (index >= 0 && index < host.PetCount) return index;
            Reload();
            return -1;
        }

        private void PetShown_Click(object sender, RoutedEventArgs e)
        {
            var box = (CheckBox)sender;
            int index = RowPet(box);
            if (index >= 0) host.SetPetVisible(index, box.IsChecked == true);
        }

        private void Pet_Checked(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            int index = RowPet(sender);
            if (index < 0 || index == selected) return;
            selected = index;
            Reload();
        }

        private void ConfigurePet_Click(object sender, RoutedEventArgs e)
        {
            int index = RowPet(sender);
            if (index >= 0) host.OpenPetSettings(index);
        }

        private void IndividualVisible_Click(object sender, RoutedEventArgs e)
        {
            if (!loading && !globalSettings) host.SetPetVisible(selected, IndividualVisibleToggle.IsChecked == true);
        }

        private void PositionReset_Click(object sender, RoutedEventArgs e) => host.ResetPetPosition(selected);

        private void Action_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is string key) host.SetPetAnimation(selected, key);
        }

        private void ChangePet_Click(object sender, RoutedEventArgs e)
        {
            int index = RowPet(sender);
            if (index < 0) return;
            selected = index;
            host.ChangePet(index);
            if (!shut) Reload(); // cancelled too: the header and list name the pet the buttons now act on
        }

        private void RemovePet_Click(object sender, RoutedEventArgs e)
        {
            int index = RowPet(sender);
            if (index >= 0) { selected = 0; host.RemovePet(index); }
        }

        private void AddPet_Click(object sender, RoutedEventArgs e)
        {
            int before = host.PetCount;
            host.AddPet();
            if (host.PetCount > before) { selected = host.PetCount - 1; Reload(); }
        }

        private void Visible_Click(object sender, RoutedEventArgs e) => host.SetCharacterVisible(VisibleToggle.IsChecked == true);

        private void Scale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ScaleText == null) return; // still loading the XAML
            UpdateLabels();
            if (!loading) host.SetPetScale(selected, (int)Math.Round(ScaleSlider.Value)); // the selected pet only
        }

        // 좌우 반전: the selected pet only, applied and saved at once.
        private void Flip_Click(object sender, RoutedEventArgs e)
        {
            if (!loading) host.SetPetFlipped(selected, FlipSwitch.IsChecked == true);
        }

        private void Placement_Changed(object sender, RoutedEventArgs e)
        {
            if (GapText == null || VerticalText == null || SideCombo == null) return; // still loading the XAML
            UpdateLabels();
            if (loading) return;
            host.SetCharacterPlacement(SideCombo.SelectedIndex == 1 ? "Right" : "Left",
                (int)Math.Round(VerticalSlider.Value), (int)Math.Round(GapSlider.Value));
        }

        // 초기화 → 정말 초기화 → reset. Anything else pressed in between cancels the confirmation (Reload).
        private bool resetArmed;

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (!resetArmed)
            {
                resetArmed = true;
                ResetButton.Content = "정말 초기화";
                return;
            }
            if (globalSettings) host.ResetCharacterPlacement();
            else host.ResetPetDefaults(selected);
            Reload();
        }

        private void Place_Click(object sender, RoutedEventArgs e)
        {
            RequestClose();
            if (globalSettings) host.StartPetPlacement();
            else host.StartPetPlacement(selected);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => RequestClose();

        // No title bar: the header moves the window.
        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } }
            e.Handled = true; // the window-wide drag (ChromelessWindow) must not start a second one
        }

        private void PetSettings_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (SideCombo.IsDropDownOpen) SideCombo.IsDropDownOpen = false; // Esc closes the open list first, like any drop-down
            else RequestClose();
        }
    }
}
