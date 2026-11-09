using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScheduleWidget
{
    public partial class MainWindow
    {
        private readonly Dictionary<string, FrameworkElement> appearanceSections = new Dictionary<string, FrameworkElement>();
        private readonly Dictionary<string, RadioButton> appearanceTabs = new Dictionary<string, RadioButton>();
        private Image appearancePreviewImage;
        private MiniWindow appearancePreviewWindow;
        private DispatcherTimer appearancePreviewTimer;
        private string appearanceSection = "Common";
        private double appearanceAvailableHeight;
        private bool appearancePreviewErrorLogged;

        private FrameworkElement BuildAppearanceSettingsPage(SettingsWindow window)
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            var tabs = new UniformGrid { Columns = 4 };
            var tabBar = new Border { Child = tabs, Padding = new Thickness(4), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 8, 12) };
            tabBar.SetResourceReference(Border.BackgroundProperty, "AuxSurfaceBrush");
            root.Children.Add(tabBar);
            var previewBody = new Grid();
            previewBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            previewBody.RowDefinitions.Add(new RowDefinition());
            var previewHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var enlarge = new Button { Content = "크게 보기", Style = (Style)window.FindResource("AuxCompactButton"),
                Padding = new Thickness(10, 3, 10, 3), MinHeight = 26, HorizontalAlignment = HorizontalAlignment.Right };
            System.Windows.Automation.AutomationProperties.SetName(enlarge, "미리보기 크게 보기");
            enlarge.Click += (s, e) => { RenderAppearancePreviewNow(); CreateAppearancePreviewDialog()?.ShowDialog(); };
            DockPanel.SetDock(enlarge, Dock.Right); previewHeader.Children.Add(enlarge);
            var previewLabel = new TextBlock { Text = "미리보기", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            previewLabel.SetResourceReference(TextBlock.ForegroundProperty, "AuxMutedBrush");
            previewHeader.Children.Add(previewLabel); previewBody.Children.Add(previewHeader);
            appearancePreviewImage = new Image { Stretch = Stretch.Uniform, MaxHeight = 260, VerticalAlignment = VerticalAlignment.Top, SnapsToDevicePixels = true };
            System.Windows.Automation.AutomationProperties.SetName(appearancePreviewImage, "설정을 반영한 예시 화면");
            Grid.SetRow(appearancePreviewImage, 1); previewBody.Children.Add(appearancePreviewImage);
            var preview = new Border { Child = previewBody, Padding = new Thickness(10),
                CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 12) };
            preview.SetResourceReference(Border.BackgroundProperty, "AuxSurfaceBrush");
            preview.SetResourceReference(Border.BorderBrushProperty, "AuxHairlineBrush");
            Grid.SetRow(preview, 1); root.Children.Add(preview);
            var pages = new Grid(); Grid.SetRow(pages, 2); root.Children.Add(pages);
            root.SizeChanged += (s, e) =>
            {
                appearanceAvailableHeight = e.NewSize.Height;
                UpdateAppearancePreviewSize();
            };

            void Section(string key, string label, params UIElement[] cards)
            {
                var group = new Grid();
                group.ColumnDefinitions.Add(new ColumnDefinition());
                group.ColumnDefinitions.Add(new ColumnDefinition());
                for (int i = 0; i < cards.Length; i++)
                {
                    group.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    Grid.SetRow(cards[i], i); Grid.SetColumnSpan(cards[i], 2);
                    ((FrameworkElement)cards[i]).VerticalAlignment = VerticalAlignment.Top;
                    group.Children.Add(cards[i]);
                }
                var page = SettingsScroll(group);
                page.SizeChanged += (s, e) =>
                {
                    bool beside = e.NewSize.Width >= 680 && cards.Length == 2;
                    for (int i = 0; i < cards.Length; i++)
                    {
                        Grid.SetRow(cards[i], beside ? 0 : i); Grid.SetColumn(cards[i], beside ? i : 0);
                        Grid.SetColumnSpan(cards[i], beside ? 1 : 2);
                        ((FrameworkElement)cards[i]).Margin = new Thickness(0, 0, beside && i == 0 ? 12 : 0, 12);
                    }
                };
                page.Visibility = Visibility.Collapsed;
                appearanceSections[key] = page;
                pages.Children.Add(page);
                var tab = new RadioButton { Content = label, GroupName = "AppearanceSection", Style = (Style)window.FindResource("AppearanceTab") };
                System.Windows.Automation.AutomationProperties.SetAutomationId(tab, "Appearance" + key + "Tab");
                tab.Checked += (s, e) => SelectAppearanceSection(key);
                appearanceTabs[key] = tab; tabs.Children.Add(tab);
            }

            Grid Row(FrameworkElement label, FrameworkElement control)
            {
                label.Margin = control.Margin = new Thickness(0);
                label.VerticalAlignment = control.VerticalAlignment = VerticalAlignment.Center;
                var row = new Grid { MinHeight = 38, Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                row.Children.Add(label); Grid.SetColumn(control, 1); row.Children.Add(control);
                row.SizeChanged += (s, e) =>
                {
                    bool narrow = e.NewSize.Width < 320;
                    Grid.SetColumnSpan(label, narrow ? 2 : 1);
                    Grid.SetRow(control, narrow ? 1 : 0);
                    Grid.SetColumn(control, narrow ? 0 : 1);
                    Grid.SetColumnSpan(control, narrow ? 2 : 1);
                    label.Margin = new Thickness(0, 0, 0, narrow ? 6 : 0);
                    row.Margin = new Thickness(0, 0, 0, narrow ? 10 : 4);
                };
                return row;
            }
            var dday = TakeSettingsControl(SettingsCalendarDdayRow);
            dday.Margin = new Thickness(0, 4, 0, 4);
            Section("Common", "공통", TakeSettingsControl(SettingsThemeCard), TakeSettingsControl(SettingsOpacityCard));
            Section("Compact", "기본 모드",
                SettingsCard("표시 방식", Row(new TextBlock { Text = "한 번에 보일 날짜" }, settingsDayCount), dday,
                    Row(TakeSettingsControl(SettingsFlipLabel), TakeSettingsControl(InlineFlipEffectCombo))),
                TakeSettingsControl(SettingsCompactFontCard));
            Section("Month", "한 달 보기", TakeSettingsControl(SettingsMonthFontCard));
            Section("Details", "일정 상세", TakeSettingsControl(SettingsFontCard));
            UpdateCompactDDayAvailability();
            SelectAppearanceSection("Common");
            return root;
        }

        private void SelectAppearanceSection(string section)
        {
            if (!appearanceSections.ContainsKey(section)) return;
            appearanceSection = section;
            foreach (var page in appearanceSections) page.Value.Visibility = page.Key == section ? Visibility.Visible : Visibility.Collapsed;
            appearanceTabs[section].IsChecked = true;
            UpdateAppearancePreviewSize();
            UpdateCalendarSettingsPreviews();
        }

        private void UpdateAppearancePreviewSize()
        {
            if (appearancePreviewImage == null || appearanceAvailableHeight <= 0) return;
            // Month/Common need taller images; their shorter controls leave room for a larger preview.
            double height = appearanceSection == "Month" || appearanceSection == "Common"
                ? Math.Min(420, appearanceAvailableHeight - 240) : Math.Min(280, appearanceAvailableHeight * .42);
            appearancePreviewImage.MaxHeight = Math.Max(96, height);
        }

        private void AppearanceBlockDDayChanged(object sender, RoutedEventArgs e)
        {
            UpdateCompactDDayAvailability();
            UpdateCalendarSettingsPreviews();
        }

        private void UpdateCompactDDayAvailability()
        {
            if (SettingsCompactDdaySizeRow == null) return;
            bool enabled = InlineBlockDDaySwitch.IsChecked == true;
            SettingsCompactDdaySizeRow.IsEnabled = enabled;
            SettingsCompactDdaySizeRow.Opacity = enabled ? 1 : .4;
        }

        private Window CreateAppearancePreviewDialog()
        {
            var source = appearancePreviewImage?.Source;
            if (source == null || UnifiedSettings == null) return null;
            var picture = new Image { Source = source, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top, SnapsToDevicePixels = true };
            var body = new DockPanel { Margin = new Thickness(18) };
            var heading = new TextBlock { Text = "미리보기", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 42, 16) };
            DockPanel.SetDock(heading, Dock.Top); body.Children.Add(heading);
            body.Children.Add(new ScrollViewer { Content = picture, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Rect area = SystemParameters.WorkArea;
            var dialog = new Window { Title = "미리보기", Owner = UnifiedSettings, Content = body,
                Width = Math.Min(Math.Max(500, source.Width + 60), Math.Max(320, area.Width - 40)),
                Height = Math.Min(Math.Max(300, source.Height + 100), Math.Max(240, area.Height - 40)),
                WindowStartupLocation = WindowStartupLocation.CenterOwner };
            dialog.SetResourceReference(Window.BackgroundProperty, "AuxCanvasBrush");
            dialog.SetResourceReference(Window.ForegroundProperty, "AuxInkBrush");
            dialog.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; dialog.Close(); } };
            ChromelessWindow.Apply(dialog);
            return dialog;
        }

        private void UpdateCalendarSettingsPreviews()
        {
            if (appearancePreviewImage == null || _inlineSettingsDraft == null || settingsDayCount == null) return;
            if (appearancePreviewTimer == null)
            {
                appearancePreviewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(80) };
                appearancePreviewTimer.Tick += (s, e) => RenderAppearancePreviewNow();
            }
            if (!appearancePreviewTimer.IsEnabled) appearancePreviewTimer.Start();
        }

        private void RenderAppearancePreviewNow()
        {
            appearancePreviewTimer?.Stop();
            if (appearancePreviewImage == null || _inlineSettingsDraft == null || settingsDayCount == null) return;
            try
            {
                if (appearancePreviewWindow == null) appearancePreviewWindow = MiniWindow.CreateAppearancePreviewWindow();
                appearancePreviewImage.Source = appearancePreviewWindow.RenderAppearancePreview(appearanceSection,
                    CloneAppearance(_inlineSettingsDraft), settingsDayCount.SelectedIndex + 1, InlineBlockDDaySwitch.IsChecked == true);
                appearancePreviewImage.ToolTip = null;
                appearancePreviewErrorLogged = false;
            }
            catch (Exception error) when (error is InvalidOperationException || error is ArgumentException || error is System.Runtime.InteropServices.COMException)
            {
                if (!appearancePreviewErrorLogged) ErrorLog.Write("settings-preview", error);
                appearancePreviewErrorLogged = true;
                appearancePreviewImage.ToolTip = "미리보기를 만들지 못했습니다. 설정은 계속 변경할 수 있습니다.";
            }
        }

        private void ReleaseAppearancePreview()
        {
            appearancePreviewTimer?.Stop();
            appearancePreviewTimer = null;
            if (appearancePreviewImage != null) appearancePreviewImage.Source = null;
            appearancePreviewImage = null;
            appearanceAvailableHeight = 0;
            appearancePreviewWindow?.Close();
            appearancePreviewWindow = null;
            appearancePreviewErrorLogged = false;
        }
    }
}
