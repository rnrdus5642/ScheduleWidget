using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ScheduleWidget
{
    public enum SettingsPage { General, Appearance, Characters, Music, Notifications, Connections, About }

    public partial class SettingsWindow : Window
    {
        private readonly Dictionary<SettingsPage, FrameworkElement> content = new Dictionary<SettingsPage, FrameworkElement>();
        private bool resetArmed;
        private readonly DispatcherTimer dismissTimer;
        private readonly AuxiliaryWindowPlacement placement;
        private bool settingsClosed;
        // Allows activation checks without displaying windows or changing the user's foreground app.
        private Func<bool> canDismissOverride = null;
        public event Action<SettingsPage> PageChanged;
        public event Action SaveRequested;
        public event Action RetrySaveRequested;
        public event Action CloseRequested;
        public event Action<SettingsPage> ResetRequested;

        public SettingsWindow()
        {
            InitializeComponent();
            ChromelessWindow.Apply(this, addCloseButton: false);
            placement = new AuxiliaryWindowPlacement(this);
            dismissTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
            dismissTimer.Tick += RecheckDismissal;
            Activated += Settings_Activated;
            Deactivated += Settings_Deactivated;
            Closed += (s, e) =>
            {
                settingsClosed = true;
                dismissTimer.Stop();
                dismissTimer.Tick -= RecheckDismissal;
            };
            UpdatePage();
        }

        private void Settings_Activated(object sender, EventArgs e) => dismissTimer.Stop();

        private void Settings_Deactivated(object sender, EventArgs e)
        {
            if (!settingsClosed) dismissTimer.Start();
        }

        private void RecheckDismissal(object sender, EventArgs e)
        {
            if (settingsClosed || !dismissTimer.IsEnabled) return;
            // Deactivation can arrive before Windows has finished activating a popup or an owned dialog.
            // Only check while inactive; keep checking if a modal picker temporarily disables settings.
            bool dismiss = canDismissOverride?.Invoke() ?? (!IsActive && IsVisible && ShouldDismissForForeground(
                new WindowInteropHelper(this).Handle, NativeMethods.GetForegroundWindow()));
            if (!dismiss) return;
            dismissTimer.Stop();
            CloseRequested?.Invoke();
        }

        internal static bool ShouldDismissForForeground(IntPtr settings, IntPtr foreground)
        {
            // Never tear down an owner inside a modal file/color/confirmation dialog's nested message loop.
            // A zero foreground handle also occurs transiently during activation, locking, and remote switches.
            return settings != IntPtr.Zero && NativeMethods.IsWindow(settings) && NativeMethods.IsWindowEnabled(settings) &&
                foreground != IntPtr.Zero && NativeMethods.IsWindow(foreground) && !NativeMethods.IsWindowOrOwnedBy(foreground, settings);
        }

        public SettingsPage SelectedPage => SettingsNavigation.SelectedItem is ListBoxItem item &&
            Enum.TryParse(item.Tag as string, out SettingsPage page) ? page : SettingsPage.General;

        public void SelectPage(SettingsPage page)
        {
            foreach (ListBoxItem item in SettingsNavigation.Items)
                if ((string)item.Tag == page.ToString()) { SettingsNavigation.SelectedItem = item; return; }
        }

        public void SetPage(SettingsPage page, FrameworkElement element)
        {
            if (content.TryGetValue(page, out FrameworkElement previous)) Pages.Children.Remove(previous);
            content[page] = element;
            Pages.Children.Add(element);
            element.Visibility = SelectedPage == page ? Visibility.Visible : Visibility.Collapsed;
        }

        public void ShowStatus(string text)
        {
            SettingsStatus.Text = text ?? "";
            SettingsStatus.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        internal void ShowPersistentWarning(string text)
        {
            PersistentSaveWarning.Text = text ?? "";
            PersistentSaveWarningPanel.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void RetrySave_Click(object sender, RoutedEventArgs e) => RetrySaveRequested?.Invoke();

        private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, SettingsNavigation) || PageTitle == null) return;
            UpdatePage();
            PageChanged?.Invoke(SelectedPage);
        }

        private void UpdatePage()
        {
            string[] descriptions = {
                "앱 실행과 창 표시, 단축키를 설정합니다.",
                "",
                "전체 표시·캐릭터 목록·전체 배치를 관리합니다. 개별 설정은 캐릭터를 더블클릭하세요.",
                "",
                "마감 알림의 시점과 받을 곳을 설정합니다.",
                "구글·텔레그램·카카오톡·휴대폰을 각각 설정합니다.",
                "현재 버전과 업데이트를 확인합니다."
            };
            PageTitle.Text = (SettingsNavigation.SelectedItem as ListBoxItem)?.Content as string ?? "일반";
            PageDescription.Text = descriptions[(int)SelectedPage];
            PageDescription.Visibility = string.IsNullOrEmpty(PageDescription.Text) ? Visibility.Collapsed : Visibility.Visible;
            foreach (var pair in content) pair.Value.Visibility = pair.Key == SelectedPage ? Visibility.Visible : Visibility.Collapsed;
            bool immediate = SelectedPage == SettingsPage.Music || SelectedPage == SettingsPage.Characters;
            SaveHint.Text = SelectedPage == SettingsPage.Music ? "플레이리스트는 추가·수정 팝업에서 저장합니다."
                : immediate ? "캐릭터 변경은 자동 저장됩니다. 다른 페이지의 변경은 설정 저장을 눌러 주세요."
                : SelectedPage == SettingsPage.Appearance ? "미리 보고 조절한 뒤 설정 저장을 눌러 주세요."
                : SelectedPage == SettingsPage.Connections ? "연동 정보는 저장해야 적용됩니다. 구글 계정 연결과 동기화는 즉시 적용됩니다."
                : "일반·화면·알림·연동 정보는 설정 저장을 누르면 적용됩니다.";
            SaveHint.Visibility = SelectedPage == SettingsPage.Appearance ? Visibility.Collapsed : Visibility.Visible;
            SettingsResetButton.Visibility = SelectedPage == SettingsPage.General || SelectedPage == SettingsPage.Appearance ? Visibility.Visible : Visibility.Collapsed;
            resetArmed = false;
            SettingsResetButton.Content = SelectedPage == SettingsPage.Appearance ? "화면 설정 전체 초기화" : "이 페이지 초기화";
            ShowStatus(null);
        }

        private void Save_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke();
        private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (!resetArmed) { resetArmed = true; SettingsResetButton.Content = "정말 초기화"; return; }
            resetArmed = false;
            SettingsResetButton.Content = SelectedPage == SettingsPage.Appearance ? "화면 설정 전체 초기화" : "이 페이지 초기화";
            ResetRequested?.Invoke(SelectedPage);
        }
    }
}
