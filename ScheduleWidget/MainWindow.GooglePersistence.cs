using System.Threading.Tasks;

namespace ScheduleWidget
{
    public partial class MainWindow
    {
        private bool googlePreferencesUnsaved, googleSignOutUnsaved;
        private bool? googleRevocationSucceeded;

        private string GooglePersistenceWarning => !googlePreferencesUnsaved ? null : googleSignOutUnsaved
            ? googleRevocationSucceeded == false
                ? "로그아웃 정보 저장과 구글 권한 해제에 실패했습니다. 재시작하면 다시 연동될 수 있으니 종료 전에 다시 저장해 주세요."
                : googleRevocationSucceeded == true
                    ? "구글 권한은 해제했지만 로그아웃 정보를 저장하지 못했습니다. 다시 저장해 주세요."
                    : "로그아웃 정보를 저장하지 못했습니다. 종료 전에 다시 저장해 주세요."
            : "구글 연동 설정을 저장하지 못했습니다. 현재 선택은 유지되지만 재시작하면 이전 설정으로 돌아갈 수 있습니다.";

        private bool SaveGooglePreferences(bool signingOut = false)
        {
            googlePreferencesUnsaved = true;
            googleSignOutUnsaved = signingOut;
            bool saved = SaveDataSafely(false);
            UpdateGooglePersistenceWarning();
            return saved;
        }

        private void GooglePreferencesSaved()
        {
            if (!googlePreferencesUnsaved) return;
            googlePreferencesUnsaved = googleSignOutUnsaved = false;
            UpdateGooglePersistenceWarning();
        }

        private void UpdateGooglePersistenceWarning()
        {
            if (closingApp) return;
            UnifiedSettings?.ShowPersistentWarning(GooglePersistenceWarning);
            miniWindow?.SetGoogleSaveWarning(GooglePersistenceWarning);
        }

        internal async Task CompleteGoogleSignOutAsync()
        {
            CancelGoogleWork();
            int generation = googleWorkGeneration;
            googleRevocationSucceeded = null;
            var signOut = googleCalendar.SignOutAsync(GoogleSettings);
            googleLastError = googlePetStatus = null;
            SaveGooglePreferences(signingOut: true);
            UpdateGoogleTimers();
            UpdateGoogleUi();
            bool revoked = await signOut;
            if (closingApp || generation != googleWorkGeneration) return;
            googleRevocationSucceeded = revoked;
            if (!revoked) googleLastError = "현재 실행에서는 로그아웃했습니다. 구글 연결 권한은 해제하지 못했습니다.";
            UpdateGooglePersistenceWarning();
            UpdateGoogleUi();
        }
    }
}
