# Google OAuth 검수 준비

자료 기준: 2026-11-27. 현재 소스·배포 구조와 Google 공식 안내, 사용자가 제공한 콘솔 화면·심사 메일을 대조한 제출 기록입니다. 앱 배포는 Google의 데이터 접근 권한 심사 완료를 뜻하지 않습니다.

## 지금 확인된 사항

| 항목 | 상태와 근거 |
| --- | --- |
| 프로젝트 | 현재 배포 앱에 `schedulewidget-production`의 데스크톱 클라이언트가 내장되어 있음 |
| 로그인 | 기본 브라우저, PKCE S256, 임의 포트의 `127.0.0.1` 콜백과 `state` 검증 사용 |
| 권한 | 아래 3개를 로그인 시 함께 요청. 캘린더·Drive 스위치는 동의 이후 실제 동기화를 제어 |
| 홈페이지·방침 | `https://schedule.jwstudio.page/`와 `/privacy.html`이 로그인 없이 공개됨 |
| 도메인 | `jwstudio.page`에 Google 확인용 DNS TXT가 있음. Search Console 소유자 승인 완료 여부는 미확인 |
| 기능 검증 이력 | 2026-11-12 배포 `2026.1112.0052`의 시연 영상에서 실제 동의, 캘린더 추가·수정·가져오기·조건부 삭제, Drive 캐릭터 보관, 로그아웃 확인 |
| 마지막 심사 관련 기록 | 2026-11-14 Google이 개인정보처리방침의 공유 대상·보호 수단 보완을 요청한 메일을 11월 27일 사용자가 제공함. 수정 후 **같은 메일에 회신해야 심사를 계속한다**고 명시 |
| 현재 확인 상태 | 브랜딩 인증·게시는 완료. 데이터 접근 심사는 **보완 요청 대응이 필요한 상태**이며 승인 미확인. 마지막 콘솔 화면은 11월 12일의 검토 중 표시; 이후 상태는 메일 내용과 구분해서 기록 |
| 콘솔 제출 준비 | 실제 요청 범위 3개와 범위 근거, 영문 추가 정보, 시연 영상 준비 후 사용자가 제출 절차 진행. 최종 인증 센터의 검토 중 상태로 접수 확인 |
| 영상·심사 제출 | 영문 자막·개인정보 가림 영상 검토 및 YouTube 일부 공개·비로그인 접근 확인 완료. **2026-11-12 제출, 11월 14일 보완 요청, 원래 메일에 회신 필요** |

프로젝트 이름의 `production`, 공개 홈페이지, DNS 확인용 TXT만으로 Google의 프로덕션 게시·도메인 승인·앱 검수를 완료했다고 표시하지 않습니다.

## 콘솔에서 대조할 값

| 항목 | 사용할 값 |
| --- | --- |
| 프로젝트 ID | `schedulewidget-production` |
| 앱 이름 | `ScheduleWidget` |
| 앱 유형 | Windows 데스크톱 앱 |
| OAuth 클라이언트 유형 | Desktop app / 데스크톱 앱. 현재 앱에 내장된 기존 클라이언트 사용 |
| 대상 사용자·게시 상태 | External / 외부, In production / 프로덕션. 사용자 제공 콘솔 화면에서 확인 |
| 승인된 도메인 | `jwstudio.page` |
| 홈페이지 | `https://schedule.jwstudio.page/` |
| 개인정보처리방침 | `https://schedule.jwstudio.page/privacy.html` |
| 설치 파일 | `https://github.com/rnrdus5642/ScheduleWidget/releases/latest/download/ScheduleWidget-Setup.exe` |
| 사용자 지원 이메일 | `rnrdus5642@gmail.com` — 사용자 제공 콘솔 화면에서 확인 |
| 개발자 연락처 | `rnrdus5642@gmail.com` — 사용자 제공 콘솔 요약에서 확인 |
| 시연 영상 URL | 사용자 제공 일부 공개 주소를 로컬 제출 기록 `.work/oauth-video-review-20261112-01/youtube-access-check.json`에 보관 |

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
| 같은 PC에서 계정만 바꾸어 시연 | `--demo`로 별도 저장 공간을 사용. 기존 일정·토큰은 복사하지 않으며 Google 로그인과 동기화는 실제 서비스 사용 |

