# Google 검수용 시연 영상 구성

2026-11-12 최신 앱 기준의 촬영 대본입니다. 실제 촬영·업로드·데이터 접근 권한 심사 제출은 아직 하지 않았습니다. 사용자 화면에서 브랜딩 게시 완료와 외부/프로덕션 상태를 확인했으며, 별도 시연용 Google 계정을 보유하고 있습니다.

## 현재 PC에서 시연 공간 준비

일반 앱을 트레이에서 종료하고 같은 배포 실행 파일에 `--demo`를 붙여 실행합니다. 설정창의 `시연용 데이터` 표시를 확인합니다. 일정·로그인·캐릭터·내장 브라우저 캐시는 `%LocalAppData%\ScheduleWidget.Demo`에 저장되며 기존 데이터 폴더는 유지됩니다. 빈 저장 공간을 사용할 때 Google은 로그아웃 상태이고 두 동기화 스위치는 OFF입니다.

시연 모드는 실제 Google 로그인과 동기화를 그대로 사용합니다. 기본 브라우저에는 기존 계정이 남아 있을 수 있으므로 계정 선택 화면과 로그인 후 앱의 이메일을 확인합니다. 녹화 전에 현재 개인 계정의 연결을 해제하거나 기존 일정을 삭제하지 않습니다. 촬영을 마치면 시연 앱을 종료하고 평소 앱 바로가기로 돌아갑니다.

## 촬영 준비

- 제출 프로젝트 `schedulewidget-production`과 같은 OAuth 클라이언트를 사용하는 최신 배포의 시연 모드 또는 별도 Windows 프로필·PC에서 진행합니다.
- 계정 비밀번호·2단계 인증 코드는 녹화하지 않습니다. 앱 이름, 요청 권한, 동의 화면 주소의 OAuth client ID는 Google이 확인할 수 있어야 합니다. 토큰이나 client secret 파일을 열지 않습니다.
- 캘린더에는 시연용 일정만 준비합니다. 예: `ScheduleWidget review — created in widget`, `ScheduleWidget review — created in Google`.
- Drive 시연에는 직접 만든 작은 PNG 또는 샘플 캐릭터 ZIP을 준비합니다. 기본 모찌·설치된 Codex 기본 캐릭터는 Drive에 올리지 않으므로 시연 대상으로 고르지 않습니다.
- 세 권한을 실제로 보여 줄 수 있는 계정을 사용합니다. 필요한 경우 **검수용 계정에서만** 기존 앱 연결을 해제하고 다시 동의합니다. 현재 사용자의 실제 연결을 무단으로 끊지 않습니다.
- OAuth 동의 화면의 언어는 English로 맞춥니다. 앱은 한국어여도 되며 아래 영문 자막으로 설명할 수 있습니다.
- 확인되지 않은 앱 경고·차단이 나오면 실제 상태를 확인하고 Google의 정식 검수 절차로 해결합니다. 다른 클라이언트로 바꾸거나 동의 화면을 편집해 정상 동의처럼 보이게 만들지 않습니다.

## 촬영 순서와 영문 설명

