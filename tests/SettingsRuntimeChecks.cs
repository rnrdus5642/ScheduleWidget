using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
                            Control<FrameworkElement>(main, "InlineSettingsPanel").FindResource(signedIn ? "AuxUtilityButton" : "AuxPrimaryButton")),
                            "Google sign-in button lost the appropriate style.");
                        SetField(main, "googleSyncing", false);
                        Call(main, "UpdateGoogleUi");
                    }
                }
                finally
                {
                    data.GoogleCalendar.ProtectedRefreshToken = previousToken;
                    SetField(main, "googleSyncing", false);
                    Call(main, "CloseInlineSettings", false);
                }
            });

            Run("Hosted font preview reflects both sliders and the chosen title/status colors", () =>
            {
                var settings = OpenSettingsForCheck(main);
                try
                {
                    settings.SelectPage(SettingsPage.Appearance);
                    Control<Slider>(main, "InlineTitleFontSizeSlider").Value = 23;
                    Control<Slider>(main, "InlineDDayFontSizeSlider").Value = 21;
                    var draft = (AppearanceSettings)Field(main, "_inlineSettingsDraft");
                    draft.TextColor = "#125634"; draft.FutureColor = "#652143";
                    Call(main, "ApplyAppearance", draft);
                    LayoutSettings(settings);
                    var sample = Control<Border>(main, "SettingsFontCard");
                    var title = VisualDescendants<TextBlock>(sample).Single(t => t.Text == "보고서 제출");
                    var status = VisualDescendants<TextBlock>(sample).Single(t => t.Text == "D-12");
                    Require(title.FontSize == 23 && status.FontSize == 21,
                        "Hosted sample ignores sliders: title=" + title.FontSize + ", D-day=" + status.FontSize);
                    Require(((SolidColorBrush)title.Foreground).Color == (Color)ColorConverter.ConvertFromString(draft.TextColor) &&
                        ((SolidColorBrush)status.Foreground).Color == (Color)ColorConverter.ConvertFromString(draft.FutureColor), "Hosted sample masks selected title or status colors.");
                    Require(((SolidColorBrush)Control<Border>(main, "SettingsFontPreview").Background).Color == (Color)ColorConverter.ConvertFromString(draft.CardColor), "Card color is masked in the sample.");
                }
                finally { Call(main, "CloseInlineSettings", false); }
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

            Run("Hosted music options remain reachable at the minimum settings width", () =>
            {
                var settings = OpenSettingsForCheck(main);
                try
                {
                    settings.SelectPage(SettingsPage.Music); Call(main, "PrepareSettingsPage", SettingsPage.Music);
                    LayoutSettings(settings, 760);
                    var music = (MusicWindow)Field(main, "musicWindow");
                    var content = Control<FrameworkElement>(music, "MusicSettingsContent");
                    var clip = VisualDescendants<ScrollContentPresenter>(content).First();
                    foreach (string name in new[] { "VolumeSlider", "RepeatToggle", "ShuffleToggle", "RepeatOneToggle", "PlayOptionsResetButton" })
                    {
                        var element = Control<FrameworkElement>(music, name);
                        Rect bounds = element.TransformToAncestor(clip).TransformBounds(new Rect(element.RenderSize));
                        Require(bounds.Left >= -1 && bounds.Right <= clip.ActualWidth + 1,
                            "Music option is outside the visible page: " + name + " (" + bounds + ", viewport=" + clip.ActualWidth + ")");
                    }
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
                    var expand = Control<CheckBox>(music, "ExpandPlaylists");
                    expand.IsChecked = false; expand.RaiseEvent(ClickArgs());
                    Require(writes > 0, "Playlist expansion option is never saved.");
                    var reloaded = new MusicWindow(store.LoadData().Data.Music, () => true, () => { }, embedded: true);
                    try { Require(Control<CheckBox>(reloaded, "ExpandPlaylists").IsChecked == false, "Playlist expansion resets on reopening."); }
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
