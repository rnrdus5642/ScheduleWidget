using System;
using System.Windows;
using System.Windows.Controls;

namespace ScheduleWidget
{
    public partial class SettingsWindow : Window
    {
        public AppearanceSettings Result { get; private set; }

        private readonly AppearanceSettings originalSettings;
        private readonly Action<AppearanceSettings> applyPreview;
        private bool isLoading = true;

        public SettingsWindow(AppearanceSettings current, Action<AppearanceSettings> applyPreview)
        {
            InitializeComponent();

            this.applyPreview = applyPreview;

            // 원본 백업 (취소 시 복원용)
            originalSettings = new AppearanceSettings
            {
                Opacity = current.Opacity,
                ThemePreset = current.ThemePreset,
                TitleFontSize = current.TitleFontSize,
                DDayFontSize = current.DDayFontSize
            };
            originalSettings.CopyColorsFrom(current);

            // 작업용 복사본
            Result = new AppearanceSettings
            {
                Opacity = current.Opacity,
                ThemePreset = current.ThemePreset,
                TitleFontSize = current.TitleFontSize,
                DDayFontSize = current.DDayFontSize
            };
            Result.CopyColorsFrom(current);

            LoadSettingsToUI();
            isLoading = false;
        }

        private void LoadSettingsToUI()
        {
            OpacitySlider.Value = Result.Opacity * 100;

            for (int i = 0; i < PresetCombo.Items.Count; i++)
            {
                if ((PresetCombo.Items[i] as ComboBoxItem)?.Content.ToString() == Result.ThemePreset)
                {
                    PresetCombo.SelectedIndex = i;
                    break;
                }
            }

            TitleFontSizeSlider.Value = Result.TitleFontSize;
            DDayFontSizeSlider.Value = Result.DDayFontSize;

            UpdateFontSizeLabels();
        }

        private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (isLoading || PresetCombo.SelectedItem == null) return;

            string preset = (PresetCombo.SelectedItem as ComboBoxItem)?.Content.ToString();
            if (preset != null && AppearanceSettings.Presets.ContainsKey(preset))
            {
                Result.ThemePreset = preset;
                Result.CopyColorsFrom(AppearanceSettings.Presets[preset]);
                ApplyLivePreview();
            }
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (isLoading || OpacityValueText == null) return;
            OpacityValueText.Text = $"{(int)OpacitySlider.Value}%";
            Result.Opacity = OpacitySlider.Value / 100.0;
            ApplyLivePreview();
        }

        private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (isLoading) return;
            if (TitleFontSizeSlider != null) Result.TitleFontSize = TitleFontSizeSlider.Value;
            if (DDayFontSizeSlider != null) Result.DDayFontSize = DDayFontSizeSlider.Value;
            UpdateFontSizeLabels();
            ApplyLivePreview();
        }

        private void UpdateFontSizeLabels()
        {
            if (TitleFontSizeText != null) TitleFontSizeText.Text = $"{(int)TitleFontSizeSlider.Value}";
            if (DDayFontSizeText != null) DDayFontSizeText.Text = $"{(int)DDayFontSizeSlider.Value}";
        }

        private void ApplyLivePreview()
        {
            applyPreview?.Invoke(Result);
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            // 원래 설정으로 복원
            applyPreview?.Invoke(originalSettings);
            DialogResult = false;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            // X 버튼으로 닫은 경우에도 복원
            if (DialogResult != true)
                applyPreview?.Invoke(originalSettings);

            base.OnClosed(e);
        }
    }
}
