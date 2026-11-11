; Built by tools/Build-Installer.ps1 from the verified update ZIP, never from a working app folder.
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef PrerequisitesDir
  #error PrerequisitesDir is required
#endif
#ifndef ProductId
  #define ProductId "ScheduleWidget"
#endif
#ifndef RuntimeMutex
  #define RuntimeMutex "ScheduleWidget_Unique_Mutex_5642"
#endif
#ifndef StartupRegistryKey
  #define StartupRegistryKey "Software\Microsoft\Windows\CurrentVersion\Run"
#endif

[Setup]
AppId={#ProductId}
AppName=ScheduleWidget
AppVersion={#AppVersion}
AppPublisher=rnrdus5642
AppPublisherURL=https://schedule.jwstudio.page/
AppSupportURL=https://github.com/rnrdus5642/ScheduleWidget/issues
AppUpdatesURL=https://schedule.jwstudio.page/
DefaultDirName={localappdata}\Programs\ScheduleWidget
DefaultGroupName={#ProductId}
PrivilegesRequired=lowest
MinVersion=10.0
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=yes
WizardStyle=modern
SetupIconFile=..\ScheduleWidget\app.ico
UninstallDisplayIcon={app}\ScheduleWidget.exe
OutputDir={#OutputDir}
OutputBaseFilename={#ProductId}-Setup
VersionInfoVersion={#AppVersion}
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PrerequisitesDir}\MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy
Source: "{#PrerequisitesDir}\ndp48-web.exe"; Flags: dontcopy

[Icons]
Name: "{group}\ScheduleWidget"; Filename: "{app}\ScheduleWidget.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#ProductId}"; Filename: "{app}\ScheduleWidget.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\ScheduleWidget.exe"; WorkingDir: "{app}"; Description: "ScheduleWidget 실행"; Flags: nowait postinstall skipifsilent

[Code]
const
  WebViewKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  StartupKey = '{#StartupRegistryKey}';

var
  PrerequisitePage: TOutputProgressWizardPage;

function HasWebViewVersion(Root: Integer): Boolean;
var
  Version: String;
  Packed: Int64;
begin
  Result := RegQueryStringValue(Root, WebViewKey, 'pv', Version) and
    StrToVersion(Version, Packed) and (Packed > 0);
end;

function HasWebView: Boolean;
begin
  Result := HasWebViewVersion(HKCU) or HasWebViewVersion(HKLM32);
end;

procedure InitializeWizard;
begin
  PrerequisitePage := CreateOutputProgressPage('실행 환경 준비', '필요한 구성 요소를 설치하고 있습니다.');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExistingVersion: String;
  Existing, Current: Int64;
  ExitCode: Integer;
begin
  Result := '';
  if GetVersionNumbersString(ExpandConstant('{app}\ScheduleWidget.exe'), ExistingVersion) and
     StrToVersion(ExistingVersion, Existing) and StrToVersion('{#AppVersion}', Current) and (Existing > Current) then
  begin
    Result := '더 최신 버전이 이미 설치되어 있습니다. 홈페이지에서 최신 설치 파일을 받아 주세요.';
    Exit;
  end;

  if IsDotNetInstalled(net472, 0) and HasWebView then Exit;
  PrerequisitePage.Show;
  try
    if not IsDotNetInstalled(net472, 0) then
    begin
      PrerequisitePage.SetText('.NET Framework를 설치합니다.', '인터넷 연결과 Windows의 관리자 권한 확인이 필요할 수 있습니다.');
      ExtractTemporaryFile('ndp48-web.exe');
      if not ShellExec('runas', ExpandConstant('{tmp}\ndp48-web.exe'), '/passive /norestart', '',
          SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode) then
      begin
        Result := '.NET Framework 설치를 시작하지 못했습니다. Windows의 설치 확인을 허용한 뒤 다시 시도해 주세요.';
        Exit;
      end;
      if ExitCode = 3010 then
      begin
        NeedsRestart := True;
        Result := '실행 환경 준비를 마치려면 PC를 다시 시작해야 합니다. 다시 시작한 뒤 이 설치 파일을 실행해 주세요.';
        Exit;
      end;
      if (ExitCode <> 0) or not IsDotNetInstalled(net472, 0) then
      begin
        Result := '.NET Framework 설치를 마치지 못했습니다. 인터넷 연결을 확인한 뒤 다시 시도해 주세요. 오류: ' + IntToStr(ExitCode);
        Exit;
      end;
    end;
    if not HasWebView then
    begin
      PrerequisitePage.SetText('캐릭터와 음악 화면에 필요한 구성 요소를 설치합니다.', 'Microsoft Edge WebView2를 준비합니다. 인터넷 연결을 유지해 주세요.');
      ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
      if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '',
          SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      begin
        Result := 'WebView2 설치를 시작하지 못했습니다. 다시 시도해 주세요.';
        Exit;
      end;
      if (ExitCode <> 0) or not HasWebView then
        Result := 'WebView2 설치를 마치지 못했습니다. 인터넷 연결을 확인한 뒤 다시 시도해 주세요. 오류: ' + IntToStr(ExitCode);
    end;
  finally
    PrerequisitePage.Hide;
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := not CheckForMutexes('{#RuntimeMutex}');
  if not Result then
    SuppressibleMsgBox('ScheduleWidget이 실행 중입니다. 작업 표시줄 오른쪽의 일정 위젯 아이콘을 우클릭해 종료한 뒤 다시 제거해 주세요.', mbInformation, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command, AppPath: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  { Remove only this installation's startup entry. Schedule data and shared runtimes remain untouched. }
  AppPath := ExpandConstant('{app}\ScheduleWidget.exe');
  if RegQueryStringValue(HKCU, StartupKey, 'ScheduleWidget', Command) and
     ((CompareText(Trim(Command), '"' + AppPath + '"') = 0) or (CompareText(Trim(Command), AppPath) = 0)) then
    RegDeleteValue(HKCU, StartupKey, 'ScheduleWidget');
end;
