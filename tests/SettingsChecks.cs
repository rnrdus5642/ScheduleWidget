using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScheduleWidget.Checks
{
    internal static partial class Checks
    {
        private static T Control<T>(Window window, string name) where T : class => window.FindName(name) as T;
        private static RoutedEventArgs ClickArgs() => new RoutedEventArgs(Button.ClickEvent);
        private static void LayoutAudit(MiniWindow mini)
        {
            var content = (FrameworkElement)mini.Content;
            content.Measure(new Size(800, 320)); content.Arrange(new Rect(0, 0, 800, 320)); content.UpdateLayout();
        }
        private static void SettingsAudit()
        {
            var main = new MainWindow();
            ((TrayService)Field(main, "trayService")).Dispose();
            var data = new AppData { StartupEnabled = false, BringToFrontHotKeyEnabled = false, MiniCharacterVisible = false };
            data.Schedules.Add(new ScheduleItem { Title = "Audit title", Period = DateTime.Today.ToString("yyyy-MM-dd") });
            data.Schedules.Add(new ScheduleItem { Title = "Future", Period = DateTime.Today.AddDays(2).ToString("yyyy-MM-dd") });
            data.Schedules.Add(new ScheduleItem { Title = "Past", Period = DateTime.Today.AddDays(-2).ToString("yyyy-MM-dd") });
            SetField(main, "dataStore", Store("audit-settings")); SetField(main, "appData", data);
            SetField(main, "startupService", new StartupService("Software\\ScheduleWidgetChecks\\audit-" + Guid.NewGuid().ToString("N")));
            var mini = new MiniWindow(data, () =>
            {
                bool saved = (bool)typeof(MainWindow).GetMethod("SaveDataSafely", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(main, new object[] { false });
                Call(main, "RefreshScheduleList");
                return saved;
            }, () => { }, player: main);
            SetField(main, "miniWindow", mini); Call(main, "ApplyAppearance", data.Appearance);
            SettingsWindow Open()
            {
                Call(main, "BeginSettingsEdit"); var window = new SettingsWindow(); SetField(main, "settingsHost", window);
                Call(main, "BuildUnifiedSettingsPages", window); return window;
            }
            void Cancel() => Call(main, "CloseInlineSettings", false);
            void Save() => Call(main, "InlineSettingsApplyButton_Click", main, ClickArgs());
            var settings = Open();
            foreach (var theme in new[] { "Light", "Dark", "Blue", "Pink", "Modern" })
                Run("AUDIT 화면/테마 미리보기 " + theme, () => {
                    Control<ComboBox>(main, "InlinePresetCombo").SelectedIndex = Array.IndexOf(new[] { "Light", "Dark", "Blue", "Pink", "Modern" }, theme);
                    Require(mini.ThemeName == theme && AuxTheme.Current == theme, "Calendar or settings did not reflect the theme.");
                });
            Cancel();
            Run("AUDIT 화면/미리보기 취소", () => Require(mini.ThemeName == "Light" && data.Appearance.ThemePreset == "Light", "Theme cancellation failed."));
            settings = Open(); mini.SetAllSchedulesOpen(true); LayoutAudit(mini);
            var agenda = Control<ItemsControl>(mini, "AllSchedulesList");
            Run("AUDIT 화면/투명도 실제 표시", () => {
                Control<Slider>(main, "InlineOpacitySlider").Value = 45;
                double opacity = 1;
                for (DependencyObject node = Control<FrameworkElement>(mini, "AllSchedulesPanel"); node != null; node = VisualTreeHelper.GetParent(node))
                    if (node is UIElement element) opacity *= element.Opacity;
                Require(Math.Abs(opacity - .45) < .01, "Visible agenda opacity remains " + opacity + "; only legacy MainBorder changes.");
            });
            Run("AUDIT 화면/제목 글자 크기 실제 표시", () => {
                Control<Slider>(main, "InlineTitleFontSizeSlider").Value = 20; LayoutAudit(mini);
                double actual = VisualDescendants<TextBlock>(agenda).First(t => t.Text == "Audit title").FontSize;
                Require(actual == 20, "Agenda title font remains " + actual + ".");
            });
            Run("AUDIT 화면/D-day 글자 크기 실제 표시", () => {
                Control<Slider>(main, "InlineDDayFontSizeSlider").Value = 18; LayoutAudit(mini);
                double actual = VisualDescendants<TextBlock>(agenda).First(t => t.Text == "D-day").FontSize;
                Require(actual == 18, "Agenda D-day font remains " + actual + ".");
            });
            foreach (string property in new[] { "BackgroundColor", "CardColor", "TextColor", "TodayColor", "FutureColor", "PastColor" })
                Run("AUDIT 화면/개별 색상 실제 표시 " + property, () => {
                    var draft = (AppearanceSettings)Field(main, "_inlineSettingsDraft");
                    typeof(AppearanceSettings).GetProperty(property).SetValue(draft, "#123456"); Call(main, "ApplyAppearance", draft); LayoutAudit(mini);
                    string actual;
                    if (property == "BackgroundColor") actual = ((SolidColorBrush)Control<Border>(mini, "AllSchedulesPanel").Background).Color.ToString();
                    else if (property == "CardColor") actual = (string)agenda.Items[0].GetType().GetProperty("Background").GetValue(agenda.Items[0]);
                    else {
                        string label = property == "TextColor" ? "Audit title" : property == "TodayColor" ? "D-day" : property == "FutureColor" ? "D-2" : "D+2";
                        actual = ((SolidColorBrush)VisualDescendants<TextBlock>(agenda).First(t => t.Text == label).Foreground).Color.ToString();
                    }
                    Require(actual.EndsWith("123456", StringComparison.OrdinalIgnoreCase), "Visible color remains " + actual + "; custom color is only applied to the retired view.");
                });
            Cancel(); mini.SetAllSchedulesOpen(false);
            foreach (int days in Enumerable.Range(1, 7)) Run("AUDIT 화면/표시 일수 저장 " + days, () => {
                Open(); ((ComboBox)Field(main, "settingsDayCount")).SelectedIndex = days - 1; Save();
                Require(data.MiniDayCount == days && Control<ItemsControl>(mini, "WeekDays").Items.Count == days, "Day count not applied.");
            });
            foreach (int effect in Enumerable.Range(0, 7)) Run("AUDIT 화면/넘김 효과 저장 " + effect, () => {
                Open(); var combo = Control<ComboBox>(main, "InlineFlipEffectCombo"); combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(c => (string)c.Tag == effect.ToString()); Save();
                Require(data.MiniFlipEffect == effect, "Flip selection not persisted.");
            });
            Run("AUDIT 화면/일정 블록 D-day 켜기 끄기", () => {
                foreach (bool enabled in new[] { false, true }) { Open(); Control<System.Windows.Controls.Primitives.ToggleButton>(main, "InlineBlockDDaySwitch").IsChecked = enabled; Save();
                    var cells = Control<ItemsControl>(mini, "WeekDays").Items.Cast<MiniWindow.DayCell>();
                    var block = cells.SelectMany(c => ((System.Collections.IEnumerable)c.TaskBlocks).Cast<object>()).First();
                    Require((string)block.GetType().GetProperty("DDay").GetValue(block) == (enabled ? "D-day" : ""), "Block D-day not applied."); }
            });
            Run("AUDIT 일반/항상 위 저장 및 창 속성", () => {
                foreach (bool on in new[] { true, false }) { Open(); Control<System.Windows.Controls.Primitives.ToggleButton>(main, "InlineAlwaysOnTopToggle").IsChecked = on; Save(); Require(data.AlwaysOnTop == on && mini.Topmost == on, "Topmost not applied."); }
            });
            Run("AUDIT 일반/초기화 및 취소", () => {
                Open(); var startup = Control<System.Windows.Controls.Primitives.ToggleButton>(main, "InlineStartupToggle").IsChecked;
                Call(main, "ResetUnifiedSettingsPage", SettingsPage.General);
                Require(Control<System.Windows.Controls.Primitives.ToggleButton>(main, "InlineStartupToggle").IsChecked == startup && Control<CheckBox>(main, "InlineHotKeyToggle").IsChecked == true, "General reset changed startup or omitted hotkey default."); Cancel();
                Require(!data.BringToFrontHotKeyEnabled && !data.StartupEnabled, "Cancel saved general changes.");
            });
            Run("AUDIT 화면/페이지 초기화", () => {
                Open(); Call(main, "ResetUnifiedSettingsPage", SettingsPage.Appearance);
                Require(((ComboBox)Field(main, "settingsDayCount")).SelectedIndex == 6 && Control<Slider>(main, "InlineOpacitySlider").Value == 100 && mini.ThemeName == "Light", "Appearance reset failed."); Cancel();
            });
            Run("AUDIT 연동/구글 로그인 전 스위치 잠금", () => { Call(main, "UpdateGoogleUi"); Require(!Control<Control>(main, "GoogleCalendarSwitch").IsEnabled && !Control<Control>(main, "GooglePetsSwitch").IsEnabled, "Signed-out sync switches enabled."); });
            Run("AUDIT 연동/구글 캘린더 및 드라이브 스위치 저장 (오프라인)", () => {
                data.GoogleCalendar.ProtectedRefreshToken = "offline-audit-token";
                foreach (string channel in new[] { "GoogleCalendar", "GooglePets" }) foreach (bool on in new[] { true, false }) {
                    Control<System.Windows.Controls.Primitives.ToggleButton>(main, channel + "Switch").IsChecked = on; Call(main, channel + "Switch_Click", main, ClickArgs());
                    Require((channel == "GoogleCalendar" ? data.GoogleCalendar.Enabled : data.GoogleCalendar.PetsEnabled) == on, "Google switch not saved."); }
                data.GoogleCalendar.ProtectedRefreshToken = null; Call(main, "UpdateGoogleUi");
            });
            Run("AUDIT 앱 정보/현재 버전 및 업데이트 비활성 상태", () => { Call(main, "LoadUpdateUi"); Require(Control<TextBlock>(main, "UpdateCurrentVersionText").Text.Contains(UpdateInfo.CurrentVersionText) && !Control<Button>(main, "UpdateButton").IsEnabled, "Version or no-release state invalid."); });
            Run("AUDIT 일반/자동 시작 등록 해제 (임시 레지스트리 경로)", () => {
                string key = "Software\\ScheduleWidgetChecks\\startup-audit-" + Guid.NewGuid().ToString("N");
                var service = new StartupService(key);
                try { Require(service.TryEnableStartup(out string error), error); Require(service.IsStartupEnabled(out bool enabled, out error) && enabled, "Startup registration not read back.");
                    Require(service.TryDisableStartup(out error), error); Require(service.IsStartupEnabled(out enabled, out error) && !enabled, "Startup removal failed."); }
                finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, false); }
            });
            Run("AUDIT 음악/달력 음악 막대 표시 켜기 끄기", () => {
                Open(); var box = (CheckBox)Field(main, "settingsPlayerVisible");
                foreach (bool shown in new[] { false, true }) { box.IsChecked = shown; box.RaiseEvent(ClickArgs()); Require(data.MiniPlayerVisible == shown && Control<FrameworkElement>(mini, "PlayerBar").Visibility == (shown ? Visibility.Visible : Visibility.Collapsed), "Player bar toggle failed."); }
                Cancel(); Require(data.MiniPlayerVisible, "Closing settings reverted an immediate music option.");
            });
            Run("AUDIT 공통/저장 실패 시 설정창 유지", () => {
                Open(); var previousStore = Field(main, "dataStore");
                SetField(main, "dataStore", new AuditFailingStore());
                // The previous error has already been acknowledged; suppress repeating its real MessageBox in this audit.
                SetField(main, "_saveErrorShown", true);
                try { Save(); Require(Field(main, "settingsHost") != null && Field(main, "_inlineSettingsDraft") != null,
                    "Settings close and discard their editing session even though the data store rejected the save."); }
                finally { SetField(main, "dataStore", previousStore); SetField(main, "_saveErrorShown", false); if (Field(main, "settingsHost") != null) Cancel(); }
            });
            SettingsRetryChecks(main, mini, data);
            ScheduleMenuChecks(main, mini, data);
            AppearancePersistenceCheck(main, mini, data);
            ContactAudit(); PetAudit(); MusicAudit();
            Console.WriteLine("AUDIT MANUAL: 실제 Windows 로그인 자동 실행, 전역 단축키 호출, 화면 위 고정/모니터 전환, 효과 1~6 애니메이션, 캐릭터 파일 선택/가져오기/내보내기 및 실제 WebView2/타이핑 반응/드래그, 파일 선택창과 삭제 확인창, YouTube 네트워크 재생/목록 가져오기, 구글 OAuth/실제 캘린더·Drive 동기화, PC 알림 실제 표시, Telegram/Kakao 실제 전송, 휴대폰·전화·도움말 앱 열기, 업데이트 다운로드·설치는 실행하지 않음.");
            mini.Close();
        }

        private sealed class ControlledSettingsStore : IAppDataStore
        {
            private readonly IAppDataStore inner;
            public bool Fail = true;
            public int Writes;
            public ControlledSettingsStore(IAppDataStore inner) { this.inner = inner; }
            public DataLoadResult LoadData() => inner.LoadData();
            public void SaveData(AppData data)
            {
                Writes++;
                if (Fail) throw new DataStorageException("Isolated audit failure", new IOException("Synthetic write failure"));
                inner.SaveData(data);
            }
        }

        private static SettingsWindow OpenSettingsForCheck(MainWindow main)
        {
            Call(main, "BeginSettingsEdit");
            var settings = new SettingsWindow();
            SetField(main, "settingsHost", settings);
            Call(main, "BuildUnifiedSettingsPages", settings);
            return settings;
        }

        private static void SettingsRetryChecks(MainWindow main, MiniWindow mini, AppData data)
        {
            Run("Settings commit failure rolls back deferred data; retry writes all pages once", () =>
            {
                var originalStore = (IAppDataStore)Field(main, "dataStore");
                var store = new ControlledSettingsStore(originalStore);
                var beforeAppearance = data.Appearance;
                var beforeCommunication = data.Communication;
                var beforeReminders = data.Reminders;
                int beforeDays = data.MiniDayCount;
                try
                {
                    var settings = OpenSettingsForCheck(main);
                    SetField(main, "dataStore", store);
                    Call(main, "EnsureContactSettings");
                    var contact = (ContactWindow)Field(main, "contactWindow");
                    Control<TextBox>(contact, "TelegramChat").Text = "retry-chat";
                    Control<TextBox>(contact, "DaysBefore").Text = "3";
                    Control<Slider>(main, "InlineTitleFontSizeSlider").Value = 22;
                    ((ComboBox)Field(main, "settingsDayCount")).SelectedIndex = 2;
                    data.MiniCharacterGap = 27; // an immediate pet change must survive both attempts
                    Call(main, "InlineSettingsApplyButton_Click", settings, ClickArgs());
                    Require(store.Writes == 1 && ReferenceEquals(Field(main, "settingsHost"), settings), "Failed save partially wrote another page or closed settings.");
                    Require(ReferenceEquals(data.Appearance, beforeAppearance) && ReferenceEquals(data.Communication, beforeCommunication) &&
                        ReferenceEquals(data.Reminders, beforeReminders) && data.MiniDayCount == beforeDays, "Failed save left deferred values in shared data.");
                    Require(contact.HasPendingChanges && Control<TextBox>(contact, "TelegramChat").Text == "retry-chat" &&
                        Control<Slider>(main, "InlineTitleFontSizeSlider").Value == 22, "Failed save discarded editable inputs.");
                    Require(data.MiniCharacterGap == 27 && !string.IsNullOrWhiteSpace(Control<TextBlock>(settings, "SettingsStatus").Text), "Failure lost an immediate setting or omitted the error message.");
                    store.Fail = false;
                    Call(main, "InlineSettingsApplyButton_Click", settings, ClickArgs());
                    var saved = originalStore.LoadData().Data;
                    Require(store.Writes == 2 && Field(main, "settingsHost") == null, "Retry did not commit exactly once and close.");
                    Require(saved.Appearance.TitleFontSize == 22 && saved.MiniDayCount == 3 && saved.Reminders.DaysBefore == 3 &&
                        saved.Communication.TelegramChatId == "retry-chat" && saved.MiniCharacterGap == 27, "Retry missed a deferred page or an immediate change.");
                }
                finally
                {
                    SetField(main, "dataStore", originalStore);
                    if (Field(main, "settingsHost") != null) Call(main, "CloseInlineSettings", false);
                }
            });

            Run("Cancel after a failed settings save restores the preview and saved values", () =>
            {
                var originalStore = (IAppDataStore)Field(main, "dataStore");
                double beforeFont = data.Appearance.TitleFontSize;
                int beforeDays = data.MiniDayCount;
                try
                {
                    var settings = OpenSettingsForCheck(main);
                    SetField(main, "dataStore", new ControlledSettingsStore(originalStore));
                    Control<Slider>(main, "InlineTitleFontSizeSlider").Value = 24;
                    Control<Slider>(main, "InlineDDayFontSizeSlider").Value = 24;
                    Require((double)mini.Resources["AgendaDDayFontSize"] == 24, "The largest D-day font was clamped below the slider's limit.");
                    ((ComboBox)Field(main, "settingsDayCount")).SelectedIndex = 4;
                    Call(main, "InlineSettingsApplyButton_Click", settings, ClickArgs());
                    Call(main, "CloseInlineSettings", false);
                    Require(data.Appearance.TitleFontSize == beforeFont && data.MiniDayCount == beforeDays &&
                        (double)mini.Resources["AgendaTitleFontSize"] == beforeFont, "Cancel committed failed edits or left their preview on screen.");
                }
                finally
                {
                    SetField(main, "dataStore", originalStore);
                    if (Field(main, "settingsHost") != null) Call(main, "CloseInlineSettings", false);
                }
            });

            Run("Failed settings save restores startup registration in an isolated registry key", () =>
            {
                var originalStore = (IAppDataStore)Field(main, "dataStore");
                var originalStartup = Field(main, "startupService");
                string key = "Software\\ScheduleWidgetChecks\\rollback-" + Guid.NewGuid().ToString("N");
                var startup = new StartupService(key);
                try
                {
                    SetField(main, "startupService", startup);
                    var settings = OpenSettingsForCheck(main);
                    SetField(main, "dataStore", new ControlledSettingsStore(originalStore));
                    SetField(main, "_inlineStartupDraft", true);
                    Call(main, "InlineSettingsApplyButton_Click", settings, ClickArgs());
                    Require(startup.IsStartupEnabled(out bool enabled, out _) && !enabled && !data.StartupEnabled &&
                        Field(main, "settingsHost") != null, "Failed save left the startup registration changed.");
                    Call(main, "CloseInlineSettings", false);
                }
                finally
                {
                    SetField(main, "dataStore", originalStore);
                    SetField(main, "startupService", originalStartup);
                    Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, false);
                    if (Field(main, "settingsHost") != null) Call(main, "CloseInlineSettings", false);
                }
            });
        }

        private static void ScheduleMenuChecks(MainWindow main, MiniWindow mini, AppData data)
        {
            Run("Schedule color menu reaches the selected item and keeps text readable", () =>
            {
                var item = data.Schedules[0];
                Call(main, "ConnectScheduleActions", mini);
                SetField(main, "colorPickerOverride", (Func<string, string>)(_ => "#102030"));
                try
                {
                    SetField(mini, "blockItem", item);
                    Call(mini, "BlockColor_Click", mini, ClickArgs());
                    mini.SetAllSchedulesOpen(true);
                    var row = Control<ItemsControl>(mini, "AllSchedulesList").Items.Cast<object>()
                        .Single(value => ReferenceEquals(value.GetType().GetProperty("Item").GetValue(value), item));
                    Require(item.Color == "#102030" && (string)row.GetType().GetProperty("Ink").GetValue(row) == FeatureRules.ReadableText(item.Color), "Color menu did not update the item or its readable text.");
                    Call(mini, "BlockDefaultColor_Click", mini, ClickArgs());
                    Require(item.Color == null, "Default color action did not clear the item override.");
                }
                finally { SetField(main, "colorPickerOverride", null); mini.SetAllSchedulesOpen(false); }
            });
            Run("Schedule message action requests a draft and rejects a stale item", () =>
            {
                var menu = new MiniWindow(data, () => true, () => { });
                int requests = 0;
                menu.ScheduleMessageRequested += item => { Require(ReferenceEquals(item, data.Schedules[0]), "Message used another schedule."); requests++; };
                try
                {
                    SetField(menu, "blockItem", data.Schedules[0]);
                    Call(menu, "BlockMessage_Click", menu, ClickArgs());
                    SetField(menu, "blockItem", new ScheduleItem());
                    Call(menu, "BlockMessage_Click", menu, ClickArgs());
                    Require(requests == 1 && !menu.IsVisible, "Message action used a removed item or showed a test window.");
                }
                finally { menu.Close(); }
            });
        }

        private static void AppearancePersistenceCheck(MainWindow main, MiniWindow mini, AppData data)
        {
            Run("All appearance controls persist, reload and restore after cancelling a new preview", () =>
            {
                string[] colors = { "BackgroundColor", "CardColor", "TextColor", "TodayColor", "FutureColor", "PastColor" };
                try
                {
                    var settings = OpenSettingsForCheck(main);
                    Control<Slider>(main, "InlineOpacitySlider").Value = 60;
                    Control<Slider>(main, "InlineTitleFontSizeSlider").Value = 19;
                    Control<Slider>(main, "InlineDDayFontSizeSlider").Value = 17;
                    SetField(main, "colorPickerOverride", (Func<string, string>)(_ => "#314159"));
                    foreach (Button button in Control<System.Windows.Controls.Primitives.UniformGrid>(main, "SettingsColorButtons").Children)
                        button.RaiseEvent(ClickArgs());
                    Call(main, "InlineSettingsApplyButton_Click", settings, ClickArgs());
                    var loaded = ((IAppDataStore)Field(main, "dataStore")).LoadData().Data;
                    Require(loaded.Appearance.Opacity == .6 && loaded.Appearance.TitleFontSize == 19 && loaded.Appearance.DDayFontSize == 17,
                        "Appearance sizes/opacity were not persisted.");
                    foreach (string color in colors)
                        Require((string)typeof(AppearanceSettings).GetProperty(color).GetValue(loaded.Appearance) == "#314159", "Color not persisted: " + color);
                    var reloaded = new MiniWindow(loaded, () => true, () => { });
                    try
                    {
                        reloaded.SetAllSchedulesOpen(true);
                        LayoutAudit(reloaded);
                        var row = Control<ItemsControl>(reloaded, "AllSchedulesList").Items[0];
                        Require(Control<FrameworkElement>(reloaded, "MiniRoot").Opacity == .6 && (double)reloaded.Resources["AgendaTitleFontSize"] == 19 &&
                            (double)reloaded.Resources["AgendaDDayFontSize"] == 17 && (string)row.GetType().GetProperty("Background").GetValue(row) == "#314159", "A reopened calendar ignored saved appearance.");
                    }
                    finally { reloaded.Close(); }
                    OpenSettingsForCheck(main);
                    Control<ComboBox>(main, "InlinePresetCombo").SelectedIndex = 1;
                    Control<Slider>(main, "InlineOpacitySlider").Value = 30;
                    Call(main, "CloseInlineSettings", false);
                    Require(data.Appearance.ThemePreset == "Light" && mini.ThemeName == "Light" && Control<FrameworkElement>(mini, "MiniRoot").Opacity == .6 &&
                        ((SolidColorBrush)Control<Border>(mini, "AllSchedulesPanel").Background).Color.ToString().EndsWith("314159"), "Cancelling a later preview lost saved custom appearance.");
                }
                finally
                {
                    SetField(main, "colorPickerOverride", null);
                    if (Field(main, "settingsHost") != null) Call(main, "CloseInlineSettings", false);
                }
            });
        }

        private sealed class AuditFailingStore : IAppDataStore
        {
            public DataLoadResult LoadData() => new DataLoadResult(new AppData());
            public void SaveData(AppData data) => throw new DataStorageException("Isolated audit save failure", new IOException("Synthetic write failure"));
        }

        private static void ContactAudit()
        {
            var data = new AppData(); bool saveOK = true; int saves = 0;
            var contact = new ContactWindow(data, new CommunicationService(), () => { saves++; return saveOK; }, embedded: true); contact.CreateEmbeddedViews();
            try {
                foreach (var invalid in new[] { ("DaysBefore", "-1"), ("DaysBefore", "31"), ("DaysBefore", "abc"), ("Hour", "24"), ("Minute", "60") })
                    Run("AUDIT 알림/잘못된 입력 거부 " + invalid.Item1 + "=" + invalid.Item2, () => {
                        var box = Control<TextBox>(contact, invalid.Item1); var old = box.Text; box.Text = invalid.Item2; int before = saves;
                        Require(!contact.SaveSettings() && saves == before && contact.HasPendingChanges, "Invalid reminder was saved."); box.Text = old;
                    });
                Run("AUDIT 알림/수신처 없음 거부", () => { Control<CheckBox>(contact, "RemindersEnabled").IsChecked = true; Control<CheckBox>(contact, "DesktopReminder").IsChecked = false; Require(!contact.SaveSettings(), "No destination accepted."); });
                Run("AUDIT 알림/토큰 없는 텔레그램 거부", () => { Control<CheckBox>(contact, "TelegramReminder").IsChecked = true; Require(!contact.SaveSettings(), "Missing Telegram credential accepted."); Control<CheckBox>(contact, "TelegramReminder").IsChecked = false; });
                Run("AUDIT 알림/토큰 없는 카카오 거부", () => { Control<CheckBox>(contact, "KakaoReminder").IsChecked = true; Require(!contact.SaveSettings(), "Missing Kakao credential accepted."); Control<CheckBox>(contact, "KakaoReminder").IsChecked = false; });
                Run("AUDIT 연동/모든 입력값 저장 및 암호화 복원", () => {
                    Control<CheckBox>(contact, "DesktopReminder").IsChecked = true;
                    foreach (string field in new[] { "TelegramToken", "KakaoToken", "KakaoRefresh", "KakaoSecret" }) Control<PasswordBox>(contact, field).Password = "audit-dummy-" + field;
                    foreach (string field in new[] { "TelegramChat", "KakaoFriend", "KakaoAppKey", "PhoneNumber" }) Control<TextBox>(contact, field).Text = "audit-" + field;
                    Control<TextBox>(contact, "KakaoLink").Text = "https://example.invalid";
                    Control<TextBox>(contact, "DaysBefore").Text = "30"; Control<TextBox>(contact, "Hour").Text = "23"; Control<TextBox>(contact, "Minute").Text = "59";
                    Control<CheckBox>(contact, "TelegramReminder").IsChecked = Control<CheckBox>(contact, "KakaoReminder").IsChecked = true;
                    Require(contact.SaveSettings() && !contact.HasPendingChanges && data.Reminders.DaysBefore == 30 && data.Reminders.Hour == 23 && data.Reminders.Minute == 59, "Valid reminders did not save.");
                    var reloaded = new ContactWindow(data, new CommunicationService(), () => true, embedded: true);
                    foreach (string field in new[] { "TelegramToken", "KakaoToken", "KakaoRefresh", "KakaoSecret" }) Require(Control<PasswordBox>(reloaded, field).Password == "audit-dummy-" + field, "Secret did not round trip.");
                    foreach (string field in new[] { "TelegramChat", "KakaoFriend", "KakaoAppKey", "PhoneNumber" }) Require(Control<TextBox>(reloaded, field).Text == "audit-" + field, "Connection field did not round trip."); reloaded.Close();
                });
                Run("AUDIT 알림/두 번 초기화 및 연동 정보 보존", () => { Call(contact, "ReminderReset_Click", contact, ClickArgs()); Require(Control<TextBox>(contact, "DaysBefore").Text == "30", "Reset did not require confirmation."); Call(contact, "ReminderReset_Click", contact, ClickArgs()); Require(Control<TextBox>(contact, "DaysBefore").Text == "0" && Control<CheckBox>(contact, "RemindersEnabled").IsChecked == false && Control<PasswordBox>(contact, "TelegramToken").Password == "audit-dummy-TelegramToken", "Reset touched secrets or failed defaults."); });
                Run("AUDIT 연동/저장 실패 시 이전 값 보존", () => { string old = data.Communication.TelegramChatId; Control<TextBox>(contact, "TelegramChat").Text = "new-unsaved"; saveOK = false; Require(!contact.SaveSettings() && data.Communication.TelegramChatId == old && contact.HasPendingChanges, "Failed save discarded or committed changes."); saveOK = true; });
                Run("AUDIT 연동/전송 입력 검증 (실제 전송 없음)", () => { Control<TextBox>(contact, "MessageInput").Text = ""; Call(contact, "Send_Click", contact, ClickArgs()); Require(contact.StatusMessage.Contains("메시지를 입력"), "Empty message accepted."); Control<ComboBox>(contact, "SendChannel").SelectedIndex = 1; Control<TextBox>(contact, "MessageInput").Text = new string('x', 201); Call(contact, "Send_Click", contact, ClickArgs()); Require(contact.StatusMessage.Contains("200자"), "Oversize Kakao text accepted."); });
            } finally { contact.Close(); }
        }

        private static void PetAudit()
        {
            var data = new AppData(); data.MiniExtraCharacters.Add(new MiniCharacterSlot { Manifest = "DefaultPets/mochi-blue/pet.json" });
            int saves = 0; var mini = new MiniWindow(data, () => { saves++; return true; }, () => { }); var host = (IPetSettingsHost)mini;
            var pet = new PetSettingsWindow(host, 1, embedded: true);
            try {
                Run("AUDIT 캐릭터/선택한 캐릭터만 크기 변경", () => { Control<Slider>(pet, "ScaleSlider").Value = 170; Require(host.PetScale(1) == 170 && host.PetScale(0) == 100, "Scale changed the wrong pet."); });
                Run("AUDIT 캐릭터/좌우 반전", () => { Control<CheckBox>(pet, "FlipSwitch").IsChecked = true; Call(pet, "Flip_Click", pet, ClickArgs()); Require(host.PetFlipped(1) && !host.PetFlipped(0), "Flip changed the wrong pet."); });
                Run("AUDIT 캐릭터/동작 선택", () => { foreach (var option in host.PetAnimationOptions(1)) { Call(pet, "Action_Click", new Button { Tag = option.Key }, ClickArgs()); Require(host.PetAnimation(1) == option.Key, "Animation selection failed: " + option.Key); } });
                Run("AUDIT 캐릭터/좌우 위치 세로 위치 간격", () => { Control<ComboBox>(pet, "SideCombo").SelectedIndex = 1; Control<Slider>(pet, "VerticalSlider").Value = 25; Control<Slider>(pet, "GapSlider").Value = -20; Require(host.CharacterSide == "Right" && host.CharacterVertical == 25 && host.CharacterGap == -20, "Placement fields did not apply."); });
                Run("AUDIT 캐릭터/개별 보이기", () => { Call(pet, "PetShown_Click", new CheckBox { Tag = 1, IsChecked = false }, ClickArgs()); Require(!host.PetVisible(1) && host.PetVisible(0), "Per-pet visibility failed."); host.SetPetVisible(1, true); });
                Run("AUDIT 캐릭터/전체 표시 켜기 끄기", () => { foreach (bool shown in new[] { false, true }) { Control<CheckBox>(pet, "VisibleToggle").IsChecked = shown; Call(pet, "Visible_Click", pet, ClickArgs()); Require(host.CharactersVisible == shown, "Global visibility failed."); } });
                Run("AUDIT 캐릭터/초기화 확인 및 기본값", () => { Call(pet, "Reset_Click", pet, ClickArgs()); Require(host.PetScale(1) == 170, "Pet reset skipped confirmation."); Call(pet, "Reset_Click", pet, ClickArgs()); Require(host.PetScale(1) == 100 && host.PetAnimation(1) == "idle" && !host.PetFlipped(1) && host.CharacterGap == 8 && host.CharacterSide == "Left", "Pet reset failed."); });
                Run("AUDIT 캐릭터/빼기 및 즉시 저장", () => { Call(pet, "RemovePet_Click", new Button { Tag = 1 }, ClickArgs()); Require(host.PetCount == 1 && saves > 0, "Pet removal/save failed."); });
            } finally { pet.Close(); mini.Close(); }
        }

        private static void MusicAudit()
        {
            var data = new MusicSettings(); int saves = 0; var music = new MusicWindow(data, () => { saves++; return true; }, () => { }, embedded: true);
            try {
                Run("AUDIT 음악/새 목록 및 이름 변경", () => { Control<TextBox>(music, "PlaylistName").Text = "Audit playlist"; Call(music, "CreatePlaylist_Click", music, ClickArgs()); Require(music.CurrentPlaylist.Name == "Audit playlist", "Playlist create failed."); Control<TextBox>(music, "PlaylistName").Text = "Renamed"; Call(music, "RenamePlaylist_Click", music, ClickArgs()); Require(music.CurrentPlaylist.Name == "Renamed", "Playlist rename failed."); });
                Run("AUDIT 음악/파일 추가 순서 변경 삭제", () => { Call(music, "InsertFiles", new[] { SilentWave(), SilentWave() }, 0); var list = music.CurrentPlaylist.Tracks; var first = list[0]; Call(music, "MoveTrackTo", first, 1); Require(list[1] == first, "Track reorder failed."); Call(music, "RemoveTrack", first); Require(list.Count == 1 && !list.Contains(first), "Track deletion failed."); });
                Run("AUDIT 음악/음량 및 반복 랜덤 한 곡 반복", () => { music.SetVolume(.37); Require(Math.Abs(data.Volume - .37) < .001, "Volume not saved."); foreach (string name in new[] { "Repeat", "Shuffle", "RepeatOne" }) { Control<CheckBox>(music, name + "Toggle").IsChecked = true; Call(music, name + "_Click", music, ClickArgs()); Require((bool)typeof(MusicSettings).GetProperty(name).GetValue(data), name + " failed."); } });
                Run("AUDIT 음악/재생 옵션 초기화 확인 및 목록 보존", () => { int count = data.Playlists.Count; Call(music, "PlayOptionsReset_Click", music, ClickArgs()); Require(Math.Abs(data.Volume - .37) < .001, "Music reset skipped confirmation."); Call(music, "PlayOptionsReset_Click", music, ClickArgs()); var defaults = new MusicSettings(); Require(data.Volume == defaults.Volume && data.Repeat == defaults.Repeat && data.Shuffle == defaults.Shuffle && data.RepeatOne == defaults.RepeatOne && data.Playlists.Count == count && saves > 0, "Music reset removed playlists or failed defaults."); });
                Run("AUDIT 음악/로컬 재생 일시정지 다음 이전 정지 (무음 파일)", () => {
                    music.SetVolume(0); Call(music, "InsertFiles", new[] { SilentWave() }, music.CurrentPlaylist.Tracks.Count);
                    var first = music.CurrentPlaylist.Tracks[0]; var second = music.CurrentPlaylist.Tracks[1];
                    music.PlayFromListAsync(first).GetAwaiter().GetResult(); Require(music.IsPlaying, "Local playback did not start.");
                    ((System.Threading.Tasks.Task)music.GetType().GetMethod("TogglePlaybackAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(music, null)).GetAwaiter().GetResult(); Require(!music.IsPlaying, "Pause failed.");
                    ((System.Threading.Tasks.Task)music.GetType().GetMethod("TogglePlaybackAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(music, null)).GetAwaiter().GetResult(); Require(music.IsPlaying, "Resume failed.");
                    ((System.Threading.Tasks.Task)music.GetType().GetMethod("AdvanceAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(music, new object[] { 1, false })).GetAwaiter().GetResult(); Require(music.PlayingTrack == second, "Next failed.");
                    ((System.Threading.Tasks.Task)music.GetType().GetMethod("AdvanceAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(music, new object[] { -1, false })).GetAwaiter().GetResult(); Require(music.PlayingTrack == first, "Previous failed.");
                    Call(music, "StopPlayback"); Require(!music.IsPlaying, "Stop failed.");
                });
            } finally { music.Close(); }
        }

    }
}
