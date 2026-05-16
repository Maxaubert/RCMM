; RCMM (Right-Click Menu Manager) — Inno Setup script
; Build:  ISCC.exe installer\RCMM.iss
; Output: dist\installer\RCMM-Setup-x64.exe

#define MyAppName        "RCMM"
#define MyAppFullName    "Right-Click Menu Manager"
#define MyAppVersion     "0.6.0"
#define MyAppPublisher   "Max"
#define MyAppExeName     "RCMM.exe"
#define MyAppId          "{{CB056509-57B8-424B-B7D2-8A75A523AC65}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppFullName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppFullName}
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppFullName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\manager\src\RCMM\Assets\app.ico
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir=..\dist\installer
OutputBaseFilename=RCMM-Setup-x64-{#MyAppVersion}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; Bring in everything the self-contained publish produced.
Source: "..\dist\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppFullName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppFullName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppFullName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppFullName}"; Flags: nowait postinstall skipifsilent runascurrentuser
