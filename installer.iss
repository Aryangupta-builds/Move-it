; Inno Setup script - builds MoveIt-Setup.exe (real installer with Start Menu + Desktop shortcut)
[Setup]
AppName=MoveIt
AppVersion=1.1
AppPublisher=Aryan Gupta
DefaultDirName={autopf}\MoveIt
DefaultGroupName=MoveIt
OutputBaseFilename=MoveIt-Setup
OutputDir=installer-out
SetupIconFile=Assets\logo.ico
UninstallDisplayIcon={app}\MoveIt.exe
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: checkedonce

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\MoveIt"; Filename: "{app}\MoveIt.exe"
Name: "{autodesktop}\MoveIt"; Filename: "{app}\MoveIt.exe"; Tasks: desktopicon

[Run]
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""MoveIt"" dir=in action=allow program=""{app}\MoveIt.exe"" profile=private enable=yes"; Flags: runhidden
Filename: "{app}\MoveIt.exe"; Description: "Launch MoveIt"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""MoveIt"""; Flags: runhidden
