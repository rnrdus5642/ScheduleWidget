# ScheduleWidget 소개 홈페이지

기존 저장소의 `docs` 폴더를 GitHub Pages로 게시합니다. 사이트 파일은 HTML·CSS·SVG로 구성되며 별도 빌드, 외부 폰트, 자바스크립트 또는 분석 도구가 필요하지 않습니다.

- `index.html`: 앱 소개와 구글 연동의 사용 목적
- `privacy.html`: 개인정보처리방침과 영문 요약
- `assets/site.css`: 데스크톱·모바일 공통 스타일
- `assets/icon.svg`: 사이트 아이콘
- `.nojekyll`: 정적 파일을 그대로 제공

## GitHub Pages 설정

저장소 **Settings → Pages → Build and deployment**에서 다음을 지정합니다.

- Source: **Deploy from a branch**
- Branch: **main**
- Folder: **/docs**

기본 주소는 `https://rnrdus5642.github.io/ScheduleWidget/`이며 개인정보처리방침은 같은 주소 아래 `privacy.html`입니다. 모든 로컬 링크는 상대 경로라서 이후 사용자 지정 주소를 연결해도 그대로 작동합니다.

구글 OAuth 게시 상태·브랜딩 검수·권한 검수는 사이트 게시와 별도입니다. 검수에 사용할 주소의 소유권을 확인한 뒤 Google 인증 플랫폼에 홈페이지와 개인정보처리방침의 최종 URL을 등록합니다. `is-a.dev`와 같은 무료 주소를 사용할 경우 서비스의 용도 제한과 등록 승인을 먼저 확인하고 Google Search Console에서 요구하는 DNS 인증값을 등록해야 합니다. 주소 등록이나 검수 승인을 사이트 게시만으로 보장하지 않습니다.

## 수정과 확인

앱의 정보 처리 방식이 바뀌면 저장소 루트의 `PRIVACY.md`와 `privacy.html`을 함께 갱신합니다. 홈페이지의 기능 설명도 실제 동작과 맞춰 관리합니다. 문의처는 프로젝트의 GitHub 이슈입니다.

로컬 미리보기는 프로젝트 루트에서 `python -m http.server 8000 --directory docs`를 실행한 후 `http://localhost:8000/`에서 확인할 수 있습니다. 320px 이상의 좁은 화면과 데스크톱 화면에서 레이아웃·목차·앵커 링크를 확인합니다.
