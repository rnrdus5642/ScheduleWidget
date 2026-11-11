# ScheduleWidget 소개 홈페이지

문서 기준: 2026-11-12. 앱의 사용·개발 안내는 [루트 README](../README.md), 개인정보 문서의 원본은 [PRIVACY.md](../PRIVACY.md)입니다.

기존 저장소의 `docs` 폴더를 GitHub Pages로 게시합니다. 사이트 파일은 HTML·CSS·SVG로 구성되며 별도 빌드, 외부 폰트, 자바스크립트 또는 분석 도구가 필요하지 않습니다.

- `index.html`: 기능, 캐릭터 추가 방법, 오프라인 사용과 구글 연동 소개
- `privacy.html`: 개인정보처리방침과 영문 요약
- `assets/site.css`: 데스크톱·모바일 공통 스타일
- `assets/icon.svg`: 사이트 아이콘
- `.nojekyll`: 정적 파일을 그대로 제공
- `CNAME`: 앱 소개 홈페이지에 사용할 도메인

## 다운로드 버튼

첫 화면과 시작 안내의 **Windows용 다운로드**는 `https://github.com/rnrdus5642/ScheduleWidget/releases/latest/download/ScheduleWidget-Setup.exe`로 직접 연결합니다. 방문자는 GitHub의 파일 목록을 거치지 않고 설치 EXE를 받습니다. ZIP은 보조 링크의 릴리스 페이지에서 제공합니다.

매 릴리스에는 `ScheduleWidget-Setup.exe`, 앱 업데이트 ZIP, `latest.json`이 모두 있어야 합니다. `tools/Publish-Update.ps1`가 세 파일을 생성합니다. 게시 후 직접 다운로드 주소가 새 설치 파일로 연결되는지, 공개 파일의 해시가 검증한 파일과 같은지 확인합니다. 설치 방식이나 외부 런타임 다운로드가 바뀌면 루트 README와 개인정보 문서도 함께 갱신합니다.

## GitHub Pages 설정

저장소 **Settings → Pages → Build and deployment**에서 다음을 지정합니다.

- Source: **Deploy from a branch**
- Branch: **main**
- Folder: **/docs**

사용자 지정 주소는 `https://schedule.jwstudio.page/`이며 개인정보처리방침은 `https://schedule.jwstudio.page/privacy.html`입니다. `jwstudio.page`의 기본 주소는 별도의 개인 포트폴리오에 사용할 수 있습니다. 모든 로컬 링크는 상대 경로입니다. 로컬 파일 편집은 공개 사이트를 갱신하지 않으며, 게시 브랜치로 푸시한 뒤 Pages 배포가 완료돼야 반영됩니다. 공개 게시와 릴리스 배포는 사용자가 요청한 범위에서 진행합니다.

### 도메인 연결 순서

1. 저장소 **Settings → Pages → Custom domain**에 `schedule.jwstudio.page`를 입력하고 저장합니다. `docs/CNAME` 파일만 수정하는 것으로 Pages 설정 변경을 대신할 수는 없습니다.
2. Cloudflare에서 `jwstudio.page`의 **DNS → Records**에 다음 레코드를 추가합니다.

   | 항목 | 값 |
   | --- | --- |
   | Type | `CNAME` |
   | Name | `schedule` |
   | Target | `rnrdus5642.github.io` |
   | Proxy status | **DNS only** |
   | TTL | **Auto** |

   Target에는 `https://`나 `/ScheduleWidget/` 경로를 넣지 않습니다.
3. GitHub Pages에서 DNS 확인과 인증서 발급이 완료되면 **Enforce HTTPS**를 켭니다.
4. 사용자 지정 주소에서 홈페이지, `privacy.html`, `assets/site.css`, `assets/icon.svg`가 HTTPS로 열리는지 확인합니다.

연결 전 GitHub Pages 기본 주소는 `https://rnrdus5642.github.io/ScheduleWidget/`입니다. GitHub 설정과 Cloudflare DNS를 실제로 적용하고 위 검증을 마쳐야 도메인 연결이 완료됩니다.

### Google OAuth에 사용할 주소

Google Cloud 프로젝트 소유자 계정으로 Google Search Console의 **도메인** 속성에 `jwstudio.page`를 추가하고, 안내받은 TXT 레코드를 Cloudflare DNS에 등록해 소유권을 확인합니다. 실제 HTTPS 연결을 확인한 뒤 Google 인증 플랫폼에 다음 값을 등록합니다.

| 항목 | 값 |
| --- | --- |
| 승인된 도메인 | `jwstudio.page` |
| 애플리케이션 홈페이지 | `https://schedule.jwstudio.page/` |
| 개인정보처리방침 | `https://schedule.jwstudio.page/privacy.html` |

구글 OAuth 게시 상태·브랜딩 검수·권한 검수는 사이트 게시와 별도입니다. 도메인 연결과 소유권 확인만으로 앱 검수가 완료되는 것은 아닙니다.

### 검수 준비

