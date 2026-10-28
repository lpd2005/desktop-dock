#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef Payload
  #error Payload is required
#endif
#ifndef Output
  #error Output is required
#endif

[Setup]
AppId={{1F94CBEF-8E1A-4A28-B1A1-9FC24B81D401}
AppName=Desktop Dock
AppVersion={#AppVersion}
AppPublisher=lpd2005
AppPublisherURL=https://github.com/lpd2005/desktop-dock
AppSupportURL=https://github.com/lpd2005/desktop-dock/issues
AppUpdatesURL=https://github.com/lpd2005/desktop-dock/releases
DefaultDirName={localappdata}\Programs\DesktopDock
DefaultGroupName=Desktop Dock
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22621
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\DesktopDock.exe
SetupIconFile=..\src\DesktopDock.App\Assets\Brand.ico
OutputDir={#Output}
OutputBaseFilename=DesktopDock-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=no

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Desktop Dock"; Filename: "{app}\DesktopDock.exe"
Name: "{group}\Desktop Dock 设置"; Filename: "{app}\DesktopDock.exe"; Parameters: "--settings"
Name: "{group}\使用说明"; Filename: "{app}\使用说明.txt"
Name: "{autodesktop}\Desktop Dock"; Filename: "{app}\DesktopDock.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\DesktopDock"; ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\DesktopDock.exe"; Parameters: "--updates"; Description: "启动 Desktop Dock 并设置开机自启"; Flags: nowait postinstall skipifsilent

[Code]
const RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function StopDock: Boolean;
var ExitCode, Attempt: Integer;
    Window: HWND;
    AppExe: String;
begin
  AppExe := ExpandConstant('{app}\DesktopDock.exe');
  Window := FindWindowByWindowName('Desktop Dock');
  if Window <> 0 then PostMessage(Window, $0010, 0, 0);
  for Attempt := 1 to 100 do begin
    if not CheckForMutexes('Local\DesktopDock.SingleInstance') then Break;
    Sleep(100);
  end;
  if CheckForMutexes('Local\DesktopDock.SingleInstance') then begin
    Result := False;
    Exit;
  end;
  if FileExists(AppExe) then begin
    Exec(AppExe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
    for Attempt := 1 to 100 do begin
      if not CheckForMutexes('Local\DesktopDock.SingleInstance') then Break;
      Sleep(100);
    end;
    if CheckForMutexes('Local\DesktopDock.SingleInstance') then begin
      Result := False;
      Exit;
    end;
    Exec(AppExe, '--recover', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  end;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not StopDock then Result := '请退出 Desktop Dock 后重试，程序将保留现有设置。';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Existing: String;
begin
  if CurStep = ssPostInstall then begin
    { Preserve the user's enabled/disabled choice; only migrate an existing entry. }
    if RegQueryStringValue(HKCU, RunKey, 'DesktopDock', Existing) and
       (Pos('\desktopdock.exe" --startup', Lowercase(Existing)) > 0) then
      RegWriteStringValue(HKCU, RunKey, 'DesktopDock', '"' + ExpandConstant('{app}\DesktopDock.exe') + '" --startup');
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := StopDock;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Existing: String;
begin
  if CurUninstallStep = usUninstall then begin
    if RegQueryStringValue(HKCU, RunKey, 'DesktopDock', Existing) and
       (CompareText(Existing, '"' + ExpandConstant('{app}\DesktopDock.exe') + '" --startup') = 0) then
      RegDeleteValue(HKCU, RunKey, 'DesktopDock');
    { User configuration is deliberately retained outside the installation directory. }
  end;
end;
