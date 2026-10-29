#define AppName "Soundshark"
#define AppVersion "1.9.1"
[Setup]
AppId={{5FDE8A51-809E-4747-AF34-A235723DB4BF}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Soundshark
DefaultDirName={localappdata}\Programs\Soundshark
DefaultGroupName=Soundshark
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\outputs
OutputBaseFilename=Soundshark-Setup-x64
SetupIconFile=..\assets\Soundshark.ico
UninstallDisplayIcon={app}\Soundshark.exe
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
LicenseFile=..\docs\NOTICES.txt
[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked
[Files]
Source: "..\outputs\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Dirs]
Name: "{app}\Library"; Flags: uninsneveruninstall
[Icons]
Name: "{group}\Soundshark"; Filename: "{app}\Soundshark.exe"
Name: "{autodesktop}\Soundshark"; Filename: "{app}\Soundshark.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\Soundshark.exe"; Description: "Launch Soundshark"; Flags: nowait postinstall skipifsilent
; No uninstall-delete rules: exported sounds and user recovery data are preserved.
