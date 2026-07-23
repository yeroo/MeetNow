; MeetNow installer — native Win32 app (native\x64\Release\MeetNow.exe).
; Per-user install, no admin, no HKLM, no registry Run key (Startup-folder
; shortcut instead) — matching the repo's IT constraints.
;
; Upgrade compatibility:
;  - 1.0.0 shipped as a 7z SFX wrapping an MSI (VS installer project). If
;    that product is present, it is uninstalled via msiexec /x first.
;  - This and all future Inno versions share AppId, so they upgrade in
;    place over each other.

#define MyAppVersion "2.1.1"
#define MyAppExe "MeetNow.exe"
; ProductCode of the 1.0.0 MSI (extracted from the shipped Installer.exe).
#define OldMsiProductCode "{8C041E07-9421-4959-870E-EEC892DF1FB6}"

[Setup]
AppId={{D092D475-EAE6-4751-A320-25E1A0FC5EC2}
AppName=MeetNow
AppVersion={#MyAppVersion}
AppPublisher=Boris Kudriashov
DefaultDirName={userpf}\MeetNow
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=MeetNow-Setup-{#MyAppVersion}
SetupIconFile=..\native\app\app.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; No AppMutex on purpose: its close-the-app prompt runs BEFORE
; PrepareToInstall, and /SUPPRESSMSGBOXES answers it with Cancel — so
; silent upgrades over a running app abort with exit code 1 before the
; taskkill below can run. PrepareToInstall closes the app instead.
UninstallDisplayIcon={app}\{#MyAppExe}

[Tasks]
Name: "startup"; Description: "Start MeetNow when you sign in to Windows"

[Files]
Source: "..\native\x64\Release\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\MeetNow"; Filename: "{app}\{#MyAppExe}"
Name: "{userstartup}\MeetNow"; Filename: "{app}\{#MyAppExe}"; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Launch MeetNow"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /F /IM {#MyAppExe}"; Flags: runhidden; RunOnceId: "KillApp"

[Code]
// True when the 1.0.0 MSI product is registered (per-user or per-machine).
function OldMsiInstalled(): Boolean;
begin
  Result :=
    RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#OldMsiProductCode}') or
    RegKeyExists(HKLM, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#OldMsiProductCode}') or
    RegKeyExists(HKLM, 'Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{#OldMsiProductCode}');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  // Both the C# and native app use the exe name MeetNow.exe; make sure no
  // instance holds the mutex or locks files. Failure is fine (not running).
  Exec(ExpandConstant('{cmd}'), '/C taskkill /F /IM {#MyAppExe}', '',
       SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if OldMsiInstalled() then
  begin
    // Remove the 1.0.0 MSI quietly; ignore failures so a broken old
    // install can't block the new one (files land in a different folder).
    Exec('msiexec.exe', '/x {#OldMsiProductCode} /qn /norestart', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
