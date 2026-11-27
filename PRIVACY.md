# ScheduleWidget 개인정보처리방침 (Privacy Policy)

최종 수정: 2026-11-27

ScheduleWidget은 Windows PC에서 동작하는 개인용 일정 위젯입니다. 프로젝트 운영자는 [rnrdus5642](https://github.com/rnrdus5642)입니다. 이 문서는 앱이 어떤 정보를 어디에 저장하는지, 사용자가 선택한 기능에 따라 어떤 외부 서비스와 통신하는지, 소개 홈페이지가 처리하는 정보를 설명합니다. 읽기용 웹 문서는 [docs/privacy.html](docs/privacy.html)에 있습니다.

## 기본 원칙

- ScheduleWidget에는 사용자 데이터를 수집하는 개발자 서버가 없습니다. 앱은 일정·연동 토큰·분석·광고·오류 보고용 정보를 개발자에게 자동 전송하지 않습니다. 사용자가 지원 문의에 직접 포함한 정보의 처리는 아래 공유 항목에서 설명합니다.
- 일정과 설정의 로컬 사본은 사용자 PC의 `%LocalAppData%\ScheduleWidget` 폴더(`schedules.json`과 그 백업 `schedules.json.bak`)에 저장됩니다. 가져온 캐릭터는 같은 폴더의 `Pet`에 저장되고, 문제 해결용 기록(`pet-log.txt` 등)도 PC에 남습니다. 사용자가 켠 외부 연동에 따른 전송은 아래에서 구분해 설명합니다.
- `--demo` 시연용 실행에서는 같은 종류의 데이터를 `%LocalAppData%\ScheduleWidget.Demo`에 별도로 저장합니다. 기존 일정·로그인·캐릭터는 자동으로 복사하지 않습니다. 로그인과 선택한 외부 연동은 실제 서비스와 통신하며, 기본 브라우저의 로그인 상태는 Windows 사용자와 공유되므로 시연 계정을 직접 선택해야 합니다. 시연 실행은 일반 앱의 자동 시작 등록을 변경하지 않습니다.
- 아래의 외부 서비스는 사용자가 설정에서 직접 켜거나 그 기능을 직접 사용할 때만 쓰입니다. 통신은 사용자 PC와 해당 서비스 사이에서 직접 이루어지며, 각 서비스로 보낸 정보는 그 서비스의 개인정보처리방침을 따릅니다.

## 구글 계정 연동 (사용자가 직접 켰을 때만)

구글 로그인은 브라우저에서 진행하며 앱은 구글 계정 비밀번호를 입력받거나 보관하지 않습니다. 로그인할 때 아래의 캘린더·Drive·이메일 권한을 함께 요청합니다. 이메일은 연결 계정 확인에 사용하고, 캘린더와 캐릭터 보관은 `설정 → 연동 → 구글`의 각 스위치가 켜져 있을 때 동기화합니다. 처음 사용하는 경우 두 스위치는 OFF이며, 이전에 켜 둔 선택이 있으면 재연결 후 그 선택을 따릅니다. 스위치를 끄는 것은 동기화 중단이며 Google에서 허용한 권한의 철회와는 다릅니다.

- **Google Calendar API** (`https://www.googleapis.com/auth/calendar.events.owned`): 사용자의 기본 캘린더 일정을 읽어 위젯에 표시하고, 위젯에서 추가·수정한 일정을 같은 캘린더에 반영합니다. 일정의 제목·날짜·시간·길이, 참석자가 있는지 또는 다른 사람이 주최한 일정인지(삭제할 때 구글에서도 지울지 정하는 데만 사용)만 사용합니다. 반복 일정은 앞으로 8주 치만 가져옵니다.
  - 위젯이 만든 일정에는 "이 앱이 만든 일정"이라는 표시와 완료 여부를 일정의 비공개 확장 속성(`extendedProperties.private`)으로 저장합니다.
  - 위젯에서 일정을 지우면, 위젯이 만든 참석자 없는 일정만 구글 캘린더에서도 지웁니다. 참석자가 있는 일정이나 구글 캘린더에서 만든 일정은 위젯에서만 지우고 구글 캘린더에는 그대로 둡니다.
- **Google Drive API** (`https://www.googleapis.com/auth/drive.file`): 가져온 캐릭터의 이름·이미지 경로·동작 정의를 담은 `pet.json`과 해당 이미지를 ZIP으로 묶어 `ScheduleWidget 캐릭터` 폴더에 보관합니다. 같은 계정의 다른 PC에서도 보관 옵션을 켜면 받아 올 수 있습니다. 원본 폴더의 다른 파일이나 PC별 캐릭터 크기·반전·표시·배치 설정은 보내지 않습니다. 이 앱은 자체 캐릭터 보관 파일만 사용하고 다른 드라이브 파일을 탐색하지 않습니다. 이 PC의 캐릭터 선택 화면에서 명시적으로 삭제한 캐릭터만 드라이브에서도 삭제합니다.
- **계정 이메일 주소** (`https://www.googleapis.com/auth/userinfo.email`): 설정 화면에 어떤 계정이 연결되었는지 표시하고, 다른 계정으로 로그인했는지 확인하는 데만 사용합니다.
- 로그인 정보(갱신 토큰)는 Windows 계정 암호화(DPAPI)로 보호해 사용자 PC에만 저장합니다.

구글 API에서 받은 정보는 위 기능에 사용하며, 개발자가 광고·사용자 추적·판매·AI 모델 학습에 사용하지 않습니다. 사용자가 일정의 메시지 작성·전송을 선택하거나 전송 알림을 설정하면 일정 정보가 선택한 메시지 서비스로 전송될 수 있으며, 그 내용은 아래에 설명합니다. ScheduleWidget의 구글 사용자 데이터 사용은 [Google API 서비스 사용자 데이터 정책](https://developers.google.com/terms/api-services-user-data-policy)(제한적 사용 요구사항 포함)을 따릅니다.

## Google 사용자 데이터의 공유·전송·공개

**공유 대상과 조건:** Google에서 받은 일정 정보를 포함한 사용자 데이터는 사용자가 선택한 아래 기능을 제공하는 데만 전송됩니다.

- **Google:** Google 계정 연결과 Calendar·Drive API 요청은 사용자 PC와 Google 사이에서 직접 이루어집니다. 캘린더 연동을 켜면 일정의 제목·날짜·시간·완료 상태 등 동기화에 필요한 정보를 Google Calendar로 보냅니다. 캐릭터 보관을 켜면 가져온 캐릭터의 manifest와 이미지 ZIP을 사용자의 Google Drive에 저장하고 같은 계정의 다른 PC에서 받을 수 있습니다.
- **Telegram 및 선택한 채팅의 참여자:** 사용자가 Telegram 연동을 설정하고 직접 메시지를 보내거나 전송 알림을 켠 경우에만 일정 제목·날짜·시간과 작성한 메시지를 Telegram Bot API 및 지정한 채팅으로 보냅니다. **Google Calendar에서 가져온 일정도 이 대상에 포함될 수 있습니다.**
- **Kakao 및 선택한 수신자:** 사용자가 Kakao 연동을 설정하고 직접 메시지를 보내거나 전송 알림을 켠 경우에만 같은 종류의 일정·메시지 내용을 Kakao 메시지 API를 통해 자신의 대화 또는 지정한 친구에게 보냅니다. Google Calendar 연동을 켜는 것만으로 Telegram·Kakao 전송이 켜지지는 않습니다.
- **사용자가 선택한 다른 앱·수신자:** 메시지 복사를 선택하면 일정 문구를 Windows 클립보드에 넣습니다. 이를 다른 앱에 붙여넣거나 전송할 때는 사용자가 선택한 앱과 수신자가 그 내용을 받습니다. Windows의 클립보드 설정과 해당 서비스의 방침이 적용됩니다.
- **개발자와 지원 문의 서비스:** 앱은 Google 일정·캐릭터 파일·이메일·OAuth 토큰을 개발자 서버로 보내지 않습니다. 사용자가 이메일이나 GitHub 이슈로 지원 문의를 직접 보내면 개발자와 해당 이메일 제공자 또는 GitHub가 사용자가 포함한 내용을 받습니다. 앱이 Google 데이터를 문의에 자동 첨부하지 않으며, 비밀번호·토큰·개인 일정 원문은 보내지 않아도 됩니다.

Google 계정 이메일은 앱의 연결 계정 표시와 계정 구분에 사용하며, 앱이 Telegram·Kakao로 보내는 일정 메시지에 자동 첨부하지 않습니다. Google OAuth 토큰은 Google 인증과 API 접근에 사용하고 Telegram·Kakao에 전달하지 않습니다. 앱은 Google 사용자 데이터를 광고업체·데이터 중개업체·분석업체·AI 서비스에 제공하거나 판매·대여하지 않으며, AI/ML 모델의 개발·개선·학습에 사용하지 않습니다. 업데이트·홈페이지 제공자 GitHub와 실행 환경 제공자 Microsoft에 Google 일정이나 OAuth 토큰을 첨부하지 않습니다.

### Sharing, transfer and disclosure of Google user data

ScheduleWidget shares Google user data only as needed for the user-selected functions described here:

- **Google:** The PC communicates directly with Google's sign-in, Calendar and Drive services. Enabled Calendar synchronization sends the event details needed to create or update the user's events. Enabled character storage uploads an imported character's manifest and image ZIP to the user's Google Drive, where another PC using the same account can retrieve it.
- **Telegram and the configured chat participants:** Only after the user configures Telegram and sends a message or enables outgoing reminders, the app sends schedule titles, dates, times and composed message text through the Telegram Bot API to the selected chat. **This can include events obtained from Google Calendar.**
- **Kakao and the configured recipient:** The same user-enabled message or reminder functions can send schedule details through Kakao's messaging API to the user's own conversation or selected friend. Enabling Google Calendar synchronization alone does not enable either messaging service.
- **Other apps and recipients selected by the user:** Copying a schedule message places its text on the Windows clipboard. If the user pastes or sends it elsewhere, the selected app and recipients receive that text. Windows clipboard settings and the receiving service's privacy practices apply.
- **The developer and support providers:** The app does not send Google events, character files, account emails or OAuth tokens to a developer backend. If a user voluntarily contacts support by email or GitHub issue, the developer and the email provider or GitHub receive the information the user includes. The app does not automatically attach Google data to support requests; passwords, tokens and private event contents are not required.

The connected Google email is used locally for account identification and is not automatically added to Telegram or Kakao messages. Google OAuth tokens are used with Google, not supplied to Telegram or Kakao. We do not sell or rent Google user data or disclose it to advertisers, data brokers, analytics companies or AI providers. Google user data is not used to develop, improve or train AI/ML models. Google events and OAuth tokens are not attached to GitHub update/website requests or Microsoft runtime downloads.

## Google 사용자 데이터와 민감한 정보의 보호

- **전송 중 보호:** Google 인증·Calendar·Drive API 및 Telegram·Kakao 외부 API 통신은 HTTPS/TLS를 사용합니다. 로그인 결과를 앱으로 돌려주는 임시 `http://127.0.0.1` 콜백은 같은 PC의 루프백 주소에서만 수신하며 외부 서비스로 데이터를 보내는 HTTP 연결이 아닙니다.
- **저장된 인증 정보 보호:** Google 갱신 토큰과 Telegram·Kakao의 비밀 토큰은 현재 Windows 사용자 범위의 Windows DPAPI로 암호화해 저장합니다. Google의 단기 액세스 토큰은 프로세스 메모리에 보관하며 일정·설정 파일에 기록하지 않습니다.
- **로그인 과정 보호:** 시스템 브라우저에서 Google OAuth 동의를 받고 PKCE S256과 무작위 `state` 검증으로 인증 응답을 확인합니다. 앱은 Google 비밀번호를 입력받거나 보관하지 않습니다.
- **로컬 파일의 보호 범위:** 일정 내용, 연결 계정 이메일, 일반 설정, 캐릭터와 백업 파일 전체를 앱이 별도로 암호화하지는 않습니다. 이 파일은 Windows 사용자 프로필에 보관되며 Windows 계정 로그인과 파일 접근 권한의 보호를 받습니다. 기기·Windows 계정에 접근할 수 있는 사람이나 프로그램이 파일에 접근할 수 있으므로 사용자가 기기 접근을 관리해야 합니다. DPAPI 토큰 암호화와 전체 일정 파일 암호화는 서로 다릅니다.
- **접근과 전송 제한:** 앱은 선언한 세 Google 권한으로 기본 캘린더·앱의 캐릭터 파일·계정 이메일을 사용합니다. 사용자는 각 동기화를 끄거나 로그아웃하고 Google 계정에서 접근을 철회할 수 있습니다. 보관 기간과 로컬·외부 데이터 삭제 방법은 아래 삭제 항목에 설명합니다.

### Security safeguards for Google user data and sensitive information

- **Encryption in transit:** External requests to Google's authorization, Calendar and Drive APIs and to Telegram/Kakao APIs use HTTPS/TLS. The temporary HTTP redirect to `127.0.0.1` is a loopback callback on the same PC, not an external HTTP data transfer.
- **Stored credential protection:** Persisted Google refresh tokens and Telegram/Kakao secret tokens are encrypted using Windows DPAPI with `DataProtectionScope.CurrentUser`. Short-lived Google access tokens are kept in process memory and are not written to the schedule/settings file.
- **OAuth safeguards:** Sign-in uses the system browser, PKCE S256 and a random `state` value checked on return. ScheduleWidget does not collect or store the user's Google password.
- **Local storage limits:** Calendar content, the account email, ordinary settings, character assets and their backups are not separately encrypted as complete files by ScheduleWidget. They are stored in the Windows user profile and rely on Windows account authentication and filesystem access controls. People or software with access to that account or device may access those files. DPAPI protection of tokens is not encryption of the complete calendar database.
- **User control and limited access:** The three declared scopes support the primary calendar, app-managed character packages and account identification. Users can disable each synchronization feature, sign out or revoke access through their Google Account. Retention and deletion controls for local and external copies are described below.

## 텔레그램·카카오톡 알림과 메시지 (사용자가 직접 설정했을 때만)

`설정 → 연동`에서 텔레그램 봇이나 카카오톡을 설정하고, 메시지를 보내거나 `설정 → 알림`에서 전송 알림을 켠 경우에 사용합니다.

- 알림을 켜면, 마감이 다가온 일정의 제목·날짜·시간을 설정한 대화로 보냅니다. 텔레그램은 `api.telegram.org`(텔레그램 Bot API), 카카오톡은 `kapi.kakao.com`(카카오톡 메시지 API)을 사용합니다.
- 일정의 `메시지 작성`이나 연동 페이지에서 사용자가 직접 보낸 내용도 같은 방식으로 선택한 서비스에 보냅니다.
- 카카오 토큰이 만료되면 갱신 토큰으로 카카오 인증 서버(`kauth.kakao.com`)에서 새 토큰을 받습니다.
- 텔레그램 봇 토큰과 카카오 액세스·갱신 토큰, 카카오 클라이언트 시크릿은 Windows 계정 암호화(DPAPI)로 보호해 PC에만 저장합니다. 텔레그램 채팅 ID, 카카오 REST API 키·웹 링크·친구 UUID, 전화번호는 설정 파일에 그대로 저장됩니다.
- 문자와 전화는 앱이 직접 보내지 않습니다. 문구를 클립보드에 복사하고 Windows의 `휴대폰과 연결` 앱이나 `tel:` 링크를 열 뿐입니다.

## 음악 (YouTube, 사용자가 직접 추가했을 때만)

- 사용자가 YouTube 영상이나 재생목록 링크를 추가하면, 제목과 재생목록의 곡 목록을 읽기 위해 앱이 YouTube(`www.youtube.com`의 재생목록 페이지와 oEmbed)에 직접 요청합니다. 이때 유럽 등의 쿠키 동의 화면을 건너뛰기 위한 동의 쿠키를 함께 보냅니다. 로그인 정보는 보내지 않습니다.
- 곡은 앱 안의 YouTube 플레이어(YouTube IFrame Player API, Microsoft Edge WebView2)로 재생합니다. YouTube가 설정하는 쿠키와 캐시는 `%LocalAppData%\ScheduleWidget\WebView2Music` 폴더에만 저장되며, 재생 정보는 YouTube(Google)의 개인정보처리방침에 따라 YouTube로 전송됩니다.
- WebView2 런타임 자체는 Windows와 Microsoft Edge의 개인정보 설정을 따릅니다. 캐릭터 화면은 PC에 있는 파일만 표시하며 인터넷에 연결하지 않습니다.

## 연동 해제와 삭제

일정·설정·가져온 캐릭터는 사용자가 삭제할 때까지 PC에 보관합니다. 기능을 끄거나 구글에서 로그아웃해도 이미 PC에 저장된 일정과 캐릭터는 남습니다. 외부 서비스로 보낸 데이터에는 해당 서비스의 보관·삭제 정책도 적용됩니다.

- 설정의 `로그아웃`은 현재 앱의 구글 작업을 취소하고 연결 권한 해제와 로컬 로그인 정보 삭제를 시도합니다. 네트워크나 파일 저장에 실패하면 상태를 알리며, 저장 실패 경고의 `다시 저장`으로 로컬 변경을 재시도할 수 있습니다. [구글 계정 → 보안 → 타사 앱 연결](https://myaccount.google.com/connections)에서도 언제든 해제할 수 있습니다.
- 텔레그램·카카오 연동은 `설정 → 연동`에서 토큰을 지우고 저장하면 해제됩니다. 카카오 앱 연결은 카카오 계정의 연결된 서비스 관리에서도 끊을 수 있습니다.
- 앱을 종료하고 `%LocalAppData%\ScheduleWidget` 폴더를 지우면 일정·설정·백업·캐릭터·YouTube 플레이어 쿠키 등 해당 폴더의 데이터가 삭제됩니다. 구글 캘린더·드라이브, 텔레그램, 카카오톡에 보낸 데이터는 각 서비스에서 직접 지울 수 있습니다.

## PC의 미디어 정보와 캐릭터 반응

- 다른 앱의 음악을 표시하고 조작하기 위해 Windows 미디어 세션의 앱 이름·곡 제목·재생 상태·위치를 읽습니다. 관련 창 제목과 프로세스 이름을 확인하고 Windows 오디오 세션으로 음량을 읽거나 사용자의 조작을 전달합니다. 이 정보는 PC에서 처리하며 개발자 서버로 보내지 않습니다.
- 타이핑 반응을 선택한 캐릭터가 보일 때 키가 눌렸다는 사실을 세어 애니메이션에 사용합니다. 어떤 키를 눌렀는지나 입력 문장을 기록·저장·전송하지 않습니다.

## 캐릭터 제작 안내와 외부 링크

- `캐릭터 추가 방법`의 설명과 제작 규격은 앱에 포함되어 오프라인에서도 볼 수 있습니다. `JSON 복사`·`제작 요청문 복사`는 사용자가 누를 때 예시 텍스트를 PC 클립보드에 복사하며, 앱이 이미지 생성 서비스로 보내지는 않습니다.
- 갤러리·다운로드 예시, 서비스 설정 도움말, 홈페이지 링크는 사용자가 누를 때 기본 브라우저에서 엽니다. 캐릭터 자료실은 `codexpets.org`이며 해당 사이트의 접속 정보·쿠키 처리는 그 사이트와 브라우저의 방침을 따릅니다. 앱은 이 링크에 일정·토큰·로컬 파일을 첨부하지 않습니다.
- 받은 캐릭터 파일은 사용자가 `캐릭터 가져오기`로 선택할 때 PC에 복사됩니다. 구글 드라이브 보관은 별도로 켜야 동작합니다.

## 업데이트와 소개 홈페이지

- 홈페이지의 설치 파일은 GitHub에서 내려받습니다. 설치 과정에서 .NET Framework·WebView2가 없으면 Microsoft 공식 부트스트래퍼가 Microsoft 서버에서 실행 환경을 받으며, 해당 구성 요소의 설치·업데이트에는 Microsoft의 개인정보 처리방침이 적용됩니다. ScheduleWidget은 이 과정에 일정이나 연동 토큰을 전달하지 않습니다. 앱 제거는 프로그램·바로가기·해당 설치의 자동 시작 항목만 지우며 일정·설정은 남깁니다.
- 업데이트 확인·다운로드 시 GitHub의 배포 주소에 접속합니다. 일정이나 연동 토큰을 요청에 포함하지 않으며 받은 업데이트의 서명을 확인합니다.
- 소개 홈페이지는 GitHub Pages에서 제공하는 정적 페이지입니다. 자체 분석·광고·로그인·개인정보 입력 폼을 사용하지 않고 자체 쿠키나 브라우저 저장소를 생성하지 않습니다. GitHub는 보안을 위해 방문자의 IP 주소 등 접속 정보를 처리할 수 있습니다. [GitHub 개인정보처리방침](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement)을 참고해 주세요.

## 문의

[rnrdus5642@gmail.com](mailto:rnrdus5642@gmail.com) 또는 [ScheduleWidget GitHub 이슈](https://github.com/rnrdus5642/ScheduleWidget/issues)로 문의해 주세요. 공개 이슈에는 일정 원문이나 토큰 등의 개인 정보를 첨부하지 않아도 됩니다. 처리 내용이 바뀌면 이 문서와 웹 문서, 최종 수정일을 갱신합니다.

---

**English summary**: ScheduleWidget runs locally on Windows. The app does not automatically send schedules, credentials, analytics or crash reports to the developer. Local copies of schedules and settings are stored in `%LocalAppData%\ScheduleWidget`; optional external transfers and voluntary support requests are described in the sharing section above. The security section above explains HTTPS/TLS, DPAPI credential encryption, OAuth safeguards and the limits of local file protection. The app talks to outside services for features the user turns on or uses, directly between the user's PC and that service:

- **Google** (browser sign-in, then optional sync switches): the Google Calendar API (calendar.events.owned) syncs the primary calendar's events (title, date, time, length, and whether the event has guests or another organizer, used to decide whether deleting it in the widget also deletes it on Google; recurring events only for the next 8 weeks; events the widget creates carry a private "created by this app" mark and the completed flag). Deleting in the widget deletes on Google only the guest-less events the widget created. The Google Drive API (drive.file, app-created files only) stores one ZIP per imported character containing its manifest and image, excluding unrelated source-folder files and per-PC placement, size, visibility and flip settings. Each PC must enable character storage to share packages through the same account. Cloud deletion follows explicit deletion in the character picker. The account email is shown in the settings and used to tell accounts apart. Use of Google API data adheres to the Google API Services User Data Policy, including Limited Use requirements. It is not used for advertising, sale, tracking or AI model training. User-enabled messages and reminders may send schedule details to the selected service, as described below.
- **Telegram / Kakao** (optional reminders and messages set up by the user): schedule titles, dates and times, or the message the user writes, are sent to the Telegram Bot API or the Kakao Talk message API; Kakao tokens are refreshed with Kakao's auth server. SMS and calls are only handed to Windows Phone Link / `tel:` with the text on the clipboard.
- **YouTube** (music the user adds): the app fetches YouTube playlist pages and oEmbed (with a consent cookie, no sign-in) to read titles, and plays songs in an embedded YouTube player (WebView2) whose cookies and cache stay in `%LocalAppData%\ScheduleWidget\WebView2Music`.

The character creation guide is bundled locally. Its copy buttons put example text on the clipboard only when clicked. Gallery and help links open in the user's browser without attaching schedules, credentials or local files; those sites apply their own privacy practices.

The desktop sign-in requests the three declared Google scopes together. Email identifies the connected account; the separate Calendar and Drive switches control subsequent synchronization. Both switches are off for a new user, and existing enabled preferences may resume after reconnection. Turning a switch off stops synchronization but does not revoke the Google grant.

The optional --demo launch stores a separate set of schedules, credentials, characters and embedded-browser caches in %LocalAppData%\ScheduleWidget.Demo without importing the normal profile. It uses the same real Google services and declared scopes. Sign-in still opens the system browser, whose account session is shared with the Windows user. Both data folders are retained when the program is uninstalled.

Windows media information, volume controls and character typing reactions are processed locally. Typing reactions count key presses without recording keys or text. Updates are retrieved from GitHub. This static website has no custom tracking scripts or forms; GitHub Pages may process visitor IP addresses for security.

The Windows installer is downloaded from GitHub. If required runtimes are missing, bundled official Microsoft bootstrappers download .NET Framework or WebView2 from Microsoft; their installation and updating follow Microsoft's privacy practices. ScheduleWidget does not pass schedules or account tokens to these installers. Uninstalling removes the program, shortcuts and its startup entry while retaining local user data for reinstallation.

Tokens (Google refresh token, Telegram and Kakao tokens) are encrypted with Windows DPAPI on the user's PC. Data remains until deleted. Disconnect Google anytime via 로그아웃 or https://myaccount.google.com/connections, clear the Telegram/Kakao keys in 설정 → 연동, or close the app and delete `%LocalAppData%\ScheduleWidget` to remove local data and backups. Data previously sent to external services must be managed in those services. Contact: [project issues](https://github.com/rnrdus5642/ScheduleWidget/issues).