| 장면 | 화면에서 보여 줄 작업 | 사용할 영문 설명 |
| --- | --- | --- |
| 1. 앱과 배포 | 설치는 촬영 전에 완료. 홈페이지와 시연 앱의 앱 정보에서 버전 확인 | ScheduleWidget is a Windows desktop calendar widget. Local scheduling works without a Google account. |
| 2. 사전 안내 | 설정 → 연동 → 구글에서 권한 설명과 개인정보처리방침 링크 표시 | Before connecting, the app explains the requested access and links to its public privacy policy. Calendar sync and character storage are optional. |
| 3. 동의 과정 | `Sign in with Google` → 기본 브라우저. 영어 동의 화면 전체, 앱 이름, client ID가 보이는 주소, 요청 권한을 보여 준 뒤 동의 | This native desktop app requests the declared Calendar, per-file Drive, and email scopes through Google's consent screen in the system browser. |
| 4. 연결 결과 | 앱으로 돌아와 설정 → 연동 → 구글을 다시 열고 계정 표시 확인 | The email address identifies the connected account. The switches control which synchronization features are used. |
| 5. 캘린더 켜기 | 구글 캘린더 연동 ON → 지금 동기화 → 상태 줄 | I enable synchronization of my existing primary Google Calendar. |
| 6. 앱에서 추가·수정 | 위젯에서 시연 일정을 추가하고 Enter로 저장. 동기화 후 Google Calendar에서 확인. 앱에서 제목·시간을 수정하고 반영 확인 | Creating or editing a schedule here updates the same event in Google Calendar. This is why read-only access is insufficient. |
| 7. Google에서 가져오기 | Google Calendar에 시연 일정을 만들고 앱에서 지금 동기화 → 위젯에서 확인 | Existing events in the user's primary calendar are displayed in the widget. A scope limited to app-created calendars would not support this. |
| 8. 삭제 범위 | 앱이 만든 참석자 없는 시연 일정을 앱에서 삭제하고 Google에서도 삭제됨을 확인. Google에서 만든 시연 일정은 앱에서 숨겨도 Google에 남는 것을 확인 | Remote deletion is limited to attendee-free events created by this app. Events created in Google Calendar are only removed from the widget. |
| 9. 캐릭터 보관 | 캐릭터 가져오기에서 준비한 파일 선택 → 캐릭터도 구글 드라이브에 보관 ON → 지금 동기화. Drive의 앱 폴더와 ZIP 확인 | Only the imported character's manifest and referenced image are packaged in Drive. The source folder, schedules and per-PC placement settings are not uploaded. |
| 10. 다른 PC 복원 | 가능하면 다른 검수용 PC에서 같은 계정으로 로그인하고 캐릭터 보관 ON → 캐릭터 선택에서 복원된 항목 확인 | The same account can retrieve the imported character package on another PC. Its local display position is configured independently. |
| 11. 사용자 제어 | 동기화 스위치 OFF, 로그아웃, 필요 시 검수용 계정의 연결 해제 경로 표시 | The user can turn synchronization off or sign out. Local schedules remain available. Access can also be revoked from the Google account. |

설정창은 다른 앱을 사용할 때 자동으로 닫힐 수 있습니다. 로그인 실패로 해석하거나 화면을 억지로 고정하지 말고, 브라우저 작업 뒤 설정을 다시 열어 실제 결과를 보여 줍니다. 동기화는 비동기이므로 완료 상태를 확인하고 다음 장면으로 넘어갑니다. 인터넷 장애·요청 제한 때문에 대기 중이면 성공한 것처럼 편집하지 않습니다.

## 촬영 후 확인

- [ ] 영상의 앱 버전·프로젝트·클라이언트가 실제 제출 대상과 일치
- [ ] 영어 OAuth 동의 화면 전체, 앱 이름·client ID·권한이 보임
- [ ] 캘린더의 읽기·추가·수정·조건부 삭제가 실제 데이터로 확인됨
- [ ] Drive의 앱 관리 캐릭터 파일과 계정 표시 기능이 확인됨
- [ ] 일반 일정, 다른 앱 창, 비밀번호, 토큰 등 불필요한 개인 정보가 없음
- [ ] 실제 보이지 않은 기능·승인 상태를 자막으로 단정하지 않음
- [ ] YouTube **일부 공개**로 올리고 로그아웃 상태에서 링크가 열리는지 확인
- [ ] 최종 영상 URL과 촬영한 앱 버전을 검수 담당자가 기록한 뒤 제출

Google이 요구하는 범위는 실제 콘솔의 안내에 맞춥니다. 다른 PC 복원은 기능 설명을 보강하는 장면이며 필수 심사 조건이라고 단정하지 않습니다. [Google의 영상 요구사항](https://support.google.com/cloud/answer/13464321?hl=en), [제출 절차](https://developers.google.com/identity/protocols/oauth2/production-readiness/sensitive-scope-verification)
