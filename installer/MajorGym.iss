; Inno Setup script - builds MajorGym-Setup.exe (the Windows equivalent of the Android APK).
; Built by GitHub Actions (.github/workflows/windows-build.yml). Expects the self-contained publish folder
; in ..\publish (so the PC does NOT need .NET installed).

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppName "Major Gym"
#define MyAppExe "MajorGym.exe"

[Setup]
AppId={{8F6B3C1A-5D3E-4B7A-9C21-6A1E2F4D7B90}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Major Gym
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\installer-output
OutputBaseFilename=MajorGym-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExe}
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"
Name: "scannerdriver"; Description: "Install the fingerprint scanner driver (needed once on each PC that uses the scanner)"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\Drivers\vc_redist.x64.exe"; Parameters: "/install /passive /norestart"; StatusMsg: "Installing Microsoft Visual C++ runtime..."; Flags: waituntilterminated skipifdoesntexist; Tasks: scannerdriver; Check: VCRedistNeeded
Filename: "{app}\Drivers\SgDrvSetupUniversal.exe"; StatusMsg: "Installing SecuGen scanner driver (follow its prompts)..."; Flags: waituntilterminated skipifdoesntexist; Tasks: scannerdriver
Filename: "{app}\{#MyAppExe}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
function VCRedistNeeded: Boolean;
begin
  Result := (not FileExists(ExpandConstant('{sys}\vcruntime140.dll'))) or
            (not FileExists(ExpandConstant('{sys}\msvcp140.dll')));
end;
