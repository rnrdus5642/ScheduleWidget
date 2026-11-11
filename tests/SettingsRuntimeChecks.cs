using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static void LayoutSettings(SettingsWindow settings, double width = 1000)
        {
            var root = (FrameworkElement)settings.Content;
            root.Measure(new Size(width, 760)); root.Arrange(new Rect(0, 0, width, 760)); root.UpdateLayout();
        }

        private static void HostedSettingsAudit(MainWindow main, MiniWindow mini, AppData data)
        {
            Run("Google status remains usable before the connections page has been opened", () =>
            {
                string previousToken = data.GoogleCalendar.ProtectedRefreshToken;
                object previousSignIn = Field(main, "googleSignIn");
                var settings = OpenSettingsForCheck(main);
                try
                {
                    Require(((FrameworkElement)Field(main, "settingsGoogleContent")).Parent == null,
                        "The check must exercise the temporarily detached Google settings card.");
                    foreach (bool signedIn in new[] { false, true })
                    {
                        data.GoogleCalendar.ProtectedRefreshToken = signedIn ? "isolated-ui-check" : null;
                        SetField(main, "googleSyncing", true);
                        Call(main, "UpdateGoogleUi");
                        Require(ReferenceEquals(Control<Button>(main, "GoogleSignInButton").Style,
                            Control<FrameworkElement>(main, "InlineSettingsPanel").FindResource(signedIn ? "AuxUtilityButton" : "GoogleSignInButtonStyle")),
                            "Google sign-in button lost the appropriate style.");
                        var button = Control<Button>(main, "GoogleSignInButton");
                        button.ApplyTemplate();
                        var artwork = button.Template.FindName("GoogleSignInArtwork", button) as Image;
                        Require(signedIn ? artwork == null : artwork?.Source != null,
                            "Only the signed-out action must display the embedded Google artwork.");
                        if (!signedIn) Require(System.Windows.Automation.AutomationProperties.GetName(button) == "Google 계정으로 로그인",
                            "The image button needs an accessible sign-in label.");
                        SetField(main, "googleSyncing", false);
                        Call(main, "UpdateGoogleUi");
                    }
                    using (var pending = new System.Threading.CancellationTokenSource())
                    {
                        data.GoogleCalendar.ProtectedRefreshToken = null;
                        SetField(main, "googleSignIn", pending);
                        Call(main, "UpdateGoogleUi");
                        var button = Control<Button>(main, "GoogleSignInButton");
                        Require((string)button.Content == "로그인 취소" &&
                            ReferenceEquals(button.Style, Control<FrameworkElement>(main, "InlineSettingsPanel").FindResource("AuxUtilityButton")),
                            "A pending sign-in must expose a plain cancel action instead of the sign-in image.");
                    }
                }
                finally
                {
                    data.GoogleCalendar.ProtectedRefreshToken = previousToken;
                    SetField(main, "googleSignIn", previousSignIn);
                    SetField(main, "googleSyncing", false);
                    Call(main, "CloseInlineSettings", false);
                }
            });

            Run("Appearance opens with Common and renders live sample images without touching real schedules", () =>
            {
                var settings = OpenSettingsForCheck(main);
                MiniWindow previewWindow = null;
                try
                {
                    settings.SelectPage(SettingsPage.Appearance);
                    var tabs = (System.Collections.Generic.Dictionary<string, RadioButton>)Field(main, "appearanceTabs");
                    Require(ReferenceEquals(((Panel)tabs["Common"].Parent).Children[0], tabs["Common"]) && tabs["Common"].IsChecked == true,
                        "Common is not first and selected when opening appearance settings.");
                    string Picture()
                    {
                        Call(main, "RenderAppearancePreviewNow");
                        var picture = ((Image)Field(main, "appearancePreviewImage")).Source as BitmapSource;
                        Require(picture != null && picture.PixelWidth > 300 && picture.PixelHeight > 100, "The example image was not rendered.");
                        int stride = picture.PixelWidth * 4;
                        var pixels = new byte[stride * picture.PixelHeight]; picture.CopyPixels(pixels, stride, 0);
                        using (var hash = System.Security.Cryptography.SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(pixels));
                    }
                    string originalData = Newtonsoft.Json.JsonConvert.SerializeObject(data);
                    Picture();
                    Require(Newtonsoft.Json.JsonConvert.SerializeObject(data) == originalData, "Creating the sample changed real schedules or settings.");
                    previewWindow = (MiniWindow)Field(main, "appearancePreviewWindow");
                    Require(!previewWindow.IsLoaded && !previewWindow.IsVisible && new System.Windows.Interop.WindowInteropHelper(previewWindow).Handle == IntPtr.Zero,
                        "The sample created a live desktop window.");
                    Call(main, "SelectAppearanceSection", "Compact");
                    string compact = Picture();
                    Control<Slider>(main, "InlineCompactTitleFontSizeSlider").Value = 24;
                    Require(Picture() != compact && (double)previewWindow.Resources["CompactTitleFontSize"] == 24, "Compact text changes do not alter the image.");
                    compact = Picture();
                    ((ComboBox)Field(main, "settingsDayCount")).SelectedIndex = 1;
                    Require(Picture() != compact && Control<ItemsControl>(previewWindow, "WeekDays").Items.Count == 2, "Changing visible days does not alter the sample.");
                    Control<System.Windows.Controls.Primitives.ToggleButton>(main, "InlineBlockDDaySwitch").IsChecked = true;
                    compact = Picture();
                    Control<System.Windows.Controls.Primitives.ToggleButton>(main, "InlineBlockDDaySwitch").IsChecked = false;
                    Require(Picture() != compact, "The D-day switch does not alter the sample.");
                    Call(main, "SelectAppearanceSection", "Month");
                    string month = Picture();
                    Control<Slider>(main, "InlineMonthTitleFontSizeSlider").Value = 24;
                    Require(Picture() != month && previewWindow.IsMonthViewOpen, "Monthly size changes do not alter the month image.");
                    Require(VisualDescendants<TextBlock>(Control<ItemsControl>(previewWindow, "MonthDays"))
                        .Any(t => t.Name == "MonthEventTitle" && t.Text == "보고서 제출" && t.FontSize == 24 && t.ActualWidth > 0),
                        "The monthly example hides its sample title behind an overflow link.");
                    Call(main, "SelectAppearanceSection", "Details");
                    string detail = Picture();
                    Control<Slider>(main, "InlineTitleFontSizeSlider").Value = 24;
                    Control<Slider>(main, "InlineDDayFontSizeSlider").Value = 24;
                    Require(Picture() != detail && (double)previewWindow.Resources["TitleFontSize"] == 24, "Detail font changes do not alter the image.");
                    Call(main, "SelectAppearanceSection", "Common");
                    string common = Picture();
                    Control<Slider>(main, "InlineOpacitySlider").Value = 35;
                    Require(Picture() != common, "Opacity changes do not alter the desktop example.");
                    Require(Control<FrameworkElement>(previewWindow, "MiniEditorPanel").Visibility == Visibility.Visible &&
                        Control<FrameworkElement>(previewWindow, "DayBubble").Opacity == .35,
                        "The common preview does not include the schedule editor at the selected opacity.");
                    common = Picture();
                    Control<ComboBox>(main, "InlinePresetCombo").SelectedIndex = Control<ComboBox>(main, "InlinePresetCombo").SelectedIndex == 1 ? 0 : 1;
                    Require(Picture() != common, "Changing the theme did not update the calendar and entry preview.");
                    Require(Control<TextBlock>(settings, "PageDescription").Visibility == Visibility.Collapsed,
                        "Appearance still reserves space for the removed introductory text.");
                    Call(main, "SelectAppearanceSection", "Compact"); Picture();
                    LayoutSettings(settings, 760);
                    var image = (Image)Field(main, "appearancePreviewImage");
                    Point beforeScroll = image.TranslatePoint(new Point(), (UIElement)settings.Content);
                    var sections = (System.Collections.Generic.Dictionary<string, FrameworkElement>)Field(main, "appearanceSections");
                    var controls = (ScrollViewer)sections["Compact"];
                    Point controlsStart = controls.TranslatePoint(new Point(), (UIElement)settings.Content);
                    Require(image.ActualWidth > 460 && image.TranslatePoint(new Point(0, image.ActualHeight), (UIElement)settings.Content).Y < controlsStart.Y &&
                        controls.ActualHeight > 180, "The full-width preview is too small or leaves no usable settings area.");
                    controls.ScrollToEnd(); LayoutSettings(settings, 760);
                    Require(image.ActualHeight > 80 && image.TranslatePoint(new Point(), (UIElement)settings.Content) == beforeScroll,
                        "Scrolling the controls hides or moves the example image.");
                    // WPF requires an owner's HWND. Create it hidden, without activating or showing settings.
                    new System.Windows.Interop.WindowInteropHelper(settings).EnsureHandle();
                    var enlarged = (Window)typeof(MainWindow).GetMethod("CreateAppearancePreviewDialog",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(main, null);
                    try
                    {
                        var body = (FrameworkElement)enlarged.Content;
                        body.Measure(new Size(520, 400)); body.Arrange(new Rect(0, 0, 520, 400)); body.UpdateLayout();
                        var picture = VisualDescendants<Image>(body).Single();
                        Require(ReferenceEquals(enlarged.Owner, settings) && ReferenceEquals(picture.Source, image.Source) &&
                            picture.Stretch == Stretch.None && picture.ActualWidth >= picture.Source.Width && !enlarged.IsVisible,
                            "The enlarged preview is not owned by settings or shrinks the picture again.");
                    }
                    finally { enlarged.Close(); }
                }
                finally { Call(main, "CloseInlineSettings", false); }
                Require(Field(main, "appearancePreviewWindow") == null && Field(main, "appearancePreviewTimer") == null,
                    "Closing settings retained the sample window or render timer.");
            });

            Run("Saving unrelated settings preserves a date-count change made on the calendar", () =>
            {
                int original = data.MiniDayCount;
                var settings = OpenSettingsForCheck(main);
                try
                {
                    mini.SetDayCount(original == 3 ? 4 : 3);
                    int changed = data.MiniDayCount;
                    Require(((ComboBox)Field(main, "settingsDayCount")).SelectedIndex + 1 == changed, "Untouched settings do not follow the calendar's newer selection.");
                    Call(main, "InlineSettingsApplyButton_Click", settings, ClickArgs());
                    Require(data.MiniDayCount == changed && Control<ItemsControl>(mini, "WeekDays").Items.Count == changed,
                        "Saving an untouched display-days field reverted the calendar's newer selection.");
                    var loaded = ((IAppDataStore)Field(main, "dataStore")).LoadData().Data;
                    Require(loaded.MiniDayCount == changed, "The newer calendar selection was not persisted.");
                }
                finally
                {
                    if (Field(main, "settingsHost") != null) Call(main, "CloseInlineSettings", false);
                    mini.SetDayCount(original);
                }
            });

            Run("Explicit day-count drafts win on save and cancellation keeps calendar changes", () =>
            {
                int original = data.MiniDayCount;
                var settings = OpenSettingsForCheck(main);
                try
                {
                    ((ComboBox)Field(main, "settingsDayCount")).SelectedIndex = 1;
                    mini.SetDayCount(4);
                    Require(((ComboBox)Field(main, "settingsDayCount")).SelectedIndex == 1, "Refresh overwrote an explicitly edited draft.");
                    Call(main, "InlineSettingsApplyButton_Click", settings, ClickArgs());
                    Require(data.MiniDayCount == 2, "Explicit draft did not take effect.");
                    OpenSettingsForCheck(main);
                    ((ComboBox)Field(main, "settingsDayCount")).SelectedIndex = 2;
                    mini.SetDayCount(5);
                    Call(main, "CloseInlineSettings", false);
                    Require(data.MiniDayCount == 5, "Cancel reverted a change made on the calendar.");
                }
                finally
                {
                    if (Field(main, "settingsHost") != null) Call(main, "CloseInlineSettings", false);
                    mini.SetDayCount(original);
                }
            });

            Run("Pointer changes apply the displayed font, opacity and placement tick values", () =>
            {
                OpenSettingsForCheck(main);
                void Pointer(Slider slider, double value) => typeof(SliderInteraction).GetMethod("SetPointerValue", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { slider, value });
                try
                {
                    Pointer(Control<Slider>(main, "InlineTitleFontSizeSlider"), 12.4);
                    Pointer(Control<Slider>(main, "InlineDDayFontSizeSlider"), 17.7);
                    Pointer(Control<Slider>(main, "InlineOpacitySlider"), 57.4);
                    var draft = (AppearanceSettings)Field(main, "_inlineSettingsDraft");
                    Require(draft.TitleFontSize == 12 && draft.DDayFontSize == 18 && draft.Opacity == .55 &&
                        Control<TextBlock>(main, "InlineTitleFontSizeText").Text == "12" && Control<TextBlock>(main, "InlineDDayFontSizeText").Text == "18" &&
                        Control<TextBlock>(main, "InlineOpacityValueText").Text == "55%", "The slider label and applied value disagree.");
                    var gaps = new Slider { Minimum = -80, Maximum = 60, TickFrequency = 2, IsSnapToTickEnabled = true };
                    Pointer(gaps, -19.2); Require(gaps.Value == -20, "Negative gap did not snap to a valid tick.");
                    Pointer(gaps, 70); Require(gaps.Value == 60, "Pointer exceeded the slider's maximum.");
                    var custom = new Slider { Minimum = 0, Maximum = 10, IsSnapToTickEnabled = true, Ticks = new DoubleCollection { 0, 3, 7, 10 } };
                    Pointer(custom, 5.2); Require(custom.Value == 7, "Explicit ticks were ignored.");
                    custom.IsSnapToTickEnabled = false;
                    Pointer(custom, 4.2); Require(custom.Value == 4.2, "Continuous slider unexpectedly snapped.");
                }
                finally { Call(main, "CloseInlineSettings", false); }
            });

            Run("Each hosted notification and provider field marks its deferred changes", () =>
            {
                var settings = OpenSettingsForCheck(main);
                try
                {
                    Call(main, "EnsureContactSettings");
                    var contact = (ContactWindow)Field(main, "contactWindow");
                    foreach (string name in new[] { "TelegramChat", "KakaoLink", "KakaoFriend", "KakaoAppKey", "PhoneNumber", "DaysBefore", "Hour", "Minute" })
                    {
                        contact.AcceptSavedSettings();
                        var box = Control<TextBox>(contact, name); box.Text += "1";
                        Require(contact.HasPendingChanges, "Changed text was ignored: " + name);
                    }
                    foreach (string name in new[] { "TelegramToken", "KakaoToken", "KakaoRefresh", "KakaoSecret" })
                    {
                        contact.AcceptSavedSettings(); Control<PasswordBox>(contact, name).Password = "isolated-audit";
                        Require(contact.HasPendingChanges, "Changed credential was ignored: " + name);
                    }
                    foreach (string name in new[] { "RemindersEnabled", "DesktopReminder", "TelegramReminder", "KakaoReminder" })
                    {
                        contact.AcceptSavedSettings(); var box = Control<CheckBox>(contact, name); box.IsChecked = box.IsChecked != true;
                        Require(contact.HasPendingChanges, "Changed checkbox was ignored: " + name);
                    }
                }
                finally { Call(main, "CloseInlineSettings", false); }
            });

            Run("Hosted music keeps playlist tools reachable without duplicate playback controls", () =>
            {
                var settings = OpenSettingsForCheck(main);
                try
                {
                    settings.SelectPage(SettingsPage.Music); Call(main, "PrepareSettingsPage", SettingsPage.Music);
                    LayoutSettings(settings, 760);
                    var music = (MusicWindow)Field(main, "musicWindow");
                    Require(Control<FrameworkElement>(music, "PlaybackControls").Visibility == Visibility.Collapsed,
                        "Settings duplicate the calendar's playback controls.");
                    var content = Control<FrameworkElement>(music, "MusicSettingsContent");
                    foreach (string name in new[] { "PlaylistCombo", "AddPlaylistButton", "EditPlaylistButton", "DeletePlaylistButton", "RepeatToggle", "PlayOptionsResetButton" })
                    {
                        var element = Control<FrameworkElement>(music, name);
                        Rect bounds = element.TransformToAncestor(content).TransformBounds(new Rect(element.RenderSize));
                        Require(bounds.Left >= -1 && bounds.Right <= content.ActualWidth + 1, "Music option is outside the visible page: " + name);
                    }
                    var selector = Control<ComboBox>(music, "PlaylistCombo");
                    Point before = selector.TranslatePoint(new Point(), content);
                    Require(selector.TranslatePoint(new Point(selector.ActualWidth, 0), content).X < Control<Button>(music, "AddPlaylistButton").TranslatePoint(new Point(), content).X,
                        "Playlist actions do not sit to the right of the selector.");
                    Control<ScrollViewer>(music, "MusicBodyScroll").ScrollToEnd(); LayoutSettings(settings, 760);
                    Require(selector.TranslatePoint(new Point(), content) == before && music.FindName("YouTubeLinks") == null,
                        "Scrolling moved the selector or the import form remains on the main page.");
                }
                finally { Call(main, "CloseInlineSettings", false); }
            });

            Run("Playlist URL expansion preference survives reopening the player", () =>
            {
                var settings = new MusicSettings(); var dataCopy = new AppData { Music = settings };
                var store = Store("playlist-expansion-preference"); int writes = 0;
                var music = new MusicWindow(settings, () => { writes++; store.SaveData(dataCopy); return true; }, () => { }, embedded: true);
                try
                {
                    var editor = music.CreatePlaylistEditor(false);
                    Control<CheckBox>(editor, "ExpandPlaylists").IsChecked = false;
                    Require(editor.TryCommit(), "Popup expansion preference did not save."); editor.Close();
                    Require(writes > 0, "Playlist expansion option is never saved.");
                    var reloaded = new MusicWindow(store.LoadData().Data.Music, () => true, () => { }, embedded: true);
                    try {
                        var reopened = reloaded.CreatePlaylistEditor(false);
                        try { Require(Control<CheckBox>(reopened, "ExpandPlaylists").IsChecked == false, "Playlist expansion resets on reopening."); }
                        finally { reopened.Close(); }
                    }
                    finally { reloaded.Close(); }
                    Call(music, "PlayOptionsReset_Click", music, ClickArgs());
                    Require(settings.ExpandYouTubePlaylists == false, "Reset ignored its confirmation step.");
                    Call(music, "PlayOptionsReset_Click", music, ClickArgs());
                    Require(settings.ExpandYouTubePlaylists && store.LoadData().Data.Music.ExpandYouTubePlaylists, "Reset did not persist the default expansion option.");
                }
                finally { music.Close(); }
            });
        }
    }
}
