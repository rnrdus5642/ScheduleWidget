using System;
using System.Threading.Tasks;
using System.Windows;

namespace ScheduleWidget
{
    // 설정 › 업데이트: one button. Updates come from the public GitHub repository set in code (UpdateClient.GitHubRepository);
    // nothing here is a setting (저장/취소/초기화 do not touch it). While the repository is blank the button is disabled.
    // Pressed: check → a newer version whose signature was verified is confirmed in a dialog (version + notes) → download and
    // check the package → start the updater → close the app the normal way (ExitApplication: data saved, single-instance
    // mutex released) so the updater can replace the files. Up to date, declined or failed: the status line says so.
    public partial class MainWindow
    {
        private readonly UpdateClient updateClient = new UpdateClient();
        private bool updateBusy;
        private string updateStatus;

        // Test hooks (set by reflection in the checks): answer the confirm dialog, and take the staging folder in place of
        // starting the updater and closing the app.
#pragma warning disable CS0649 // set only by the checks
        private Func<UpdateCheckResult, bool> updateConfirmOverride;
        private Action<string> updateApplyOverride;
#pragma warning restore CS0649

        // Called when the panel opens.
        private void LoadUpdateUi()
        {
            UpdateCurrentVersionText.Text = "현재 버전 " + UpdateInfo.CurrentVersionText;
            UpdateUpdateButton();
            ShowUpdateStatus();
            _ = CheckForUpdateNoticeAsync(); // look now: a version put on GitHub since the last check turns the button on
        }

        // On only when the last check found a newer version whose signature was verified (up to date, an older release on
        // GitHub, none yet or not reachable: off).
        private void UpdateUpdateButton()
        {
            if (UpdateButton != null) UpdateButton.IsEnabled = UpdateClient.IsConfigured && !updateBusy && updateNoticeFound != null;
            updateNotice?.SetBusy(updateBusy);
        }

        private void SetUpdateStatus(string text)
        {
            updateStatus = text;
            ShowUpdateStatus();
            if (updateFromNotice) updateNotice?.SetStatus(text); // started from the 업데이트 알림 bubble: its status line too
        }

        private void ShowUpdateStatus()
        {
            if (UpdateStatusText == null) return;
            UpdateStatusText.Text = updateStatus ?? "";
            UpdateStatusText.Visibility = string.IsNullOrEmpty(updateStatus) ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void UpdateButton_Click(object sender, RoutedEventArgs e) => await RunUpdateAsync();

        // Never throws: whatever goes wrong is the status line. The 업데이트 알림 bubble's 업데이트 runs this too (the window
        // the bubble points at then owns the confirm dialog).
        internal async Task RunUpdateAsync()
        {
            if (updateBusy || !UpdateClient.IsConfigured) return;
            updateBusy = true;
            UpdateUpdateButton();
            SetUpdateStatus("업데이트를 확인하는 중…");
            try
            {
                UpdateCheckResult update;
                try { update = await updateClient.CheckAsync(); }
                catch (Exception ex) { SetUpdateStatus(UpdateErrorText(ex, "업데이트를 확인하지 못했습니다.")); return; }
                updateNoticeFound = update.IsNewer && update.SignatureVerified ? update : null; // the button follows this answer
                if (!update.IsNewer || !update.SignatureVerified)
                {
                    SetUpdateStatus("최신 버전입니다 (현재 " + UpdateInfo.Format(update.Current) + ").");
                    return;
                }
                if (!ConfirmUpdate(update))
                {
                    SetUpdateStatus("새 버전 " + UpdateInfo.Format(update.Version) + "이 있습니다. 설치하지 않았습니다.");
                    return;
                }

                string staging;
                try
                {
                    SetUpdateStatus("내려받는 중… 0%");
                    var progress = new Progress<double>(p => SetUpdateStatus("내려받는 중… " + (int)Math.Round(Math.Max(0, Math.Min(1, p)) * 100) + "%"));
                    staging = await updateClient.DownloadAsync(update, progress);
                    SetUpdateStatus("확인 완료. 업데이트를 시작합니다…");
                    if (updateApplyOverride != null) { updateApplyOverride(staging); return; }
                    UpdateClient.StartUpdater(staging);
                }
                catch (Exception ex) { SetUpdateStatus(UpdateErrorText(ex, "업데이트를 설치하지 못했습니다.")); return; }
                // The updater waits for this process to end: close the normal way so saves flush and the mutex is released.
                ExitApplication();
            }
            finally
            {
                updateBusy = false;
                UpdateUpdateButton();
            }
        }

        private bool ConfirmUpdate(UpdateCheckResult update)
        {
            if (updateConfirmOverride != null) return updateConfirmOverride(update);
            string text = "새 버전 " + UpdateInfo.Format(update.Version) + "이 있습니다 (현재 " + UpdateInfo.Format(update.Current) + "). 서명 확인됨.";
            string notes = update.Manifest?.Notes;
            if (!string.IsNullOrWhiteSpace(notes))
                text += Environment.NewLine + Environment.NewLine + (notes.Length > 500 ? notes.Substring(0, 500) + "…" : notes.Trim());
            text += Environment.NewLine + Environment.NewLine + "설치하면 내려받은 뒤 앱이 닫히고 새 버전으로 다시 열립니다." + Environment.NewLine +
                    "일정과 설정은 그대로 유지됩니다. 설치할까요?";
            Window fromNotice = updateFromNotice ? updateNotice?.Owner : null;
            Window owner = fromNotice != null && fromNotice.IsVisible ? fromNotice : settingsHost ?? this;
            return System.Windows.MessageBox.Show(owner, text, "업데이트", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        }

        private static string UpdateErrorText(Exception ex, string fallback)
        {
            if (ex is InvalidOperationException) return ex.Message; // ours: Korean
            if (ex is OperationCanceledException) return "취소되었습니다.";
            ErrorLog.Write("update", ex);
            return fallback;
        }
    }
}
