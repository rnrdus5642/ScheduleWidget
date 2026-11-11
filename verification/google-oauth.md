# Google OAuth 검수 준비

자료 기준: 2026-11-11. 현재 소스·배포 구조와 Google 공식 안내를 대조한 제출 준비 문서입니다. 이 문서의 작성이나 앱 배포는 Google의 승인 또는 심사 제출을 뜻하지 않습니다.

## 지금 확인된 사항

| 항목 | 상태와 근거 |
| --- | --- |
| 프로젝트 | 현재 배포 앱에 `schedulewidget-production`의 데스크톱 클라이언트가 내장되어 있음 |
| 로그인 | 기본 브라우저, PKCE S256, 임의 포트의 `127.0.0.1` 콜백과 `state` 검증 사용 |
| 권한 | 아래 3개를 로그인 시 함께 요청. 캘린더·Drive 스위치는 동의 이후 실제 동기화를 제어 |
| 홈페이지·방침 | `https://schedule.jwstudio.page/`와 `/privacy.html`이 로그인 없이 공개됨 |
| 도메인 | `jwstudio.page`에 Google 확인용 DNS TXT가 있음. Search Console 소유자 승인 완료 여부는 미확인 |
| 기능 검증 이력 | 2026-10-14 실제 로그인·캘린더 조회 성공 기록, 2026-11-11 로컬 동기화 기록 확인. 최신 배포의 새 OAuth 동의 흐름은 별도 촬영 필요 |
| 마지막 심사 관련 기록 | 2026-10-15 배포 설명에 캘린더 권한 심사 미완료로 기록됨. 현재 콘솔 결과를 대신하지 않음 |
| 현재 콘솔 상태 | 대상 사용자·게시 상태·브랜딩·데이터 액세스 심사 모두 직접 확인 필요 |
| 영상·심사 제출 | 이 준비 작업에서는 촬영·업로드·심사 제출·개발자 이메일 발송을 수행하지 않음 |

프로젝트 이름의 `production`, 공개 홈페이지, DNS 확인용 TXT만으로 Google의 프로덕션 게시·도메인 승인·앱 검수를 완료했다고 표시하지 않습니다.

## 콘솔에서 대조할 값

| 항목 | 사용할 값 |
| --- | --- |
| 프로젝트 ID | `schedulewidget-production` |
| 앱 이름 | `ScheduleWidget` |
| 앱 유형 | Windows 데스크톱 앱 |
| OAuth 클라이언트 유형 | Desktop app / 데스크톱 앱. 현재 앱에 내장된 기존 클라이언트 사용 |
| 대상 사용자 | 공개 배포에 맞는 External 여부 확인. 현재 설정값은 미확인 |
| 승인된 도메인 | `jwstudio.page` |
| 홈페이지 | `https://schedule.jwstudio.page/` |
| 개인정보처리방침 | `https://schedule.jwstudio.page/privacy.html` |
| 설치 파일 | `https://github.com/rnrdus5642/ScheduleWidget/releases/latest/download/ScheduleWidget-Setup.exe` |
| 사용자 지원 이메일 | **콘솔 소유자가 실제 수신 가능한 주소를 확인해 입력** |
| 개발자 연락처 | **현재 프로젝트 소유자·편집자와 담당 이메일 확인** |
| 시연 영상 URL | **실제 촬영 후 YouTube 일부 공개 링크 입력** |

새 클라이언트를 만들어 기존 검수를 우회하거나 기존 사용자의 동의 연결을 바꾸지 않습니다. 영상의 클라이언트 ID는 제출할 기존 클라이언트와 같아야 합니다. 비밀번호, OAuth client secret, 액세스·갱신 토큰을 이 문서나 영상에 넣지 않습니다.

## 권한과 데이터 흐름

| 실제 요청 범위 | 필요한 기능 | 더 좁은 대안 검토 |
| --- | --- | --- |
| `https://www.googleapis.com/auth/calendar.events.owned` | 본인 기본 캘린더의 일정을 표시하고 앱에서 만든·수정한 일정을 반영 | 읽기 전용은 추가·수정·삭제를 지원하지 않음. `calendar.app.created`는 사용자가 이미 쓰는 기본 캘린더를 다룰 수 없음. 공유받은 캘린더까지 다루는 `calendar.events`는 요청하지 않음 |
| `https://www.googleapis.com/auth/drive.file` | 사용자가 가져온 캐릭터의 manifest·이미지 ZIP 보관 및 다른 PC에서 복원 | 앱이 만든 캐릭터 보관 파일에 한정. Drive 전체 접근이나 Gmail 권한을 요청하지 않음 |
| `https://www.googleapis.com/auth/userinfo.email` | 현재 연결 계정 표시, 다른 계정으로 바뀌었는지 구분 | 프로필 사진·전체 프로필은 기능에 필요하지 않아 요청하지 않음 |

