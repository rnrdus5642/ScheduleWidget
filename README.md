# 📅 ScheduleWidget

Windows 바탕화면에 붙어 있는 D-day 일정 위젯입니다. 일반 프로그램 창처럼 다른 앱 위에 올라가지 않으며, `Win + D`로 바탕화면을 표시해도 위젯이 유지됩니다.

## 주요 기능

- 일정 추가, 수정, 삭제
- D-day 자동 계산 및 일정 날짜순 정렬
- 오늘, 미래, 지난 일정 상태별 색상 표시
- 연도 직접 입력 지원 (`DateTime` 기준 1~9999년)
- 달력으로 날짜 선택
- 테마, 색상, 투명도, 글꼴 크기 설정
- 이동/크기 조절 모드
- 시스템 트레이 메뉴를 통한 다시 표시 및 종료
- 중복 실행 방지
- 모니터 변경 및 DPI 변경 시 바탕화면 위치 재조정

## 바탕화면 표시 방식

위젯은 항상 위(`Topmost`) 창으로 만들지 않고, Windows 바탕화면 호스트(`WorkerW`/`Progman`)에 연결합니다. 따라서 일반 프로그램 창보다 뒤에 표시되고, `Win + D`를 사용해도 바탕화면 구성 요소로 남아 있습니다.

모니터 구성이 바뀌면 바탕화면 호스트를 다시 찾고, 현재 작업 영역 안에 들어오도록 위치와 크기를 보정합니다. DPI가 다른 모니터 사이를 이동할 때는 좌표 단위를 변환합니다.

## 데이터 저장

일정과 창 상태는 다음 위치에 저장됩니다.

```text
%LocalAppData%\ScheduleWidget\schedules.json
```

저장 중 문제가 생길 경우 `schedules.json.bak` 백업을 사용해 복구합니다. 손상된 파일은 `.corrupt.*` 이름으로 보존합니다.

구버전 실행 파일 폴더에 남아 있는 `schedules.json`은 새 데이터 파일과 백업 파일이 모두 없을 때만 자동으로 이전합니다. 새 파일을 저장하고 다시 읽어 검증한 뒤에만 구버전 파일을 삭제하며, 삭제에 실패하면 원본을 보존하고 경고합니다.

## 사용 방법

1. 제목과 날짜를 입력하고 `추가`를 누릅니다.
2. 일정 카드를 우클릭하면 수정 또는 삭제할 수 있습니다.
3. 상단 토글을 켜면 위젯을 드래그하거나 크기를 조절할 수 있습니다.
4. 프로그램을 종료하려면 시스템 트레이 아이콘의 `종료` 메뉴를 사용합니다.

## 개발 환경

- C# / WPF
- .NET Framework 4.7.2
- Win32 API (`user32.dll`)
- Newtonsoft.Json

## 빌드

Visual Studio 또는 다음 명령으로 빌드할 수 있습니다.

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' `
  ScheduleWidget.sln /t:Build /p:Configuration=Release /p:Platform='Any CPU'
```

배포 전에는 모든 수정사항이 반영된 Release 빌드를 새로 생성해야 합니다.
