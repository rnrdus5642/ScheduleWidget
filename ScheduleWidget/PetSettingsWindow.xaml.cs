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
        private readonly AuxiliaryWindowDismissal dismissal;
        private CharacterHelpWindow helpWindow;
        private int selected;
        private bool shut; // closed (e.g. 드래그로 위치 설정 chosen in the picker): nothing left to refresh
        private bool loading = true; // until the window is filled: XAML sets slider minimums while loading, which must not reach the mini window

        public PetSettingsWindow(IPetSettingsHost host, int selectedPet, bool embedded = false)
        {
            this.host = host;
            globalSettings = embedded;
            selected = selectedPet;
            InitializeComponent();
            if (!embedded)
            {
                ChromelessWindow.Apply(this, addCloseButton: false, rounded: true);
                dismissal = new AuxiliaryWindowDismissal(this, RequestClose);
            }
            else
            {
                PetEditorHeader.Visibility = Visibility.Collapsed;
                PetSettingsRoot.BorderThickness = new Thickness(0);
                SettingsBody.Margin = new Thickness(0, 0, 6, 16);
                AuxTheme.ApplyTo(this);
                ShowInTaskbar = false;
            }
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

        private void Help_Click(object sender, RoutedEventArgs e)
        {
            if (helpWindow != null) { helpWindow.Activate(); return; }
            // Embedded settings belong to the visible SettingsWindow, not this detached content's hidden owner.
            var owner = Window.GetWindow(sender as DependencyObject);
            if (owner == null || !owner.IsVisible) return;
            helpWindow = new CharacterHelpWindow { Owner = owner };
            helpWindow.Closed += (s, args) => helpWindow = null;
            helpWindow.Show();
        }

        private void SettingsSections_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!globalSettings || PlacementColumn == null) return;
            // Keep the roster and placement readable when settings is fitted to a smaller remote display.
            bool wide = e.NewSize.Width >= 680;
            PlacementColumnWidth.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(PlacementColumn, wide ? 1 : 0);
            Grid.SetRow(PlacementColumn, wide ? 0 : 1);
            PetRosterSection.Margin = new Thickness(0, 0, wide ? 16 : 0, 14);
        }

        private void ActionButtons_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Avoid splitting labels such as "오른쪽 걷기" when the personal window is narrow.
            var panel = (ItemsPanelTemplate)FindResource(e.NewSize.Width < 270 ? "PetActionsNarrowPanel" : "PetActionsWidePanel");
            if (!ReferenceEquals(ActionButtons.ItemsPanel, panel)) ActionButtons.ItemsPanel = panel;
        }

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
                PetRosterSection.Visibility = GlobalVisibilitySection.Visibility = VisibleToggle.Visibility = globalSettings ? Visibility.Visible : Visibility.Collapsed;
                ActionSection.Visibility = PersonalAppearancePanel.Visibility = PositionResetButton.Visibility = globalSettings ? Visibility.Collapsed : Visibility.Visible;
                GlobalVisibilityLabel.Text = host.BesideTodoWindow ? "일정 옆에 캐릭터 표시" : "달력에 캐릭터 표시";
                GlobalHiddenHint.Visibility = host.CharactersVisible ? Visibility.Collapsed : Visibility.Visible;
                PlacementSectionTitle.Text = globalSettings ? "전체 배치" : "위치";
                PlaceButton.Content = globalSettings ? "드래그로 전체 배치" : "이 캐릭터 이동";
                PlaceHint.Text = globalSettings ? "설정창이 닫히면 캐릭터를 각각 끌어 놓으세요." : "창이 닫히면 이 캐릭터를 끌어 놓으세요.";
                ResetHint.Text = globalSettings ? "직접 옮긴 위치를 기본 배치로 되돌립니다."
                    : "크기·동작·표시·반전·위치를 되돌립니다.";
                ResetButton.ToolTip = globalSettings ? "전체 위치를 초기화합니다. 각 캐릭터의 크기와 동작은 유지합니다."
                    : "이 캐릭터만 크기 100%, 대기 동작, 반전 끔, 보이기와 기본 위치로 되돌립니다.";
                AutomationProperties.SetName(ResetButton, globalSettings ? "전체 캐릭터 배치 초기화" : "이 캐릭터 설정 초기화");
                // The TODO window's pets have no calendar to right-click: the last pet showing there cannot be hidden, or
                // nothing would be left to open 캐릭터 설정 from.
                int shownPets = Enumerable.Range(0, count).Count(host.PetVisible);
                PetList.ItemsSource = Enumerable.Range(0, count).Select(i => new
                {
                    Index = i, Name = host.PetName(i), Label = (i + 1) + ". " + host.PetName(i), CanRemove = count > 1, Shown = host.PetVisible(i),
                    CanHide = !(host.BesideTodoWindow && shownPets <= 1 && host.PetVisible(i)),
                    VisibilityName = host.PetName(i) + " 표시",
                    VisibilityHint = host.BesideTodoWindow && shownPets <= 1 && host.PetVisible(i) ? "설정을 열 수 있도록 마지막 캐릭터는 표시합니다." : "이 캐릭터의 표시를 켜거나 끕니다.",
                    RemoveHint = count > 1 ? "현재 목록에서 뺍니다. 캐릭터 파일은 유지됩니다." : "캐릭터는 최소 1개를 유지합니다."
                }).ToList();
                PetCountText.Text = count + " / " + host.MaxPets;
                AddPetButton.IsEnabled = count < host.MaxPets;
                AddPetButton.ToolTip = "최대 " + host.MaxPets + "개까지 추가할 수 있습니다.";
                // The header names the pet being edited; the sections just follow it.
                string petName = host.PetName(selected);
                Title = globalSettings ? "캐릭터 전체 설정" : "캐릭터 설정 · " + petName;
                HeaderTitle.Text = petName;
                HeaderTitle.ToolTip = petName;
                ActionTitle.Text = "동작";
                ScaleTitle.Text = "크기";
                FlipTitle.Text = "좌우 반전";
                FlipSwitch.IsChecked = host.PetFlipped(selected);
                IndividualVisibleToggle.IsChecked = host.PetVisible(selected);
                IndividualVisibleToggle.IsEnabled = !(host.BesideTodoWindow && shownPets <= 1 && host.PetVisible(selected));
                IndividualVisibleToggle.ToolTip = IndividualVisibleToggle.IsEnabled ? "이 캐릭터의 표시를 켜거나 끕니다." : "설정을 열 수 있도록 마지막 캐릭터는 표시합니다.";
                string current = host.PetAnimation(selected);
                var options = host.PetAnimationOptions(selected);
                ActionButtons.ItemsSource = options
                    .Select(o => new { o.Key, o.Label, Selected = o.Key == current, Tip = o.Key == MiniWindow.TypingMode ? TypingPrivacyNote : null }).ToList();
                TypingHint.Text = "입력할 때 움직입니다. " + TypingPrivacyNote;
                TypingHint.Visibility = current == MiniWindow.TypingMode && options.Any(o => o.Key == MiniWindow.TypingMode) ? Visibility.Visible : Visibility.Collapsed;
                VisibleToggle.IsChecked = host.CharactersVisible;
                ScaleSlider.Value = host.PetScale(selected);
                UpdateScaleLabel();
                bool shown = host.CharactersVisible;
                ActionButtons.IsEnabled = host.PetAnimates(selected);
                StillImageHint.Visibility = ActionButtons.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
                // Nothing to drag when every pet is hidden.
                PlaceButton.IsEnabled = shown && (globalSettings ? Enumerable.Range(0, count).Any(host.PetVisible) : host.PetVisible(selected));
                PlaceButton.ToolTip = PlaceButton.IsEnabled ? null : !shown ? "전체 캐릭터 표시를 켜면 직접 이동할 수 있습니다."
                    : "이동할 캐릭터의 표시를 켜 주세요.";
            }
            finally { loading = false; }
        }

        private void UpdateScaleLabel()
        {
            if (ScaleText == null || ScaleSlider == null) return; // still loading
            ScaleText.Text = (int)Math.Round(ScaleSlider.Value) + "%";
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
            UpdateScaleLabel();
            if (!loading) host.SetPetScale(selected, (int)Math.Round(ScaleSlider.Value)); // the selected pet only
        }

        // 좌우 반전: the selected pet only, applied and saved at once.
        private void Flip_Click(object sender, RoutedEventArgs e)
        {
            if (!loading) host.SetPetFlipped(selected, FlipSwitch.IsChecked == true);
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
            RequestClose();
        }
    }
}
