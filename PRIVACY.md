# ScheduleWidget 개인정보처리방침 (Privacy Policy)

최종 수정: 2026-09-29

ScheduleWidget은 Windows PC에서 동작하는 개인용 일정 위젯입니다. 이 문서는 앱이 어떤 정보를 어디에 저장하는지, 그리고 사용자가 직접 켜거나 사용한 기능에 따라 어떤 외부 서비스와 통신하는지 설명합니다.

## 기본 원칙

- ScheduleWidget에는 개발자의 서버가 없습니다. 개발자는 사용자의 데이터를 받거나 볼 수 없으며, 앱은 분석·광고·오류 보고용 정보를 어디에도 보내지 않습니다.
- 일정과 설정은 사용자 PC의 `%LocalAppData%\ScheduleWidget` 폴더(`schedules.json`과 그 백업 `schedules.json.bak`)에만 저장됩니다. 가져온 캐릭터는 같은 폴더의 `Pet`에 저장되고, 문제 해결용 기록(`pet-log.txt` 등)도 같은 폴더에만 남으며 어디에도 보내지 않습니다.
- 아래의 외부 서비스는 사용자가 설정에서 직접 켜거나 그 기능을 직접 사용할 때만 쓰입니다. 통신은 사용자 PC와 해당 서비스 사이에서 직접 이루어지며, 각 서비스로 보낸 정보는 그 서비스의 개인정보처리방침을 따릅니다.

## 구글 계정 연동 (사용자가 직접 켰을 때만)

사용자가 설정에서 `구글 로그인`을 하고 연동 스위치를 켠 경우에만 다음 구글 API를 사용합니다.

- **Google Calendar API** (`https://www.googleapis.com/auth/calendar.events`): 사용자의 기본 캘린더 일정을 읽어 위젯에 표시하고, 위젯에서 추가·수정한 일정을 같은 캘린더에 반영합니다. 일정의 제목·날짜·시간·길이, 참석자가 있는지 또는 다른 사람이 주최한 일정인지(삭제할 때 구글에서도 지울지 정하는 데만 사용)만 사용합니다. 반복 일정은 앞으로 8주 치만 가져옵니다.
  - 위젯이 만든 일정에는 "이 앱이 만든 일정"이라는 표시와 완료 여부를 일정의 비공개 속성(`extendedProperties.private`, 다른 사람에게 보이지 않음)으로 저장합니다.
  - 위젯에서 일정을 지우면, 위젯이 만든 참석자 없는 일정만 구글 캘린더에서도 지웁니다. 참석자가 있는 일정이나 구글 캘린더에서 만든 일정은 위젯에서만 지우고 구글 캘린더에는 그대로 둡니다.
- **Google Drive API** (`https://www.googleapis.com/auth/drive.file`): 사용자가 가져온 캐릭터 이미지를 드라이브의 `ScheduleWidget 캐릭터` 폴더에 보관하고 다른 PC에서 받아 옵니다. 이 권한으로는 이 앱이 만든 파일만 볼 수 있으며, 드라이브의 다른 파일에는 접근할 수 없습니다. 캐릭터는 이 PC의 캐릭터 선택에서 삭제했을 때만 드라이브에서도 지웁니다.
- **계정 이메일 주소** (`email`): 설정 화면에 어떤 계정이 연결되었는지 표시하고, 다른 계정으로 로그인했는지 확인하는 데만 사용합니다.
- 로그인 정보(갱신 토큰)는 Windows 계정 암호화(DPAPI)로 보호해 사용자 PC에만 저장합니다.

