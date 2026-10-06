using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScheduleWidget
{
    public partial class MainWindow
    {
        private const string AppWebsiteUrl = "https://schedule.jwstudio.page/";
        private const string AppPrivacyPolicyUrl = AppWebsiteUrl + "privacy.html";

        private readonly List<SettingsContentLease> settingsContentLeases = new List<SettingsContentLease>();
        private ContentControl settingsMusicContent, settingsReminderContent, settingsCharacterContent;
        private ContentControl settingsConnectionsContent;
        private FrameworkElement settingsGoogleContent;
        private PetSettingsWindow settingsPetEditor;
        private IPetSettingsHost settingsPetHost;
        private ComboBox settingsDayCount;
        private CheckBox settingsPlayerVisible;

        private SettingsWindow UnifiedSettings => settingsHost as SettingsWindow;

        private void OpenUnifiedSettings(SettingsPage page)
        {
            if (appData == null || closingApp) return;
            if (UnifiedSettings != null)
            {
                UnifiedSettings.SelectPage(page);
                PrepareSettingsPage(page);
                BringToFront(UnifiedSettings);
                return;
            }
            if (miniWindow == null) ShowMiniWindow();
            BeginSettingsEdit();
            previewOriginalScale = appData.MiniCharacterScale;
            previewOriginalExtraScales = (appData.MiniExtraCharacters ?? new List<MiniCharacterSlot>()).Select(s => s.Scale).ToList();
            previewOriginalVisible = appData.MiniCharacterVisible;
            previewOriginalSide = appData.MiniCharacterSide;
            previewOriginalVertical = appData.MiniCharacterVertical;
            previewOriginalGap = appData.MiniCharacterGap;
            previewOriginalSpots = (appData.MiniPetSpots ?? new List<MiniPetSpot>()).Select(MiniWindow.CopySpot).ToList();
            CaptureMiniPreviewState();
            settingsPreviewMiniWindow = miniWindow;
            if (settingsPreviewMiniWindow != null)
            {
                settingsPreviewMiniWindow.PetsChanged += MiniPetsChangedWhileSettingsOpen;
                settingsPreviewMiniWindow.MiniSettingsStateChanged += MiniPetsChangedWhileSettingsOpen;
            }

            var window = new SettingsWindow { Owner = (Window)miniWindow ?? this };
            settingsHost = window;
            BuildUnifiedSettingsPages(window);
            window.PageChanged += PrepareSettingsPage;
            window.SaveRequested += () => InlineSettingsApplyButton_Click(window, new RoutedEventArgs());
            window.CloseRequested += () => CloseInlineSettings(false);
            window.ResetRequested += ResetUnifiedSettingsPage;
            window.Closing += (s, e) =>
            {
                if (!ReferenceEquals(settingsHost, window)) return;
                settingsHostClosing = true;
                try { CloseInlineSettings(false); }
                finally { settingsHostClosing = false; }
            };
            window.StateChanged += (s, e) => UpdateHostedMusic();
            window.KeyDown += (s, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Escape || InlineHotKeyBox.IsKeyboardFocused || settingsHost == null) return;
                e.Handled = true;
                CloseInlineSettings(false);
            };
            window.SelectPage(page);
            PrepareSettingsPage(page);
            window.Show();
            window.Activate();
        }

        private FrameworkElement TakeSettingsControl(FrameworkElement element)
        {
            settingsContentLeases.Add(new SettingsContentLease(element));
            if (element is Border) element.Margin = new Thickness(0, 0, 0, 12);
            return element;
        }

        private static StackPanel SettingsStack(params UIElement[] elements)
        {
            var panel = new StackPanel();
            foreach (var element in elements) if (element != null) panel.Children.Add(element);
            return panel;
        }

        private static ScrollViewer SettingsScroll(UIElement content) => new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 4)
        };

        private Border SettingsCard(string title, params UIElement[] items)
        {
            var heading = new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 14), Style = (Style)UnifiedSettings.FindResource("AuxSectionTitle") };
            var body = SettingsStack(heading);
            foreach (var item in items) body.Children.Add(item);
            return new Border { Child = body, Margin = new Thickness(0, 0, 0, 12), Style = (Style)UnifiedSettings.FindResource("AuxRoundSection") };
        }

        private void BuildUnifiedSettingsPages(SettingsWindow window)
        {
            window.SetPage(SettingsPage.General, SettingsScroll(SettingsStack(
                TakeSettingsControl(SettingsStartupCard),
                SettingsCard("창 표시", TakeSettingsControl(SettingsTopmostRow)),
                SettingsCard("단축키", TakeSettingsControl(SettingsHotkeyToggleRow), TakeSettingsControl(SettingsHotkeyInputRow), TakeSettingsControl(InlineHotKeyStatus)))));

            settingsDayCount = new ComboBox { Style = (Style)window.FindResource("AuxComboBox"), Margin = new Thickness(0, 0, 0, 16) };
            for (int day = 1; day <= 7; day++) settingsDayCount.Items.Add(new ComboBoxItem { Content = day + "일씩 표시", Tag = day });
            settingsDayCount.SelectedIndex = Math.Max(0, Math.Min(6, appData.MiniDayCount - 1));
            System.Windows.Automation.AutomationProperties.SetAutomationId(settingsDayCount, "SettingsDayCount");
            var dayLabel = new TextBlock { Text = "달력에 표시할 기간", Margin = new Thickness(0, 0, 0, 8) };
            window.SetPage(SettingsPage.Appearance, SettingsScroll(SettingsStack(
                TakeSettingsControl(SettingsThemeCard),
                SettingsCard("달력", dayLabel, settingsDayCount, TakeSettingsControl(SettingsCalendarDdayRow), TakeSettingsControl(SettingsFlipLabel), TakeSettingsControl(InlineFlipEffectCombo)),
                TakeSettingsControl(SettingsOpacityCard), TakeSettingsControl(SettingsFontCard),
                SettingsCard("전체 일정 색상", TakeSettingsControl(SettingsColorButtons), TakeSettingsControl(SettingsColorHint)))));

            settingsCharacterContent = new ContentControl();
            window.SetPage(SettingsPage.Characters, settingsCharacterContent);

            settingsMusicContent = new ContentControl();
            settingsPlayerVisible = new CheckBox { Content = "달력 아래 음악 막대 표시", IsChecked = appData.MiniPlayerVisible,
                Style = (Style)window.FindResource("AuxCheckBox"), Margin = new Thickness(0, 0, 0, 16) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(settingsPlayerVisible, "SettingsPlayerVisible");
            settingsPlayerVisible.Click += (s, e) =>
            {
                bool visible = settingsPlayerVisible.IsChecked == true;
                if (miniWindow != null) miniWindow.SetPlayerVisible(visible);
                else { appData.MiniPlayerVisible = visible; SaveDataSafely(); }
            };
            var music = new Grid();
            music.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            music.RowDefinitions.Add(new RowDefinition());
            music.Children.Add(settingsPlayerVisible);
            Grid.SetRow(settingsMusicContent, 1);
            music.Children.Add(settingsMusicContent);
            window.SetPage(SettingsPage.Music, music);

            settingsReminderContent = new ContentControl();
            window.SetPage(SettingsPage.Notifications, SettingsScroll(settingsReminderContent));
            settingsGoogleContent = TakeSettingsControl(SettingsGoogleCard);
            settingsConnectionsContent = new ContentControl();
            window.SetPage(SettingsPage.Connections, settingsConnectionsContent);
            var websiteLinks = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            websiteLinks.Children.Add(CreateWebsiteButton("홈페이지 ↗", AppWebsiteUrl));
            websiteLinks.Children.Add(CreateWebsiteButton("개인정보처리방침 ↗", AppPrivacyPolicyUrl));
            window.SetPage(SettingsPage.About, SettingsScroll(SettingsStack(
                SettingsCard("ScheduleWidget", new TextBlock { Text = "바탕화면에서 일정을 확인하고 관리하는 달력 위젯입니다.", TextWrapping = TextWrapping.Wrap }, websiteLinks),
                TakeSettingsControl(UpdateCard))));
        }

        private Button CreateWebsiteButton(string caption, string url)
        {
            var button = new Button
            {
                Content = caption, MinHeight = 32, Margin = new Thickness(0, 0, 8, 8),
                Style = (Style)UnifiedSettings.FindResource("AuxUtilityButton"),
                ToolTip = "기본 브라우저에서 열기: " + url
            };
            button.Click += (sender, args) => OpenAppWebsite(url, button);
            return button;
        }

        private void OpenPrivacyPolicy_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            OpenAppWebsite(AppPrivacyPolicyUrl, sender as DependencyObject);
        }

        private void OpenAppWebsite(string url, DependencyObject source)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            {
                var owner = (source == null ? null : Window.GetWindow(source)) ?? (Window)UnifiedSettings ?? this;
                MessageBox.Show(owner, "브라우저를 열지 못했습니다. 아래 주소로 직접 접속해 주세요.\n\n" + url,
                    "홈페이지 열기", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void PrepareSettingsPage(SettingsPage page)
        {
            if (UnifiedSettings == null) return;
            if (page == SettingsPage.Notifications || page == SettingsPage.Connections) EnsureContactSettings();
            if (page == SettingsPage.Characters && settingsPetEditor == null && miniWindow != null)
            {
                if (settingsPetHost == null) settingsPetHost = miniWindow;
                settingsPetEditor = new PetSettingsWindow(settingsPetHost, 0, embedded: true);
                settingsPetEditor.CloseRequested += () => CloseInlineSettings(false);
                settingsCharacterContent.Content = settingsPetEditor.TakeSettingsContent();
            }
            UpdateHostedMusic();
        }

        private void EnsureContactSettings()
        {
            if (contactWindow != null) return;
            contactWindow = new ContactWindow(appData, communicationService, () => SaveDataSafely(), embedded: true);
            contactWindow.CreateEmbeddedViews(settingsGoogleContent);
            contactWindow.StatusChanged += text => UnifiedSettings?.ShowStatus(text);
            settingsReminderContent.Content = SettingsCard("마감 알림", contactWindow.RemindersContent);
            settingsConnectionsContent.Content = contactWindow.ConnectionsContent;
            if (!string.IsNullOrWhiteSpace(contactWindow.StatusMessage)) UnifiedSettings?.ShowStatus(contactWindow.StatusMessage);
        }

        private void UpdateHostedMusic()
        {
            bool wantsMusic = UnifiedSettings?.SelectedPage == SettingsPage.Music && settingsHost.WindowState != WindowState.Minimized;
            if (!wantsMusic)
            {
                if (musicWindow?.IsSettingsHosted == true)
                {
                    settingsMusicContent.Content = null;
                    musicWindow.ReturnSettingsContent();
                }
                return;
            }
            EnsureMusicWindow();
            if (!musicWindow.IsSettingsHosted) settingsMusicContent.Content = musicWindow.TakeSettingsContent(settingsHost);
        }

        private void ReleaseUnifiedSettingsContent()
        {
            if (musicWindow?.IsSettingsHosted == true)
            {
                settingsMusicContent.Content = null;
                musicWindow.ReturnSettingsContent(keepPlaying: !closingApp && !Dispatcher.HasShutdownStarted);
            }
            settingsPetEditor?.Close();
            settingsPetEditor = null;
            settingsPetHost = null;
            contactWindow?.Close();
            contactWindow = null;
            for (int i = settingsContentLeases.Count - 1; i >= 0; i--) settingsContentLeases[i].Dispose();
            settingsContentLeases.Clear();
            settingsMusicContent = settingsReminderContent = settingsCharacterContent = null;
            settingsConnectionsContent = null;
            settingsGoogleContent = null;
            settingsDayCount = null;
            settingsPlayerVisible = null;
        }

        private void ResetUnifiedSettingsPage(SettingsPage page)
        {
            if (page == SettingsPage.General)
            {
                InlineAlwaysOnTopToggle.IsChecked = false;
                LoadInlineHotKey(true, HotKeyGesture.Default);
            }
            else if (page == SettingsPage.Appearance)
            {
                var defaults = new AppearanceSettings();
                defaults.CopyColorsFrom(AppearanceSettings.Presets["Light"]);
                _inlineSettingsDraft = CloneAppearance(defaults);
                _inlineSettingsLoading = true;
                InlinePresetCombo.SelectedIndex = FindPresetIndex("Light");
                InlineOpacitySlider.Value = 100;
                InlineTitleFontSizeSlider.Value = defaults.TitleFontSize;
                InlineDDayFontSizeSlider.Value = defaults.DDayFontSize;
                InlineBlockDDaySwitch.IsChecked = true;
                InlineFlipEffectCombo.SelectedIndex = FlipEffectIndex(1);
                settingsDayCount.SelectedIndex = 6;
                _inlineSettingsLoading = false;
                UpdateInlineSettingsLabels();
                ApplyAppearance(_inlineSettingsDraft);
            }
            UnifiedSettings?.ShowStatus("이 페이지를 기본값으로 되돌렸습니다. 설정 저장을 누르면 적용됩니다.");
        }
    }
}
