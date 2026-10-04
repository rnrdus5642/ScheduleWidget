using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace ScheduleWidget
{
    public partial class ContactWindow : Window
    {
        private readonly AppData data;
        private readonly CommunicationService service;
        private readonly Func<bool> save;
        private string loadedTelegram, loadedKakao, loadedRefresh, loadedSecret;
        private bool sending;

        public ContactWindow(AppData data, CommunicationService service, Func<bool> save, bool embedded = false)
        {
            this.data = data;
            this.service = service;
            this.save = save;
            InitializeComponent();
            if (!embedded) ChromelessWindow.Apply(this);
            else { AuxTheme.ApplyTo(this); ShowInTaskbar = false; }
            var c = data.Communication;
            TelegramToken.Password = loadedTelegram = LoadSecret(c.ProtectedTelegramToken);
            KakaoToken.Password = loadedKakao = LoadSecret(c.ProtectedKakaoToken);
            KakaoRefresh.Password = loadedRefresh = LoadSecret(c.ProtectedKakaoRefreshToken);
            KakaoSecret.Password = loadedSecret = LoadSecret(c.ProtectedKakaoClientSecret);
            TelegramChat.Text = c.TelegramChatId;
            KakaoLink.Text = c.KakaoLinkUrl;
            KakaoFriend.Text = c.KakaoFriendUuid;
            KakaoAppKey.Text = c.KakaoAppKey;
            PhoneNumber.Text = c.PhoneNumber;
            var r = data.Reminders;
            RemindersEnabled.IsChecked = r.Enabled;
            DesktopReminder.IsChecked = r.Desktop;
            TelegramReminder.IsChecked = r.Telegram;
            KakaoReminder.IsChecked = r.Kakao;
            DaysBefore.Text = r.DaysBefore.ToString();
            Hour.Text = r.Hour.ToString("00");
            Minute.Text = r.Minute.ToString("00");
            ContactContent.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((s, e) => HasPendingChanges = true));
            ContactContent.AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler((s, e) => HasPendingChanges = true));
            ContactContent.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((s, e) => HasPendingChanges = true));
            ContactContent.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((s, e) => HasPendingChanges = true));
        }

        public bool HasPendingChanges { get; private set; }
        public string StatusMessage => StatusText.Text;
        public event Action<string> StatusChanged;
        public FrameworkElement ConnectionsContent { get; private set; }
        public FrameworkElement RemindersContent { get; private set; }

        public void CreateEmbeddedViews()
        {
            if (ConnectionsContent != null) return;
            var connections = new StackPanel();
            foreach (TabItem tab in ConnectionTabs.Items)
            {
                var viewer = tab.Content as ScrollViewer;
                var body = viewer?.Content as FrameworkElement;
                if (body == null) continue;
                viewer.Content = null;
                if (ReferenceEquals(tab, RemindersTab)) { RemindersContent = body; continue; }
                var section = new Expander { Header = tab.Header, Content = body, FontSize = 15, Padding = new Thickness(2) };
                section.SetResourceReference(ForegroundProperty, "AuxInkBrush");
                body.Margin = new Thickness(0, 16, 0, 0);
                var card = new Border { Child = section, Margin = new Thickness(0, 0, 0, 12), Style = (Style)FindResource("AuxRoundSection") };
                connections.Children.Add(card);
            }
            ContactContent.Children.Remove(MessageSection);
            MessageSection.Margin = new Thickness(0, 8, 0, 0);
            connections.Children.Add(MessageSection);
            ConnectionsContent = connections;
            foreach (var view in new[] { ConnectionsContent, RemindersContent })
            {
                view.Resources.MergedDictionaries.Add(Resources);
                view.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((s, e) => HasPendingChanges = true));
                view.AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler((s, e) => HasPendingChanges = true));
                view.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((s, e) => HasPendingChanges = true));
                view.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((s, e) => HasPendingChanges = true));
            }
        }

        private string LoadSecret(string encrypted)
        {
            try { return SecretStore.Unprotect(encrypted); }
            catch (InvalidOperationException ex) { ReportStatus(ex.Message); return ""; }
        }
        public void SetMessage(string message)
        {
            MessageInput.Text = message;
            MessageComposer.IsExpanded = true;
        }
        public void ReportStatus(string message) { StatusText.Text = message; StatusChanged?.Invoke(message); }
        private string UpdatedSecret(PasswordBox box, string loaded, string current) =>
            box.Password == loaded ? current : SecretStore.Protect(box.Password);

        public bool SaveSettings()
        {
            try
            {
                if (!int.TryParse(DaysBefore.Text, out int days) || days < 0 || days > 30 ||
                    !int.TryParse(Hour.Text, out int hour) || hour < 0 || hour > 23 ||
                    !int.TryParse(Minute.Text, out int minute) || minute < 0 || minute > 59)
                    throw new InvalidOperationException("알림은 0~30일 전, 시각은 00:00~23:59로 입력해 주세요.");
                var old = data.Communication;
                var current = new CommunicationSettings
                {
                    ProtectedTelegramToken = UpdatedSecret(TelegramToken, loadedTelegram, old.ProtectedTelegramToken),
                    TelegramChatId = TelegramChat.Text.Trim(),
                    ProtectedKakaoToken = UpdatedSecret(KakaoToken, loadedKakao, old.ProtectedKakaoToken),
                    ProtectedKakaoRefreshToken = UpdatedSecret(KakaoRefresh, loadedRefresh, old.ProtectedKakaoRefreshToken),
                    ProtectedKakaoClientSecret = UpdatedSecret(KakaoSecret, loadedSecret, old.ProtectedKakaoClientSecret),
                    KakaoAppKey = KakaoAppKey.Text.Trim(), KakaoLinkUrl = KakaoLink.Text.Trim(),
                    KakaoFriendUuid = KakaoFriend.Text.Trim(), PhoneNumber = PhoneNumber.Text.Trim()
                };
                var reminders = new ReminderSettings
                {
                    Enabled = RemindersEnabled.IsChecked == true, DaysBefore = days, Hour = hour, Minute = minute,
                    Desktop = DesktopReminder.IsChecked == true, Telegram = TelegramReminder.IsChecked == true, Kakao = KakaoReminder.IsChecked == true
                };
                if (reminders.Enabled && !reminders.Desktop && !reminders.Telegram && !reminders.Kakao)
                    throw new InvalidOperationException("알림을 받을 곳을 하나 이상 선택해 주세요.");
                if (reminders.Enabled && reminders.Telegram &&
                    (string.IsNullOrWhiteSpace(current.ProtectedTelegramToken) || string.IsNullOrWhiteSpace(current.TelegramChatId)))
                    throw new InvalidOperationException("텔레그램 토큰과 Chat ID를 먼저 입력해 주세요.");
                if (reminders.Enabled && reminders.Kakao &&
                    (string.IsNullOrWhiteSpace(current.ProtectedKakaoToken) || !Uri.TryCreate(current.KakaoLinkUrl, UriKind.Absolute, out Uri link) || link.Scheme != "https"))
                    throw new InvalidOperationException("카카오 토큰과 등록한 HTTPS 웹 링크를 먼저 입력해 주세요.");
                var oldReminders = data.Reminders;
                data.Communication = current;
                data.Reminders = reminders;
                if (!save())
                {
                    data.Communication = old;
                    data.Reminders = oldReminders;
                    ReportStatus("설정을 저장하지 못했습니다. 저장 경로를 확인해 주세요.");
                    return false;
                }
                loadedTelegram = TelegramToken.Password;
                loadedKakao = KakaoToken.Password;
                loadedRefresh = KakaoRefresh.Password;
                loadedSecret = KakaoSecret.Password;
                HasPendingChanges = false;
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is CryptographicException)
            { ReportStatus(ex.Message); return false; }
        }

        // 알림 설정 초기화 → 정말 초기화 → the reminder controls go back to the defaults (saved with 설정 저장).
        // Credentials and chat targets are never touched.
        private bool reminderResetArmed;

        private void ReminderReset_Click(object sender, RoutedEventArgs e)
        {
            if (!reminderResetArmed)
            {
                reminderResetArmed = true;
                ReminderResetButton.Content = "정말 초기화";
                return;
            }
            reminderResetArmed = false;
            ReminderResetButton.Content = "알림 설정 초기화";
            var defaults = new ReminderSettings();
            RemindersEnabled.IsChecked = defaults.Enabled;
            DesktopReminder.IsChecked = defaults.Desktop;
            TelegramReminder.IsChecked = defaults.Telegram;
            KakaoReminder.IsChecked = defaults.Kakao;
            DaysBefore.Text = defaults.DaysBefore.ToString();
            Hour.Text = defaults.Hour.ToString("00");
            Minute.Text = defaults.Minute.ToString("00");
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (SaveSettings()) ReportStatus("설정을 저장했습니다.");
        }
        private void ChannelChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SendButton != null) SendButton.Content = SendChannel.SelectedIndex == 3 ? "문자 준비" : "보내기";
        }
        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            if (sending) return;
            string message = MessageInput.Text.Trim();
            if (message.Length == 0) { ReportStatus("메시지를 입력해 주세요."); return; }
            int channel = SendChannel.SelectedIndex;
            if ((channel == 1 || channel == 2) && message.Length > 200)
            { ReportStatus("카카오톡 메시지는 200자 이하로 입력해 주세요."); return; }
            if (channel == 2 && string.IsNullOrWhiteSpace(KakaoFriend.Text))
            { ReportStatus("받는 친구의 UUID를 입력해 주세요."); return; }
            if (!SaveSettings()) return;
            sending = true;
            SendButton.IsEnabled = false;
            ReportStatus("처리 중입니다…");
            try
            {
                if (channel == 0) await service.SendTelegramAsync(data.Communication, message);
                else if (channel == 1 || channel == 2) await service.SendKakaoAsync(data.Communication, message, channel == 1);
                else
                {
                    Clipboard.SetText(message);
                    OpenExternal("ms-phone:");
                    ReportStatus("메시지를 복사했습니다. 휴대폰 연결 앱에서 " + PhoneNumber.Text + " 수신자를 선택하고 붙여넣어 전송하세요.");
                    return;
                }
                ReportStatus("메시지를 전송했습니다.");
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is UnauthorizedAccessException ||
                ex is CryptographicException || ex is Win32Exception || ex is System.Runtime.InteropServices.ExternalException)
            { ReportStatus(ex.Message); }
            finally
            {
                save();
                sending = false;
                SendButton.IsEnabled = true;
            }
        }

        private static void OpenExternal(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        private void PhoneLink_Click(object sender, RoutedEventArgs e) => TryOpen("ms-phone:");
        private void Help_Click(object sender, RoutedEventArgs e) => TryOpen((string)((Button)sender).Tag);
        private void TryOpen(string uri)
        {
            try { OpenExternal(uri); }
            catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
            { ReportStatus("연결 앱을 열 수 없습니다. Windows의 기본 앱과 ‘휴대폰과 연결’ 설치 상태를 확인해 주세요."); }
        }
        private void Call_Click(object sender, RoutedEventArgs e)
        {
            string phone = Regex.Replace(PhoneNumber.Text, @"[\s()\-]", "");
            if (!Regex.IsMatch(phone, @"\A\+?[0-9]{7,15}\z"))
            { ReportStatus("국가번호를 포함해 7~15자리 전화번호를 입력해 주세요."); return; }
            TryOpen("tel:" + phone);
        }
        protected override void OnClosing(CancelEventArgs e)
        {
            if (sending) { e.Cancel = true; ReportStatus("전송 결과를 확인한 뒤 닫을 수 있습니다."); }
            base.OnClosing(e);
        }
    }
}