로그인에서 위 세 권한을 함께 요청합니다. 새 사용자에게 캘린더·캐릭터 보관은 기본 OFF이며, 사용자가 각각 켜야 동기화됩니다. 이전에 켜 둔 설정이 있으면 재연결 후 그 선택에 따라 다시 동기화될 수 있습니다. **스위치를 켤 때 권한을 처음 요청하는 구조라고 설명하면 안 됩니다.** Google은 설치형 앱에서 증분 권한 요청을 지원하지 않는다고 안내합니다. [데스크톱 OAuth 안내](https://developers.google.com/identity/protocols/oauth2/native-app)

- 캘린더는 `/calendars/primary/events`를 사용합니다. 제목·시작/끝 날짜와 시간·완료 상태·연동 ID를 처리하고, 참석자/주최자 정보로 삭제 가능 여부를 판단합니다. 반복 일정은 앞으로 8주까지 표시합니다.
- Google에서 만들어진 일정도 위젯에서 수정할 수 있습니다. 위젯에서 삭제할 때 Google에서도 지우는 대상은 **위젯이 만든 참석자 없는 일정**입니다. Google에서 만든 일정은 위젯에서만 숨깁니다.
- Drive에는 `ScheduleWidget 캐릭터` 폴더의 앱 관리 ZIP을 사용합니다. 원본 폴더 전체, 일정 데이터, 플레이리스트, PC별 캐릭터 크기·표시·반전·모드별 배치는 보내지 않습니다. 기본 캐릭터와 설치된 Codex의 기본 캐릭터는 보관 대상이 아닙니다.
- 앱과 Google이 직접 통신하며 개발자 데이터 수집 서버는 없습니다. 일정·설정과 백업은 `%LocalAppData%\ScheduleWidget`에 남고, 갱신 토큰은 Windows DPAPI로 보호합니다.
- 사용자가 Telegram·Kakao 알림이나 메시지 전송을 별도로 설정하면 Google에서 가져온 일정을 포함한 선택된 일정 정보가 그 서비스로 갈 수 있습니다. 따라서 제출 자료에 **Google 데이터는 어떤 경우에도 제3자에게 전달되지 않는다**고 쓰면 안 됩니다. 사용 목적·전송 조건은 공개 개인정보처리방침과 일치시킵니다.
- 로그아웃은 이 PC의 로그인 정보를 비우고 Google에 토큰 철회를 요청합니다. 통신 실패 시 Google 계정의 연결 관리에서 해제해야 할 수 있습니다. 로그아웃·앱 제거가 Google 캘린더·Drive의 데이터를 자동으로 지우지는 않습니다.
- 바탕화면 일정·로컬 음악·캐릭터는 Google 로그인 없이 사용할 수 있습니다. YouTube 음악 재생은 별도 WebView2 플레이어이며 Google OAuth에 YouTube 권한을 추가하지 않습니다.

Drive의 `drive.file`은 Google이 안내하는 비민감 범위이며, 캘린더 접근의 심사 필요성은 데이터 액세스 화면에서 대조합니다. 보안 평가 면제나 승인 결과를 미리 단정하지 않습니다. [Drive 범위 안내](https://developers.google.com/workspace/drive/api/guides/api-specific-auth), [Calendar 범위 안내](https://developers.google.com/workspace/calendar/api/auth)

## 제출용 영문 초안

아래 문구는 코드와 맞춘 설명 초안입니다. 실제 콘솔의 입력란과 글자 수에 맞춰 사용하고, 해당 콘솔에 표시된 범위와 기능을 마지막으로 대조합니다.

### App description

```text
ScheduleWidget is a Windows desktop calendar and task widget. Users can manage schedules locally without a Google account, optionally synchronize their existing primary Google Calendar, and optionally store imported animated-character packages in Google Drive for use on their other PCs. Google sign-in takes place in the user's system browser. Calendar synchronization and character storage are controlled separately in the app's settings.
```

### calendar.events.owned justification

```text
ScheduleWidget uses the user's existing primary calendar to display upcoming schedules in a desktop widget. Users can create and edit events in the widget and synchronize those changes to Google Calendar. Remote deletion is limited to attendee-free events originally created by this app. Read-only scopes cannot support these writes. calendar.app.created cannot access the existing primary calendar that users already manage. calendar.events would unnecessarily include calendars owned by other people, so we request calendar.events.owned. We do not manage calendar sharing, ACLs, or calendar settings. Synchronization runs only while the user has enabled the Calendar switch.
```

### drive.file justification

```text
When the user enables character storage, ScheduleWidget creates a ScheduleWidget character folder and stores app-managed ZIP packages containing an imported character's pet.json manifest and referenced image. The same account can restore these packages on another PC. Recorded local character deletions can be synchronized to those app-managed files. We do not browse unrelated Drive files, upload the source folder as a whole, or upload schedules, playlists, or per-PC character placement settings. drive.file provides the per-file access required by this feature; full Drive access is not requested.
```

### userinfo.email justification

```text
ScheduleWidget uses the signed-in account's email address to show which Google account is connected and to distinguish a changed account from the account associated with existing calendar links. It is not used for advertising, mailing lists, tracking, or profile enrichment. No broader profile scope is requested.
```

### Additional information

```text
This is a native Windows desktop app using a Desktop OAuth client, an external system browser, PKCE S256, a random loopback port, and state validation. The three declared scopes are requested together during sign-in; the Calendar and Drive switches control use of the granted access afterward. The developer does not operate a backend that receives Google user data. Refresh tokens are protected with Windows DPAPI on the user's PC. Calendar data is used for visible scheduling features. Only when users separately configure Telegram/Kakao reminders or choose to send a message can selected schedule details be sent directly to those providers. Google user data is not used for advertising, sale, or AI model training. The public privacy policy describes retention, backups, optional transfers, and revocation.
```

## 업데이트 이후 설명이 달라진 부분

| 예전 안내에서 점검할 표현 | 현재 안내 |
| --- | --- |
| ZIP을 풀어 실행 | 홈페이지의 Windows용 다운로드 → 설치 EXE 실행 → 설치. ZIP은 계속 제공하는 보조 방식 |
| 로그인하면 바로 캘린더 연동 | 로그인과 기능 ON/OFF는 구분. 첫 로그인에서 세 권한을 요청하고 스위치가 동기화를 제어 |
| 일반 구글 로그인 버튼 | 공식 사전 승인 `Sign in with Google` 이미지. 한국어 접근성 이름·툴팁 제공 |
| 캐릭터 폴더 전체 백업 | 가져온 캐릭터 manifest·이미지 ZIP만 보관. PC별 배치·크기 등은 로컬 설정 |
| 한 위치만 쓰는 캐릭터 | 기본·월간 모드별 배치 저장. Drive 패키지에 배치 설정을 포함하지 않음 |
| 설정창이 계속 열린 상태로 시연 | 다른 앱으로 전환하면 설정창이 닫힐 수 있음. 브라우저 로그인 후 설정 → 연동을 다시 열어 결과 확인 |
| 잠깐 실패한 캐릭터는 계속 건너뜀 | 일시적인 연결·파일 문제는 재시도하고, 손상되거나 지원하지 않는 파일만 같은 리비전 재시도를 제한 |
| 제거하면 일정도 삭제 | 프로그램 제거는 로컬 일정·설정·캐릭터를 남김. 전체 데이터 삭제는 별도 사용자 작업 |

## 제출 전 확인표

- [x] 내장 프로젝트와 Desktop 클라이언트 유형 확인
- [x] 실제 요청 범위 3개와 권한 설명 초안 정리
- [x] 앱·홈페이지·개인정보 문서의 권한 요청 시점 맞춤
- [x] 공식 로그인 버튼 자산과 취소·로그아웃 상태 구분
- [x] 설치 방식·캐릭터 공유 범위·오프라인 재시도·삭제 설명 갱신
- [ ] 프로젝트 소유자/편집자 계정으로 콘솔 접근
- [ ] Branding / Audience / Data Access / Verification Center의 현재 상태 기록
- [ ] 같은 프로젝트 관계자의 Search Console 도메인 소유권 승인 확인
- [ ] 지원 이메일·개발자 연락처·앱 이름·도메인·정책 URL 대조
- [ ] 브랜딩 검증 완료 후 게시. 오래된 Ready to publish 결과는 재검증 여부 확인
- [ ] 최신 배포와 같은 클라이언트로 새 동의 및 실제 기능 시연
- [ ] [촬영 순서](google-oauth-demo.md)에 따라 영상 촬영·검토·일부 공개 업로드
- [ ] 캘린더 권한 사용 이유와 영상 링크를 입력하고 심사 제출
- [ ] 심사 담당 이메일과 인증 센터의 추가 요청 처리
- [ ] 승인·공개 게시 상태를 확인한 뒤 새 계정과 다른 PC에서 로그인 확인

브랜딩 검증 결과를 게시하지 않은 채 7일이 지나면 재검증이 필요할 수 있습니다. 현재 Google 안내는 브랜딩을 게시한 뒤 데이터 액세스 심사를 진행하도록 설명합니다. [공식 제출 순서](https://developers.google.com/identity/protocols/oauth2/production-readiness/sensitive-scope-verification)

## 반복 확인

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-GoogleVerification.ps1 -ApplicationPath '<검수할 배포 폴더>\ScheduleWidget.exe' -ReportPath '.work/google-verification-report.json'
```

이 도구는 앱 내장 메타데이터, 공개 URL, DNS를 읽습니다. 사용자 일정·갱신 토큰을 읽거나 Google 로그인·동기화·철회·심사 제출을 수행하지 않습니다. 프로젝트 소유권과 심사 상태를 대신 확인하지 않으며, 승인 결과는 항상 콘솔에서 확인합니다.

공식 기준: [검수 요구사항](https://support.google.com/cloud/answer/13464321?hl=en), [민감한 권한 검수](https://developers.google.com/identity/protocols/oauth2/production-readiness/sensitive-scope-verification), [로그인 버튼 가이드](https://developers.google.com/identity/branding-guidelines). 설명의 기준 코드는 [GoogleCalendarService.cs](../ScheduleWidget/Services/GoogleCalendarService.cs), [MainWindow.Google.cs](../ScheduleWidget/MainWindow.Google.cs), 공개 방침은 [PRIVACY.md](../PRIVACY.md)입니다.
