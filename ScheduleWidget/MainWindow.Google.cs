using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ScheduleWidget
{
    // 구글 연동 in 설정: 구글 로그인 / 로그아웃 first, then two ON/OFF switches that work only while signed in —
    // 구글 캘린더 연동 (schedules) and 캐릭터도 구글 드라이브에 보관 (imported characters). Syncs run on start, every
    // 5 minutes, and a few seconds after the schedules or characters change (an edit saved while a sync runs: a few seconds
    // after that sync); after a failed sync the next try waits longer (30 seconds, 2 minutes, then 5 minutes, or as long as
    // Google asks), 지금 동기화 always tries at once.
    public partial class MainWindow
    {
        private readonly GoogleCalendarService googleCalendar = new GoogleCalendarService();
        private DispatcherTimer googlePeriodicTimer, googleSoonTimer;
        private bool googleSyncing, googleSyncAgain;
        private bool googleOwnSave;       // a sync is saving what it brought: not an edit to send back
        private long? googleLastSignature;
        private string googleLastError;   // schedules / sign-in
        private string googlePetStatus;   // characters
        private CancellationTokenSource googleSignIn;
        private int googleFailures;       // failed syncs in a row
        private DateTime googleRetryAt;   // (UTC) no automatic sync before this after failures
        private int googlePetsVersion = -1; // CharacterCatalog.Version the last Drive sync accounted for

        private GoogleCalendarSettings GoogleSettings => appData?.GoogleCalendar;
        private bool GoogleConnected => !string.IsNullOrEmpty(GoogleSettings?.ProtectedRefreshToken);
        private bool GoogleCalendarOn => GoogleConnected && GoogleSettings.Enabled;
        private bool GooglePetsOn => GoogleConnected && GoogleSettings.PetsEnabled;
        private bool GoogleActive => GoogleCalendarOn || GooglePetsOn;

        private void StartGoogleCalendar()
        {
            googlePeriodicTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            googlePeriodicTimer.Tick += (s, e) => RequestGoogleSync(now: true);
            googleSoonTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            googleSoonTimer.Tick += (s, e) => { googleSoonTimer.Stop(); RequestGoogleSync(now: true); };
            // A character imported or deleted here → keep Drive in step soon (not the characters a Drive sync itself brought).
            Action petsChanged = () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!googleSyncing && GooglePetsOn && CharacterCatalog.Version != googlePetsVersion) RequestGoogleSync(now: false);
            }));
            CharacterCatalog.Changed += petsChanged;
            Closed += (s, e) => { googlePeriodicTimer.Stop(); googleSoonTimer.Stop(); googleSignIn?.Cancel(); CharacterCatalog.Changed -= petsChanged; };
            UpdateGoogleTimers();
            // Characters just brought over from an old app-folder Pet are backed up at once (캐릭터도 구글 드라이브에 보관).
            if (GoogleActive) RequestGoogleSync(now: CharacterCatalog.LastMigrated > 0 && GooglePetsOn);
        }

        private void UpdateGoogleTimers()
        {
            if (googlePeriodicTimer == null) return;
            if (GoogleActive) { if (!googlePeriodicTimer.IsEnabled) googlePeriodicTimer.Start(); }
            else { googlePeriodicTimer.Stop(); googleSoonTimer.Stop(); }
        }

        // Every save passes here; only a real change to the schedules (not a window move) starts a sync soon. One saved while a
        // sync runs (not that sync's own save) goes up right after it — that sync may have read the list before the edit —
        // instead of waiting for the 5-minute timer.
        private void NoteSchedulesSaved()
        {
            if (!GoogleCalendarOn || googleOwnSave) return;
            if (ScheduleSignature() == googleLastSignature) return;
            if (googleSyncing) { googleSyncAgain = true; return; }
            RequestGoogleSync(now: false);
        }

        // What a sync brought (and the links it made) is saved as the sync's own save: nothing to send back.
        private void SaveGoogleSyncResult()
        {
            googleOwnSave = true;
            try { SaveDataSafely(false); }
            finally { googleOwnSave = false; }
        }

        // A cheap fingerprint of what a sync sends (no big string on every save), to tell a real edit from a window move.
        private long ScheduleSignature()
        {
            unchecked
            {
                long hash = appData.Schedules.Count;
                foreach (var s in appData.Schedules)
                {
                    hash = hash * 1000003 + s.Id.GetHashCode();
                    hash = hash * 1000003 + (s.GoogleEventId?.GetHashCode() ?? 0);
                    hash = hash * 1000003 + (s.Title?.GetHashCode() ?? 0);
                    hash = hash * 1000003 + (s.Period?.GetHashCode() ?? 0);
                    hash = hash * 1000003 + (s.EndPeriod?.GetHashCode() ?? 0);
                    hash = hash * 1000003 + (s.Time?.GetHashCode() ?? 0);
                    hash = hash * 1000003 + (s.IsCompleted ? 1 : 2);
                }
                return hash;
            }
        }

        // now: at once (else in a few seconds). After failed syncs nothing starts before the pause is over.
        private void RequestGoogleSync(bool now)
        {
            if (!GoogleActive || googleSoonTimer == null) return;
            TimeSpan wait = googleRetryAt - DateTime.UtcNow;
            if (now && wait <= TimeSpan.FromSeconds(1)) { RunGoogleSync(); return; }
            googleSoonTimer.Stop();
            googleSoonTimer.Interval = wait > TimeSpan.FromSeconds(4) ? wait : TimeSpan.FromSeconds(4);
            googleSoonTimer.Start();
        }

        private async void RunGoogleSync()
        {
            if (!GoogleActive) return;
            if (googleSyncing) { googleSyncAgain = true; return; }
            googleSyncing = true;
            UpdateGoogleUi();
            bool failed = false, more = false;
            TimeSpan? googleWait = null;
            try
            {
                if (GoogleCalendarOn)
                {
                    try
                    {
                        // What the list looks like with this sync's own changes (a new event's id, Google's answer): a save
                        // while it runs (a window move) is compared with that, so only a real edit starts another sync.
                        Action ownChanges = () => googleLastSignature = ScheduleSignature();
                        ownChanges();
                        GoogleSyncResult result = await googleCalendar.SyncAsync(GoogleSettings, appData.Schedules, ownChanges);
                        googleLastError = null;
                        more = result.More;
                        // Saved and redrawn only when something changed (a sync every 5 minutes mostly finds nothing new).
                        if (result.DataChanged) SaveGoogleSyncResult();
                        if (result.ListChanged) RefreshScheduleList();
                    }
                    catch (GoogleSignInRequiredException) { throw; }
                    catch (GoogleRateLimitException ex) { googleLastError = ex.Message; failed = true; googleWait = ex.RetryAfter; }
                    catch (InvalidOperationException ex) { googleLastError = ex.Message; failed = true; } // offline etc.: tried again after a pause
                    finally { googleLastSignature = ScheduleSignature(); } // what this sync saw: only a later edit starts one sooner
                }
                if (GooglePetsOn) await SyncPetsWithDrive();
            }
            catch (GoogleSignInRequiredException ex)
            {
                // The saved sign-in stopped working: back to signed out (switches keep their choice for the next sign-in).
                GoogleSettings.ProtectedRefreshToken = null;
                googleLastError = ex.Message;
                SaveDataSafely(false);
                UpdateGoogleTimers();
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                // Anything unexpected (an odd answer from Google, a file in use, …): show it and try again later. This
                // runs as async void on the UI thread, so letting it escape would close the whole app.
                googleLastError = "구글 동기화 중 오류: " + ex.Message;
                failed = true;
            }
            finally
            {
                googleSyncing = false;
                NoteGoogleSyncResult(failed, googleWait);
                UpdateGoogleUi();
                if (googleSyncAgain || more) { googleSyncAgain = false; RequestGoogleSync(now: false); } // more new schedules wait: next batch soon
                else if (GoogleCalendarOn && ScheduleSignature() != googleLastSignature) RequestGoogleSync(now: false); // edited during the sync
                else if (GooglePetsOn && CharacterCatalog.Version != googlePetsVersion) RequestGoogleSync(now: false); // a character changed meanwhile
            }
        }

        // Failed syncs in a row wait 30 seconds, 2 minutes, then 5 minutes before the next automatic try (longer when Google
        // asks for it); one that goes through ends the pause. Before, a failure was retried every 4 seconds.
        private void NoteGoogleSyncResult(bool failed, TimeSpan? googleWait)
        {
            if (!failed) { googleFailures = 0; googleRetryAt = DateTime.MinValue; return; }
            googleFailures++;
            TimeSpan pause = TimeSpan.FromSeconds(googleFailures == 1 ? 30 : googleFailures == 2 ? 120 : 300);
            if (googleWait.HasValue && googleWait.Value > pause) pause = googleWait.Value < TimeSpan.FromHours(1) ? googleWait.Value : TimeSpan.FromHours(1);
            googleRetryAt = DateTime.UtcNow + pause;
            RequestGoogleSync(now: false);
        }

        // 캐릭터도 구글 드라이브에 보관: its problems show on their own line and never stop the calendar.
        private async Task SyncPetsWithDrive()
        {
            int seen = CharacterCatalog.Version;
            try
            {
                GooglePetSyncResult result = await googleCalendar.SyncPetsAsync(GoogleSettings);
                // The characters this sync brought are not a change to send back; one imported or deleted by the user
                // meanwhile is (then the version differs and another sync follows).
                seen += result.Downloaded;
                googlePetStatus = PetSyncStatus(result);
                if (result.Changed) SaveGoogleSyncResult();
            }
            catch (GoogleSignInRequiredException) { throw; }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { googlePetStatus = ex.Message; } // shown on its own line; retried next sync
            finally { googlePetsVersion = seen; }
        }

        private static string PetSyncStatus(GooglePetSyncResult result)
        {
            string arrived = result.Downloaded > 0 ? "구글 드라이브에서 캐릭터 " + result.Downloaded + "개를 받았습니다." : null;
            string failed = result.Failed > 0 ? "캐릭터 " + result.Failed + "개를 드라이브와 주고받지 못했습니다: " + result.FirstError : null;
            string status = arrived != null && failed != null ? arrived + " " + failed : arrived ?? failed;
            // The same-name rule leaves a pet without a Drive copy: said, so that is never a surprise.
            return string.IsNullOrEmpty(result.Note) ? status : status == null ? result.Note : status + " " + result.Note;
        }

        private void UpdateGoogleUi()
        {
            if (GoogleCalendarSwitch == null || appData == null) return;
            var settings = GoogleSettings;
            string account = settings.Account ?? "구글 계정";

            // 구글 로그인 / 로그인 취소 / 로그아웃
            if (googleSignIn != null)
            {
                GoogleAccountText.Text = "브라우저에서 구글 로그인을 마쳐 주세요.";
                GoogleSignInButton.Content = "로그인 취소";
            }
            else if (GoogleConnected)
            {
                GoogleAccountText.Text = account + " 로그인됨";
                GoogleSignInButton.Content = "로그아웃";
            }
            else
            {
                GoogleAccountText.Text = googleLastError ?? "구글 계정으로 로그인하면 아래 연동을 켤 수 있습니다.";
                GoogleSignInButton.Content = "구글 로그인";
            }
            GoogleSignInButton.Style = (Style)GoogleSignInButton.FindResource(GoogleConnected && googleSignIn == null ? "AuxUtilityButton" : "AuxPrimaryButton");

            // The switches: OFF and locked until signed in.
            GoogleCalendarSwitch.IsEnabled = GooglePetsSwitch.IsEnabled = GoogleConnected;
            GoogleCalendarSwitch.IsChecked = GoogleCalendarOn;
            GooglePetsSwitch.IsChecked = GooglePetsOn;

            GoogleSyncNowButton.Visibility = GoogleActive ? Visibility.Visible : Visibility.Collapsed;
            GoogleSyncNowButton.IsEnabled = !googleSyncing;
            int refused = appData.Schedules.Count(GoogleCalendarSync.IsRefused); // sent again once edited here
            GoogleCalendarStatus.Text = !GoogleConnected ? "구글 로그인 후 켤 수 있습니다."
                : !settings.Enabled ? "꺼짐"
                : googleSyncing ? "동기화 중…"
                : googleLastError ?? ("연동됨" + (settings.LastSync.HasValue ? " · 마지막 동기화 " + settings.LastSync.Value.ToString("M/d HH:mm") : "") +
                    (refused > 0 ? " · 구글이 받지 않은 일정 " + refused + "개 (고치면 다시 보냅니다)" : ""));
            GooglePetsStatus.Text = !GoogleConnected ? "구글 로그인 후 켤 수 있습니다."
                : !settings.PetsEnabled ? "꺼짐"
                : googlePetStatus ?? "드라이브의 'ScheduleWidget 캐릭터' 폴더에 보관합니다. 같은 이름의 캐릭터가 이미 있으면 받지 않습니다.";
        }

        private async void GoogleSignIn_Click(object sender, RoutedEventArgs e)
        {
            var settings = GoogleSettings;
            if (settings == null) return;
            if (googleSignIn != null) { googleSignIn.Cancel(); return; } // 로그인 취소
            if (GoogleConnected) { await SignOutOfGoogle(); return; }    // 로그아웃
            if (GoogleCalendarService.LoadClient() == null && !PickGoogleClientFile()) { UpdateGoogleUi(); return; }

            googleSignIn = new CancellationTokenSource();
            googleLastError = null;
            UpdateGoogleUi();
            try
            {
                await googleCalendar.SignInAsync(settings, googleSignIn.Token);
                // Another Google account: its calendar starts from this PC's schedules. An account that could not be read (or
                // the "구글 계정" placeholder an older version saved then) is never taken for another one.
                bool Known(string account) => !string.IsNullOrWhiteSpace(account) && account != "구글 계정";
                if (Known(settings.LinkedAccount) && Known(settings.Account) &&
                    !string.Equals(settings.LinkedAccount, settings.Account, StringComparison.OrdinalIgnoreCase))
                    UnlinkSchedules();
                if (Known(settings.Account)) settings.LinkedAccount = settings.Account;
                googleSignIn = null;
                googleFailures = 0;
                googleRetryAt = DateTime.MinValue;
                SaveDataSafely(false);
                UpdateGoogleTimers();
                if (GoogleActive) RunGoogleSync(); // switches left ON from before sign-out resume at once
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) // async void: nothing may escape (socket / browser / file errors …)
            {
                bool cancelled = googleSignIn?.IsCancellationRequested == true;
                if (!cancelled && IsLoaded)
                    MessageBox.Show(Window.GetWindow(GoogleSignInButton) ?? this, "구글 로그인에 실패했습니다.\n" + ex.Message,
                        "구글 연동", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                googleSignIn = null;
                UpdateGoogleUi();
            }
        }

        // No Google OAuth client in this app yet: Google requires one per app (made once in Google Cloud Console).
        // 예 → pick the downloaded JSON (kept for this PC, then sign-in goes on); 아니요 → open the console to make one.
        private bool PickGoogleClientFile()
        {
            Window owner = Window.GetWindow(GoogleSignInButton) ?? this;
            var answer = MessageBox.Show(owner,
                "구글은 로그인 창을 여는 앱마다 'OAuth 클라이언트' 등록을 요구하는데, 이 앱에는 아직 없습니다.\n" +
                "Google Cloud Console에서 한 번만 만들면(데스크톱 앱, 무료) 그 뒤로는 로그인 버튼만 누르면 됩니다.\n\n" +
                "예: 받아 둔 클라이언트 JSON 파일 선택 후 바로 로그인\n아니요: 만드는 페이지 열기 (방법은 README '구글 캘린더 연동 준비')",
                "구글 로그인", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
            if (answer == MessageBoxResult.No)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://console.cloud.google.com/auth/clients") { UseShellExecute = true }); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) { }
                return false;
            }
            if (answer != MessageBoxResult.Yes) return false;
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "구글 OAuth 클라이언트 파일 선택", Filter = "구글 클라이언트 파일 (*.json)|*.json" };
            if (dialog.ShowDialog(owner) != true) return false;
            try
            {
                if (GoogleCalendarService.SaveClient(dialog.FileName)) return true;
                MessageBox.Show(owner, "구글 OAuth 클라이언트 파일이 아닙니다. Cloud Console에서 '데스크톱 앱' 클라이언트의 JSON을 받아 주세요.",
                    "구글 로그인", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                MessageBox.Show(owner, "파일을 저장하지 못했습니다.\n" + ex.Message, "구글 로그인", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }

        private async Task SignOutOfGoogle()
        {
            var answer = MessageBox.Show(Window.GetWindow(GoogleSignInButton) ?? this,
                "구글에서 로그아웃할까요?\n앱·구글 캘린더·드라이브의 일정과 캐릭터는 그대로 남습니다.", "구글 연동",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
            await googleCalendar.SignOutAsync(GoogleSettings);
            // The schedules keep their Google links: signing in to the same account again carries on where it stopped.
            googleLastError = googlePetStatus = null;
            SaveDataSafely(false);
            UpdateGoogleTimers();
            UpdateGoogleUi();
        }

        private void GoogleCalendarSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (GoogleSettings == null) return;
            if (!GoogleConnected) { UpdateGoogleUi(); return; }
            GoogleSettings.Enabled = GoogleCalendarSwitch.IsChecked == true;
            googleLastError = null;
            googleRetryAt = DateTime.MinValue; // turned on: try at once
            SaveDataSafely(false);
            UpdateGoogleTimers();
            if (GoogleSettings.Enabled) RequestGoogleSync(now: true);
            UpdateGoogleUi();
        }

        private void GooglePetsSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (GoogleSettings == null) return;
            if (!GoogleConnected) { UpdateGoogleUi(); return; }
            GoogleSettings.PetsEnabled = GooglePetsSwitch.IsChecked == true;
            googlePetStatus = null;
            googleRetryAt = DateTime.MinValue;
            SaveDataSafely(false);
            UpdateGoogleTimers();
            if (GoogleSettings.PetsEnabled) RequestGoogleSync(now: true);
            UpdateGoogleUi();
        }

        // 지금 동기화: at once, even while an automatic retry is still waiting.
        private void GoogleSyncNow_Click(object sender, RoutedEventArgs e)
        {
            googleRetryAt = DateTime.MinValue;
            RequestGoogleSync(now: true);
        }

        private void UnlinkSchedules()
        {
            foreach (var item in appData.Schedules)
            {
                item.GoogleEventId = null;
                item.GoogleSyncedHash = null;
                item.GoogleSyncedPeriod = null;
                item.GoogleEndMinutes = null;
                item.GoogleRefusedHash = null;
            }
            GoogleSettings.SyncedEventIds.Clear();
            GoogleSettings.OwnedEventIds?.Clear();
            GoogleSettings.HiddenEvents?.Clear();
            GoogleSettings.OwnershipVersion = 1; // nothing linked by an older version is left to sort out
            GoogleSettings.ReadStartedUtc = null; // the other account's calendar is read in full
            GoogleSettings.SyncedPetIds.Clear();
            GoogleSettings.FailedPetFiles?.Clear();
            GoogleSettings.PetDeletionsSorted = true; // no synced characters left to sort out either
        }
    }
}
