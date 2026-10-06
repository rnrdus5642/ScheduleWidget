# ScheduleWidget 소개 홈페이지

기존 저장소의 `docs` 폴더를 GitHub Pages로 게시합니다. 사이트 파일은 HTML·CSS·SVG로 구성되며 별도 빌드, 외부 폰트, 자바스크립트 또는 분석 도구가 필요하지 않습니다.

- `index.html`: 앱 소개와 구글 연동의 사용 목적
- `privacy.html`: 개인정보처리방침과 영문 요약
- `assets/site.css`: 데스크톱·모바일 공통 스타일
- `assets/icon.svg`: 사이트 아이콘
- `.nojekyll`: 정적 파일을 그대로 제공
- `CNAME`: 앱 소개 홈페이지에 사용할 도메인

## GitHub Pages 설정

저장소 **Settings → Pages → Build and deployment**에서 다음을 지정합니다.

- Source: **Deploy from a branch**
- Branch: **main**
- Folder: **/docs**

사용자 지정 주소는 `https://schedule.jwstudio.page/`이며 개인정보처리방침은 `https://schedule.jwstudio.page/privacy.html`입니다. `jwstudio.page`의 기본 주소는 별도의 개인 포트폴리오에 사용할 수 있습니다. 모든 로컬 링크는 상대 경로입니다.

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

- 앱의 **설정 → 연동 → 구글**에서 로그인 전 데이터 사용 안내와 개인정보처리방침 링크를 확인할 수 있습니다. **설정 → 앱 정보**에도 홈페이지와 개인정보처리방침 링크가 있습니다.
- Google 인증 플랫폼의 앱 이름, 지원 이메일, 개발자 연락처와 공개 페이지의 앱 설명을 확인합니다. 실제로 받을 수 있는 이메일을 사용합니다.
- 앱이 요청하는 권한은 `calendar.events.owned`, `drive.file`, 계정 표시용 `userinfo.email`입니다. 캘린더는 사용자가 소유한 기본 캘린더만 동기화하며, 계정 이메일의 보조 조회에는 같은 이메일 권한으로 UserInfo API를 사용합니다. 콘솔에도 이 세 범위를 등록합니다. 기존에 받은 더 넓은 권한의 토큰은 앱 업데이트만으로 철회되지 않으므로, 검수 시에는 최신 빌드에서 새로 로그인해 동의 화면과 기능을 확인합니다. 캘린더 권한은 일정을 추가·수정해야 하므로 읽기 전용 범위로 대체할 수 없고, 기존 기본 캘린더를 동기화하므로 앱이 새로 만든 보조 캘린더용 범위로 대체할 수 없습니다.
- 시연 영상은 테스트용 일정과 캐릭터로 촬영합니다. 앱의 로그인 전 안내와 개인정보처리방침, 영어로 표시한 전체 OAuth 동의 화면, 로그인 후 계정 표시, 캘린더 일정의 추가·수정·동기화, 가져온 캐릭터의 Drive 보관 기능을 보여 줍니다. 검수할 앱과 영상의 앱 이름·OAuth 클라이언트가 같아야 합니다.
- 브랜딩 검수와 게시를 마친 뒤, 검수 센터에서 민감한 권한의 사용 이유와 시연 영상 링크를 제출합니다. 실제 계정 로그인·동기화와 Google의 승인 여부는 각각 확인해야 합니다.

검수 기준은 [Google 검수 요구사항](https://support.google.com/cloud/answer/13464321?hl=en)과 [민감한 권한 검수 안내](https://developers.google.com/identity/protocols/oauth2/production-readiness/sensitive-scope-verification)를 기준으로 확인합니다.

## 수정과 확인

앱의 정보 처리 방식이 바뀌면 저장소 루트의 `PRIVACY.md`와 `privacy.html`을 함께 갱신합니다. 홈페이지의 기능 설명도 실제 동작과 맞춰 관리합니다. 문의처는 프로젝트의 GitHub 이슈입니다.

로컬 미리보기는 프로젝트 루트에서 `python -m http.server 8000 --directory docs`를 실행한 후 `http://localhost:8000/`에서 확인할 수 있습니다. 320px 이상의 좁은 화면과 데스크톱 화면에서 레이아웃·목차·앵커 링크를 확인합니다.
