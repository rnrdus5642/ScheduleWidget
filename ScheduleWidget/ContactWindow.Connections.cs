using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ScheduleWidget
{
    public enum ConnectionProvider { Google, Telegram, Kakao, Phone }

    public partial class ContactWindow
    {
        private readonly Dictionary<ConnectionProvider, StackPanel> providerBodies = new Dictionary<ConnectionProvider, StackPanel>();
        private readonly Dictionary<ConnectionProvider, string> messageDrafts = new Dictionary<ConnectionProvider, string>();
        private int kakaoMessageChannel = 1;
        public ConnectionProvider SelectedConnection { get; private set; } = ConnectionProvider.Google;

        public void CreateEmbeddedViews(FrameworkElement googleSettings = null)
        {
            if (ConnectionsContent != null) return;
            var reminderViewer = (ScrollViewer)RemindersTab.Content;
            RemindersContent = (FrameworkElement)reminderViewer.Content;
            reminderViewer.Content = null;
            ConnectionTabs.Items.Remove(RemindersTab);
            ContactContent.Children.Remove(ConnectionTabs);
            ContactContent.Children.Remove(MessageSection);

            var googleTab = new TabItem { Header = "구글", Style = (Style)FindResource("AuxTabItem"),
                Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 16, 0, 0) } };
            ConnectionTabs.Items.Insert(0, googleTab);
            string[] features = {
                "구글 캘린더와 일정 양방향 동기화\n가져온 캐릭터를 Google Drive에 보관하고 다른 PC와 공유",
                "봇으로 개인·그룹·채널에 메시지 전송\n설정한 대화로 일정 마감 알림 전송",
                "나와의 채팅 또는 지정한 친구에게 메시지 전송\n나와의 채팅으로 마감 알림 · 만료된 토큰 자동 갱신(선택)",
                "Windows 휴대폰 연결·전화 앱 열기\n문자 내용을 복사해 연결 앱에서 직접 전송"
            };
            for (int i = 0; i < ConnectionTabs.Items.Count; i++)
            {
                var tab = (TabItem)ConnectionTabs.Items[i];
                var viewer = (ScrollViewer)tab.Content;
                var fields = (FrameworkElement)viewer.Content;
                viewer.Content = null;
                viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                var body = new StackPanel { Margin = new Thickness(0, 0, 8, 8) };
                var info = new StackPanel();
                info.Children.Add(new TextBlock { Text = "지원 기능", Style = (Style)FindResource("AuxSectionTitle"), Margin = new Thickness(0, 0, 0, 8) });
                info.Children.Add(new TextBlock { Text = features[i], TextWrapping = TextWrapping.Wrap, LineHeight = 22 });
                if (i == 1 || i == 2)
                    info.Children.Add(new TextBlock { Text = "알림을 보낼 시점과 사용 여부는 왼쪽 ‘알림’에서 설정합니다.", TextWrapping = TextWrapping.Wrap,
                        Style = (Style)FindResource("AuxCaption"), Margin = new Thickness(0, 8, 0, 0) });
                body.Children.Add(new Border { Child = info, Style = (Style)FindResource("AuxRoundSection"), Margin = new Thickness(0, 0, 0, 12) });
                if (i == 0) { if (googleSettings != null) body.Children.Add(googleSettings); }
                else if (fields != null)
                    body.Children.Add(new Border { Child = fields, Style = (Style)FindResource("AuxRoundSection") });
                viewer.Content = body;
                tab.Tag = (ConnectionProvider)i;
                AutomationProperties.SetAutomationId(tab, "Connection" + (ConnectionProvider)i);
                providerBodies[(ConnectionProvider)i] = body;
            }
            MessageSection.Margin = new Thickness(0, 16, 0, 8);
            ConnectionsContent = ConnectionTabs;
            ConnectionTabs.Margin = new Thickness(0);
            foreach (var view in new[] { ConnectionsContent, RemindersContent })
            {
                view.Resources.MergedDictionaries.Add(Resources);
                view.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((s, e) => HasPendingChanges = true));
                view.AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler((s, e) => HasPendingChanges = true));
                view.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((s, e) => HasPendingChanges = true));
                view.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((s, e) => HasPendingChanges = true));
            }
            ConnectionTabs.SelectionChanged += ProviderChanged;
            ConnectionTabs.SelectedIndex = 0;
            ApplyConnection(ConnectionProvider.Google);
        }

        public void SelectConnection(ConnectionProvider provider)
        {
            if (ConnectionsContent == null) return;
            ConnectionTabs.SelectedIndex = (int)provider;
        }

        private void ProviderChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, ConnectionTabs) || !(ConnectionTabs.SelectedItem is TabItem tab) || !(tab.Tag is ConnectionProvider provider)) return;
            ApplyConnection(provider);
        }

        private void ApplyConnection(ConnectionProvider provider)
        {
            if (MessageSection.Parent is Panel previous)
            {
                messageDrafts[SelectedConnection] = MessageInput.Text;
                if (SelectedConnection == ConnectionProvider.Kakao) kakaoMessageChannel = SendChannel.SelectedIndex == 2 ? 2 : 1;
                previous.Children.Remove(MessageSection);
            }
            SelectedConnection = provider;
            if (provider == ConnectionProvider.Google) return;
            MessageInput.Text = messageDrafts.TryGetValue(provider, out string draft) ? draft : "";
            string label = provider == ConnectionProvider.Telegram ? "텔레그램" : provider == ConnectionProvider.Kakao ? "카카오톡" : "휴대폰 문자";
            MessageComposer.Header = label + " 메시지 작성";
            int channel = provider == ConnectionProvider.Telegram ? 0 : provider == ConnectionProvider.Kakao ? kakaoMessageChannel : 3;
            SendChannel.SelectedIndex = channel;
            foreach (var item in SendChannel.Items.Cast<ComboBoxItem>().Select((value, index) => new { value, index }))
                item.value.Visibility = provider == ConnectionProvider.Kakao ? (item.index == 1 || item.index == 2 ? Visibility.Visible : Visibility.Collapsed)
                    : item.index == channel ? Visibility.Visible : Visibility.Collapsed;
            SendChannel.IsEnabled = provider == ConnectionProvider.Kakao;
            providerBodies[provider].Children.Add(MessageSection);
        }
    }
}