## 제출 전 확인표

- [x] 내장 프로젝트와 Desktop 클라이언트 유형 확인
- [x] 실제 요청 범위 3개와 권한 설명 초안 정리
- [x] 앱·홈페이지·개인정보 문서의 권한 요청 시점 맞춤
- [x] 공식 로그인 버튼 자산과 취소·로그아웃 상태 구분
- [x] 설치 방식·캐릭터 공유 범위·오프라인 재시도·삭제 설명 갱신
- [ ] 프로젝트 소유자/편집자 계정으로 콘솔 접근
- [x] Branding / Audience / Data Access / Verification Center의 현재 상태 기록 — 브랜딩 게시 완료, 외부/프로덕션, 2026-11-12 데이터 접근 심사 중
- [ ] 같은 프로젝트 관계자의 Search Console 도메인 소유권 승인 확인
- [x] 지원 이메일·개발자 연락처·앱 이름·도메인·정책 URL 대조
- [x] 브랜딩 검증·게시 완료 확인
- [x] 최신 배포와 같은 클라이언트로 새 동의 및 실제 기능 시연
- [x] [영상 기록](google-oauth-demo.md)에 따라 촬영·검토·일부 공개 업로드 — 원본 화면의 언어 관련 기록 참조
- [x] 캘린더 권한 사용 이유와 영상 링크를 입력하고 심사 제출 — 2026-11-12 사용자 제공 인증 센터 화면으로 접수 확인
- [ ] 심사 담당 이메일과 인증 센터의 추가 요청 처리
- [ ] 승인·공개 게시 상태를 확인한 뒤 새 계정과 다른 PC에서 로그인 확인

## 제출 후 확인

사용자 지원·개발자 연락처인 `rnrdus5642@gmail.com`의 수신함·스팸함과 인증 센터의 진행 상황에서 결과 및 보완 요청을 확인합니다. 이번 실제 발신자는 `api-oauth-dev-verification-reply+…@google.com` 형식이므로 `api-oauth-support@google.com`만 검색해서는 놓칠 수 있습니다. 프로젝트 ID·앱 이름·OAuth도 함께 검색합니다. 보완 요청이 오면 원래 메일에 회신하고, 승인 화면 또는 메일을 확인하기 전에는 데이터 접근 인증 완료로 기록하지 않습니다.

### 2026-11-14 보완 요청 대응

Google이 요구한 것은 개인정보처리방침의 두 항목입니다. 11월 27일 보완본은 앱의 동작이나 OAuth 범위를 바꾸지 않고, 기존 데이터 처리의 설명을 다음과 같이 명확히 합니다.