구글 API에서 받은 정보는 위 기능에만 사용하며, 광고·분석·판매·제3자 제공에 사용하지 않습니다. ScheduleWidget의 구글 사용자 데이터 사용은 [Google API 서비스 사용자 데이터 정책](https://developers.google.com/terms/api-services-user-data-policy)(제한적 사용 요구사항 포함)을 따릅니다.

## 텔레그램·카카오톡 알림과 메시지 (사용자가 직접 설정했을 때만)

`연락 · 알림`에서 텔레그램 봇이나 카카오톡을 설정한 경우에만 사용합니다.

- 알림을 켜면, 마감이 다가온 일정의 제목·날짜·시간을 설정한 대화로 보냅니다. 텔레그램은 `api.telegram.org`(텔레그램 Bot API), 카카오톡은 `kapi.kakao.com`(카카오톡 메시지 API)을 사용합니다.
- `이 일정 보내기`나 메시지 창에서 사용자가 직접 보낸 내용도 같은 방식으로 선택한 서비스에 보냅니다.
- 카카오 토큰이 만료되면 갱신 토큰으로 카카오 인증 서버(`kauth.kakao.com`)에서 새 토큰을 받습니다.
- 텔레그램 봇 토큰과 카카오 액세스·갱신 토큰, 카카오 클라이언트 시크릿은 Windows 계정 암호화(DPAPI)로 보호해 PC에만 저장합니다. 텔레그램 채팅 ID, 카카오 REST API 키·웹 링크·친구 UUID, 전화번호는 설정 파일에 그대로 저장됩니다.
- 문자와 전화는 앱이 직접 보내지 않습니다. 문구를 클립보드에 복사하고 Windows의 `휴대폰과 연결` 앱이나 `tel:` 링크를 열 뿐입니다.

## 음악 (YouTube, 사용자가 직접 추가했을 때만)

- 사용자가 YouTube 영상이나 재생목록 링크를 추가하면, 제목과 재생목록의 곡 목록을 읽기 위해 앱이 YouTube(`www.youtube.com`의 재생목록 페이지와 oEmbed)에 직접 요청합니다. 이때 유럽 등의 쿠키 동의 화면을 건너뛰기 위한 동의 쿠키를 함께 보냅니다. 로그인 정보는 보내지 않습니다.
- 곡은 앱 안의 YouTube 플레이어(YouTube IFrame Player API, Microsoft Edge WebView2)로 재생합니다. YouTube가 설정하는 쿠키와 캐시는 `%LocalAppData%\ScheduleWidget\WebView2Music` 폴더에만 저장되며, 재생 정보는 YouTube(Google)의 개인정보처리방침에 따라 YouTube로 전송됩니다.
- WebView2 런타임 자체는 Windows와 Microsoft Edge의 개인정보 설정을 따릅니다. 캐릭터 화면은 PC에 있는 파일만 표시하며 인터넷에 연결하지 않습니다.

## 연동 해제와 삭제

- 설정의 `로그아웃`을 누르면 구글에 연결 권한 해제를 요청하고 PC에 저장된 구글 로그인 정보를 지웁니다. [구글 계정 → 보안 → 타사 앱 연결](https://myaccount.google.com/connections)에서도 언제든 해제할 수 있습니다.
- 텔레그램·카카오 연동은 `연락 · 알림`에서 토큰을 지우고 저장하면 해제됩니다. 카카오 앱 연결은 카카오 계정의 연결된 서비스 관리에서도 끊을 수 있습니다.
- PC에 저장된 모든 데이터(일정, 설정, 캐릭터, YouTube 플레이어의 쿠키)는 `%LocalAppData%\ScheduleWidget` 폴더를 지우면 삭제됩니다. 구글 캘린더·드라이브, 텔레그램, 카카오톡에 보낸 데이터는 각 서비스에서 직접 지울 수 있습니다.

## 문의

이 앱의 저장소 이슈 또는 앱 등록 시 입력한 지원 이메일로 문의해 주세요.

---

**English summary**: ScheduleWidget runs locally on Windows. It has no developer server, and it sends no analytics, ads or crash data anywhere; schedules and settings stay in `%LocalAppData%\ScheduleWidget` on the user's PC. It talks to outside services only for features the user turns on or uses, directly between the user's PC and that service:

- **Google** (after 구글 로그인 and the switches): the Google Calendar API (calendar.events) syncs the primary calendar's events (title, date, time, length, and whether the event has guests or another organizer, used only to decide whether deleting it in the widget also deletes it on Google; recurring events only for the next 8 weeks; events the widget creates carry a private "created by this app" mark and the completed flag). Deleting in the widget deletes on Google only the guest-less events the widget created. The Google Drive API (drive.file, app-created files only) keeps the user's imported characters, deleted there only when deleted on this PC. The account email is shown in the settings and used to tell accounts apart. Use of information received from Google APIs adheres to the Google API Services User Data Policy, including the Limited Use requirements; it is never sold, shared or used for ads.
- **Telegram / Kakao** (optional reminders and messages set up by the user): schedule titles, dates and times, or the message the user writes, are sent to the Telegram Bot API or the Kakao Talk message API; Kakao tokens are refreshed with Kakao's auth server. SMS and calls are only handed to Windows Phone Link / `tel:` with the text on the clipboard.
- **YouTube** (music the user adds): the app fetches YouTube playlist pages and oEmbed (with a consent cookie, no sign-in) to read titles, and plays songs in an embedded YouTube player (WebView2) whose cookies and cache stay in `%LocalAppData%\ScheduleWidget\WebView2Music`.

Tokens (Google refresh token, Telegram and Kakao tokens) are encrypted with Windows DPAPI on the user's PC. Disconnect Google anytime via 로그아웃 or https://myaccount.google.com/connections, clear the Telegram/Kakao keys in 연락 · 알림, and delete `%LocalAppData%\ScheduleWidget` to remove everything stored on the PC.
