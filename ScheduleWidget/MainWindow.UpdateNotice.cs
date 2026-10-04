using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ScheduleWidget
{
    // 업데이트 알림: with a repository set in code (UpdateClient.IsConfigured), the app looks for a newer version 30 s after
    // it starts and every 6 h after that — quietly (no dialogs; failures only go to the error log). A newer version whose
    // signature was verified shows a speech bubble ("최신 업데이트 발견!") over the first shown pet of the mini window, else
    // over the TODO window's pets, else over the mini window calendar's top band (else the TODO window's top); with nothing
    // on the screen it waits for a window to show. 업데이트 runs the same flow as 설정 › 업데이트 (RunUpdateAsync). ✕ hides it
    // until the next start (or a newer version); 하루 동안 안 보기 hides it for 24 h (AppData.UpdateNoticeSnoozedUntil).
    // With the repository blank nothing is scheduled and nothing is ever asked.
    public partial class MainWindow
    {
        internal static TimeSpan UpdateNoticeFirstDelay = TimeSpan.FromSeconds(30);
        internal static TimeSpan UpdateNoticeInterval = TimeSpan.FromHours(1); // a version put on GitHub shows within the hour
        internal static readonly TimeSpan UpdateNoticeSnooze = TimeSpan.FromHours(24);
        private static readonly TimeSpan UpdateNoticeFollowInterval = TimeSpan.FromMilliseconds(250);

        private DispatcherTimer updateNoticeTimer;       // the background check (null: none scheduled)
        private DispatcherTimer updateNoticeFollowTimer; // keeps the bubble at its pet while an update waits to be shown
        private UpdateNoticeWindow updateNotice;
        private UpdateCheckResult updateNoticeFound;     // the newer, signed version the last check found
        private Version updateNoticeClosedFor;           // ✕ pressed for this version (this session only)
        private bool updateNoticeChecking, updateFromNotice;
        private readonly HashSet<Window> updateNoticeWatched = new HashSet<Window>();

        /// <summary>Called once the app has loaded: schedules the background check — only with a repository set.</summary>
        private void StartUpdateNoticeChecks()
        {
            if (updateNoticeTimer != null || closingApp || !UpdateClient.IsConfigured) return;
            updateNoticeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = UpdateNoticeFirstDelay };
            updateNoticeTimer.Tick += async (s, e) =>
            {
                updateNoticeTimer.Interval = UpdateNoticeInterval;
                await CheckForUpdateNoticeAsync();
            };
            updateNoticeTimer.Start();
        }

        private void StopUpdateNotice()
        {
            updateNoticeTimer?.Stop();
            updateNoticeTimer = null;
            updateNoticeFollowTimer?.Stop();
            CloseUpdateNoticeWindow();
        }

        /// <summary>One quiet check. Never throws and never asks anything: a failure leaves things as they were.</summary>
        internal async Task CheckForUpdateNoticeAsync()
        {
            if (updateNoticeChecking || updateBusy || closingApp || !UpdateClient.IsConfigured) return;
            updateNoticeChecking = true;
            if (updateNoticeFound == null) SetUpdateStatus("업데이트를 확인하는 중…");
            try
            {
                var result = await updateClient.CheckAsync();
                updateNoticeFound = result.IsNewer && result.SignatureVerified ? result : null;
                // 설정 › 업데이트 follows what was found: the button is on only for a newer, signed version.
                SetUpdateStatus(updateNoticeFound != null ? "새 버전 " + UpdateInfo.Format(result.Version) + "이 있습니다."
                    : "최신 버전입니다 (현재 " + UpdateInfo.Format(result.Current) + ").");
            }
            catch (Exception ex)
            {
                ErrorLog.Write("update-notice", ex);
                // Not reachable: what was found before still stands. Any other answer (no release yet, a badly signed or
                // broken release) is no update to offer.
                bool offline = ex is InvalidOperationException && ex.Message == UpdateClient.NetworkErrorMessage;
                if (!offline) updateNoticeFound = null;
                if (updateNoticeFound == null) SetUpdateStatus(offline ? "업데이트 서버에 연결할 수 없습니다." : "받을 수 있는 업데이트가 없습니다.");
            }
            finally { updateNoticeChecking = false; UpdateUpdateButton(); }
            RefreshUpdateNotice();
        }

        private bool UpdateNoticeWanted =>
            updateNoticeFound != null && !closingApp && appData != null &&
            (updateNoticeClosedFor == null || updateNoticeFound.Version > updateNoticeClosedFor) &&
            !(appData.UpdateNoticeSnoozedUntil.HasValue && appData.UpdateNoticeSnoozedUntil.Value > DateTime.Now);

        /// <summary>Shows, moves or hides the bubble for what is known now.</summary>
        internal void RefreshUpdateNotice()
        {
            if (!UpdateNoticeWanted && !(updateFromNotice && updateBusy))
            {
                updateNoticeFollowTimer?.Stop();
                CloseUpdateNoticeWindow();
                return;
            }
            if (updateNoticeFollowTimer == null)
            {
                updateNoticeFollowTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = UpdateNoticeFollowInterval };
                updateNoticeFollowTimer.Tick += (s, e) => RefreshUpdateNotice();
            }
            if (!updateNoticeFollowTimer.IsEnabled) updateNoticeFollowTimer.Start();
            PlaceUpdateNotice();
        }

        // Where the bubble points: the first shown pet of the mini window, the TODO window's pets, the mini window calendar's
        // top band, the TODO window's top. Null: nothing on the screen (or the window it would point at is busy).
        private Window FindUpdateNoticeAnchor(out Rect anchor, out Rect workArea, out double tipY)
        {
            anchor = Rect.Empty; workArea = Rect.Empty; tipY = 0;
            bool Shown(Window w) => w != null && w.IsLoaded && w.IsVisible && w.WindowState != WindowState.Minimized;
            var mini = miniWindow;
            var pets = petCompanion;
            bool miniShown = Shown(mini), petsShown = Shown(pets) && IsVisible && !_startingHidden;
            MiniWindow owner = null;
            if (miniShown && mini.TryGetUpdateNoticePet(out anchor)) owner = mini;
            else if (petsShown && pets.TryGetUpdateNoticePet(out anchor)) owner = pets;
            if (owner != null)
            {
                if (owner.UpdateNoticeBusy) return null;
                workArea = owner.UpdateNoticeWorkArea;
                tipY = anchor.Top + anchor.Height * 0.1; // the sprite cell's empty top (the hit box leaves it out too)
                anchor = new Rect(anchor.X, tipY, anchor.Width, Math.Max(1, anchor.Bottom - tipY));
                return owner;
            }
            if (miniShown && mini.TryGetUpdateNoticeCalendar(out anchor))
            {
                if (mini.UpdateNoticeBusy) return null;
                workArea = mini.UpdateNoticeWorkArea;
                tipY = anchor.Top;
                return mini;
            }
            if (Shown(this) && !_startingHidden && Opacity > 0)
            {
                anchor = new Rect(Left, Top, ActualWidth, Math.Min(56, ActualHeight));
                workArea = SystemParameters.WorkArea;
                tipY = anchor.Top;
                return this;
            }
            return null;
        }

        private void PlaceUpdateNotice()
        {
            Window owner = FindUpdateNoticeAnchor(out Rect anchor, out Rect workArea, out double tipY);
            if (owner == null)
            {
                if (updateNotice != null && updateNotice.IsVisible) updateNotice.Hide();
                return;
            }
            WatchUpdateNoticeAnchor(owner);
            if (updateNotice == null)
            {
                var bubble = new UpdateNoticeWindow();
                bubble.UpdateRequested += async () => await RunUpdateFromNoticeAsync();
                bubble.SnoozeRequested += SnoozeUpdateNotice;
                bubble.CloseRequested += CloseUpdateNoticeForSession;
                bubble.Closed += (s, e) => { if (ReferenceEquals(updateNotice, s)) updateNotice = null; }; // closed with its window
                updateNotice = bubble;
                bubble.SetBusy(updateBusy);
                if (updateFromNotice) bubble.SetStatus(updateStatus);
            }
            if (!ReferenceEquals(updateNotice.Owner, owner))
            {
                // Hidden while it changes hands: a window shown with an owner it then loses would stay behind it.
                if (updateNotice.IsVisible) updateNotice.Hide();
                updateNotice.Owner = owner;
            }
            updateNotice.MatchTheme(owner);
            if (updateNotice.Topmost != owner.Topmost) updateNotice.Topmost = owner.Topmost;
            updateNotice.SetVersion("새 버전 " + UpdateInfo.Format(updateNoticeFound?.Version ?? UpdateInfo.CurrentVersion));
            updateNotice.PlaceOver(anchor, workArea, anchor.X + anchor.Width / 2);
            if (!updateNotice.IsVisible) updateNotice.Show(); // ShowActivated = false: no focus taken
        }

        // Moves, resizes and hide / show of the window the bubble points at move it at once (the follow timer catches the
        // rest: pets laid out again, a placement or a week flip ending).
        private void WatchUpdateNoticeAnchor(Window window)
        {
            if (!updateNoticeWatched.Add(window)) return;
            void Follow() { if (updateNoticeFound != null && !closingApp) Dispatcher.BeginInvoke(new Action(RefreshUpdateNotice), DispatcherPriority.Render); }
            window.LocationChanged += (s, e) => Follow();
            window.SizeChanged += (s, e) => Follow();
            window.IsVisibleChanged += (s, e) => Follow();
            window.Closed += (s, e) => updateNoticeWatched.Remove(window);
        }

        private void CloseUpdateNoticeWindow()
        {
            var bubble = updateNotice;
            updateNotice = null;
            bubble?.Close();
        }

        private async Task RunUpdateFromNoticeAsync()
        {
            if (updateBusy) return;
            updateFromNotice = true;
            updateNotice?.SetStatus(null);
            try { await RunUpdateAsync(); }
            finally { updateFromNotice = false; }
        }

        // ✕: gone until the next start, or until a newer version than this one is found.
        private void CloseUpdateNoticeForSession()
        {
            updateNoticeClosedFor = updateNoticeFound?.Version;
            RefreshUpdateNotice();
        }

        // 하루 동안 안 보기: gone for 24 hours (saved, so a restart does not bring it back sooner).
        private void SnoozeUpdateNotice()
        {
            if (appData == null) return;
            appData.UpdateNoticeSnoozedUntil = DateTime.Now + UpdateNoticeSnooze;
            SaveDataSafely(false);
            RefreshUpdateNotice();
        }
    }
}
