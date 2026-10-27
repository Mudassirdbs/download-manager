; Inno Setup 6 script. Built by installer\build.ps1, which passes AppVersion, Arch and SourceDir.
; Per-user install: no administrator rights, everything under the user's profile.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #error SourceDir must point at the published app folder
#endif

#if Arch == "arm64"
  #define ArchAllowed "arm64"
#else
  #define ArchAllowed "x64compatible"
#endif

[Setup]
AppId={{6C2B1F0E-6A8E-4C1B-9E1E-3D7F2A9B5C41}
AppName=DownloadManager
AppVersion={#AppVersion}
AppVerName=DownloadManager {#AppVersion}
AppPublisher=DownloadManager
DefaultDirName={localappdata}\Programs\DownloadManager
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchAllowed}
; .NET 8 supports Windows 10 1607 and later.
MinVersion=10.0.14393
OutputDir=..\publish
OutputBaseFilename=DownloadManager-Setup-{#AppVersion}-{#Arch}
SetupIconFile=..\src\DownloadManager.App\app.ico
UninstallDisplayIcon={app}\DownloadManager.exe
UninstallDisplayName=DownloadManager
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Updating while the app (or the browser's dmhost.exe) runs: close them through Restart Manager.
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "autostart"; Description: "Start DownloadManager when I sign in (needed to catch browser downloads)"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[InstallDelete]
; Files of an older version that the new one no longer ships.
Type: filesandordirs; Name: "{app}\extension"

[Icons]
Name: "{autoprograms}\DownloadManager"; Filename: "{app}\DownloadManager.exe"
Name: "{autoprograms}\DownloadManager third-party notices"; Filename: "{app}\THIRD-PARTY-NOTICES.txt"
Name: "{autodesktop}\DownloadManager"; Filename: "{app}\DownloadManager.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DownloadManager"; \
  ValueData: """{app}\DownloadManager.exe"" --background"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\DownloadManager.exe"; Description: "Open DownloadManager"; Flags: nowait postinstall skipifsilent
; Silent installs (/SILENT, /VERYSILENT) still start the tray app so the browser bridge is registered.
Filename: "{app}\DownloadManager.exe"; Parameters: "--background"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallRun]
; Removes the browser native-messaging registration and the autostart entry.
Filename: "{app}\DownloadManager.exe"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "Unregister"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
procedure StopProcess(const Name: String);
var
  ResultCode: Integer;
begin
  // Only this user's processes are reachable without elevation.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM ' + Name, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeUninstall(): Boolean;
begin
  StopProcess('DownloadManager.exe');
  StopProcess('dmhost.exe');
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Downloaded files are never touched. The download list, settings, logs and video tools are kept unless
  // the user asks to remove them.
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent() then
    if MsgBox('Also remove your download list, settings and the video tools (ffmpeg, yt-dlp) that DownloadManager installed?' + #13#10 +
              'Downloaded files are not affected.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\DownloadManager'), True, True, True);
end;