- 콘솔 입력값·현재 확인 범위·권한 사용 이유 영문 초안·제출 전 점검표는 [Google OAuth 검수 준비](../verification/google-oauth.md), 촬영 순서와 영문 자막은 [시연 영상 대본](../verification/google-oauth-demo.md)에 있습니다. 콘솔의 승인 상태, 담당 이메일, 영상 URL은 확인 전까지 임의로 채우지 않습니다.
- 로그인에서는 캘린더·Drive·이메일 권한을 함께 요청하며, 로그인 후 스위치는 실제 동기화를 제어합니다. 기능 스위치를 켤 때 처음 권한을 요청한다고 설명하지 않습니다. 앱은 공식 `Sign in with Google` 이미지를 사용하고 한국어 접근성 이름을 제공합니다.
- 앱의 **설정 → 연동 → 구글**에서 로그인 전 데이터 사용 안내와 개인정보처리방침 링크를 확인할 수 있습니다. **설정 → 앱 정보**에도 홈페이지와 개인정보처리방침 링크가 있습니다.
- Google 인증 플랫폼의 앱 이름, 지원 이메일, 개발자 연락처와 공개 페이지의 앱 설명을 확인합니다. 실제로 받을 수 있는 이메일을 사용합니다.
- 앱이 요청하는 권한은 `calendar.events.owned`, `drive.file`, 계정 표시용 `userinfo.email`입니다. 캘린더는 사용자가 소유한 기본 캘린더만 동기화하며, 계정 이메일의 보조 조회에는 같은 이메일 권한으로 UserInfo API를 사용합니다. 콘솔에도 이 세 범위를 등록합니다. 기존에 받은 더 넓은 권한의 토큰은 앱 업데이트만으로 철회되지 않으므로, 검수 시에는 최신 빌드에서 새로 로그인해 동의 화면과 기능을 확인합니다. 캘린더 권한은 일정을 추가·수정해야 하므로 읽기 전용 범위로 대체할 수 없고, 기존 기본 캘린더를 동기화하므로 앱이 새로 만든 보조 캘린더용 범위로 대체할 수 없습니다.
- 시연 영상은 테스트용 일정과 캐릭터로 촬영합니다. 앱의 로그인 전 안내와 개인정보처리방침, 영어로 표시한 전체 OAuth 동의 화면, 로그인 후 계정 표시, 캘린더 일정의 추가·수정·동기화, 가져온 캐릭터의 Drive 보관 기능을 보여 줍니다. 검수할 앱과 영상의 앱 이름·OAuth 클라이언트가 같아야 합니다.
- 브랜딩 검수와 게시를 마친 뒤, 검수 센터에서 민감한 권한의 사용 이유와 시연 영상 링크를 제출합니다. 실제 계정 로그인·동기화와 Google의 승인 여부는 각각 확인해야 합니다.

검수 기준은 [Google 검수 요구사항](https://support.google.com/cloud/answer/13464321?hl=en)과 [민감한 권한 검수 안내](https://developers.google.com/identity/protocols/oauth2/production-readiness/sensitive-scope-verification)를 기준으로 확인합니다.

## 수정과 확인

다음 문서를 함께 관리합니다.

| 변경 내용 | 확인할 파일 |
| --- | --- |
| 화면 이름·버튼·설정 저장 방식 | [루트 README](../README.md), `index.html` |
| 캐릭터 제작·다운로드 안내 | `CharacterHelpWindow.xaml`·`.xaml.cs`, 루트 README, `index.html` |
| 전송 정보·저장 위치·외부 사이트 | [PRIVACY.md](../PRIVACY.md), `privacy.html`의 한국어·영문 요약 |
| 구글 재시도·모드별 위치 복원 | 루트 README의 구글 연동·월간 달력·검증 항목 |
| OAuth 요청 시점·권한·데이터 흐름·로그인 화면 | 앱의 연동 안내, 루트 README·개인정보 문서, `verification/google-oauth.md`와 시연 대본 |
| 빌드·서명·릴리스 절차 | 루트 README의 빌드와 배포, 이 문서의 Pages 설정 |

앱의 정보 처리 방식이 바뀌면 저장소 루트의 `PRIVACY.md`와 `privacy.html`을 함께 갱신합니다. 홈페이지의 기능 설명도 실제 동작과 맞춰 관리합니다. 문의처는 프로젝트의 GitHub 이슈입니다.

로컬 미리보기는 프로젝트 루트에서 `python -m http.server 8000 --directory docs`를 실행한 후 `http://localhost:8000/`에서 확인할 수 있습니다. 320px 이상의 좁은 화면과 데스크톱 화면에서 레이아웃·목차·앵커 링크를 확인합니다.

- 링크와 `id` 앵커, 상대 경로의 CSS·아이콘 파일이 존재하는지 확인합니다. HTML의 제목·설명·문서 수정일과 루트 문서의 내용도 맞춥니다.
- 앱 검사는 `powershell -NoProfile -ExecutionPolicy Bypass -File tests/Run-Checks.ps1`로 실행합니다. 사이트는 별도 빌드가 없는 정적 파일이며, 코드 검사가 HTML 화면 확인을 대신하지는 않습니다.
- 구글 드라이브 소개는 `pet.json + 이미지`만 공유한다는 점과 PC별 배치 설정을 구분합니다. 로컬 일정 저장, 오프라인 자동 재시도, 캐릭터 안내의 외부 브라우저 링크도 실제 코드와 대조합니다.
- Pages 게시 위치는 [GitHub의 게시 소스 안내](https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site), Drive 권한 설명은 [Google의 Drive 범위 안내](https://developers.google.com/workspace/drive/api/guides/api-specific-auth)를 참고합니다.