- [공유·전송·공개](https://schedule.jwstudio.page/privacy.html#google-data-sharing): Google, 사용자가 설정한 Telegram·Kakao 및 선택한 수신자, 직접 복사한 메시지, 자발적 지원 문의의 대상·조건을 한글·영문으로 구분합니다. Google Calendar에서 가져온 일정도 사용자가 켠 메시지 기능으로 전송될 수 있음을 명시합니다.
- [보호 방법](https://schedule.jwstudio.page/privacy.html#google-data-security): HTTPS/TLS, Windows DPAPI의 현재 사용자 범위 토큰 암호화, 메모리의 단기 액세스 토큰, 시스템 브라우저·PKCE S256·state 검증을 설명합니다. 일반 일정·이메일·캐릭터·백업 파일 전체를 앱이 암호화하지 않는다는 한계도 명시합니다.
- 기준 코드는 `Services/CommunicationService.cs`, `Services/GoogleCalendarService.cs`, `Services/DataManager.cs`입니다. 로컬 JSON 저장을 전체 데이터베이스 암호화로 설명하지 않습니다.
- 기존 개인정보처리방침 URL은 유지합니다. Google의 [항목별 보완 안내](https://support.google.com/cloud/answer/13806988?hl=en)는 URL이 그대로라면 수정 완료를 기존 메일에 회신하도록 설명합니다. 콘솔에 별도의 제출 작업이 표시되는 경우 실제 안내에 맞춰 진행하며, 실행하지 않은 재제출을 답장에 완료했다고 쓰지 않습니다.
- [x] 공개 방침 파일과 `PRIVACY.md`의 공유·보호 설명 보강
- [ ] 게시된 보완 내용을 확인한 뒤 원래 Google 심사 메일에 회신
- [ ] 심사 재개·추가 요청·최종 승인 확인

회신 초안과 게시 검증 결과는 로컬 `.work/google-privacy-review-20261127`에 보관합니다. 메일의 개별 회신 주소와 사례 식별자는 공개 문서에 게시하지 않습니다.

### 2026-11-25 API 요금·AI 정책 공지

이 공지는 위의 개인정보처리방침 보완 요청과 별개입니다. [Google의 사용 등급 안내](https://developers.google.com/workspace/guides/quota-usage-tiers)에 따르면 Standard는 기본 무과금 등급이며, Scaled 전환은 명시적 선택입니다. 2027-04-01부터 적용되는 한도·과금에 대비해 실제 프로젝트 사용량과 등급을 콘솔에서 확인해야 하며, 이 작업에서 유료 전환이나 한도 변경은 하지 않습니다.

[Workspace AI Tools Addendum](https://workspace.google.com/terms/ai-tools/)의 MCP 의무는 AI 에이전트·AI 실행 계층·모델이 사용하거나 구동하는 도구에 적용됩니다. 현재 앱의 Google 호출은 일반 데스크톱 동기화이므로 AI로 구동되는 호출로 보이지 않습니다. 다만 약관의 접근 도구 정의는 일반 소프트웨어도 포함하며, 연동 해제·접근 철회 후 취득 데이터 보관 제한도 있습니다. 현재 앱은 로그아웃 후 로컬 일정·캐릭터를 유지하므로, 2027-02-01 시행 전에 Google에서 받은 데이터의 구분·삭제와 사용자 소유 로컬 데이터 보존 흐름을 별도로 점검해야 합니다. 현재 구현이 새 약관의 모든 조건을 충족한다고 단정하거나 사용자 데이터를 자동 삭제하지 않습니다.

- [ ] Standard 등급의 실제 사용량·기존 할당량과 2027-04-01 영향 확인
- [ ] 새 약관의 적용 범위와 연동 해제 시 Google 유래 데이터 보관·삭제 흐름 점검

브랜딩 검증 결과를 게시하지 않은 채 7일이 지나면 재검증이 필요할 수 있습니다. 현재 Google 안내는 브랜딩을 게시한 뒤 데이터 액세스 심사를 진행하도록 설명합니다. [공식 제출 순서](https://developers.google.com/identity/protocols/oauth2/production-readiness/sensitive-scope-verification)

## 반복 확인

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Test-GoogleVerification.ps1 -ApplicationPath '<검수할 배포 폴더>\ScheduleWidget.exe' -ReportPath '.work/google-verification-report.json'
```

이 도구는 앱 내장 메타데이터, 공개 URL, DNS를 읽습니다. 사용자 일정·갱신 토큰을 읽거나 Google 로그인·동기화·철회·심사 제출을 수행하지 않습니다. 프로젝트 소유권과 심사 상태를 대신 확인하지 않으며, 승인 결과는 항상 콘솔에서 확인합니다.

공식 기준: [검수 요구사항](https://support.google.com/cloud/answer/13464321?hl=en), [민감한 권한 검수](https://developers.google.com/identity/protocols/oauth2/production-readiness/sensitive-scope-verification), [로그인 버튼 가이드](https://developers.google.com/identity/branding-guidelines). 설명의 기준 코드는 [GoogleCalendarService.cs](../ScheduleWidget/Services/GoogleCalendarService.cs), [MainWindow.Google.cs](../ScheduleWidget/MainWindow.Google.cs), 공개 방침은 [PRIVACY.md](../PRIVACY.md)입니다.
