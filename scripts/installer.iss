; ZenLoop — Inno Setup 6 installer
; Build after publish.ps1 (see docs/PACKAGING.md and scripts/pack-installer.ps1).
; SignTool= omitted here; optional Authenticode via scripts/sign-artifacts.ps1 when SIGNING_CERT_* set.

#ifndef MyAppVersion
  #define MyAppVersion "1.2.0"
#endif

#define MyAppName "ZenLoop"
#define MyAppPublisher "ZenLoop contributors"
#define MyAppURL "https://github.com/daschill/zenloop"
#define MyAppExeName "ZenLoop.exe"

[Setup]
AppId={{A8E7C2D1-4B5F-4E9A-9C31-ZENLOOP000001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
LicenseFile=..\EULA.txt
InfoBeforeFile=..\DISCLAIMER.txt
OutputDir=..\dist
OutputBaseFilename=ZenLoop-{#MyAppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
; Unsigned by design for open builds — SignTool= is intentionally omitted.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Expect publish.ps1 output at dist\ZenLoop\
Source: "..\dist\ZenLoop\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Recovery guide"; Filename: "{app}\RECOVERY.md"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
  MsgBox('ZenLoop requires AMD Software (Adrenalin) and AMD Ryzen Master on this PC. ' +
    'Builds are unsigned unless you add your own certificate. ' +
    'Overclocking can damage hardware — read DISCLAIMER.txt.',
    mbInformation, MB_OK);
end;
