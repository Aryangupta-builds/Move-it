; Inno Setup 6.3+ script - builds MOVE-IT-Setup.exe
#define AppVer "1.4.0"
#ifndef PublishDir
  #define PublishDir "publish"
#endif

[Setup]
; AppId stays "MoveIt" so version 1.2 installs are upgraded, not duplicated
AppId=MoveIt
AppName=MOVE IT
AppVersion={#AppVer}
AppVerName=MOVE IT {#AppVer}
VersionInfoVersion={#AppVer}
AppPublisher=Aryan Gupta
DefaultDirName={autopf}\MOVE IT
DisableWelcomePage=no
DisableProgramGroupPage=yes
InfoBeforeFile=install-notes.txt
OutputBaseFilename=MOVE-IT-Setup
OutputDir=installer-out
SetupIconFile=Assets\logo.ico
WizardImageFile=Assets\wizard.bmp
WizardSmallImageFile=Assets\wizard-small.bmp
UninstallDisplayIcon={app}\MoveIt.exe
UninstallDisplayName=MOVE IT
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
UsedUserAreasWarning=no
CloseApplications=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"
Name: "startmenu"; Description: "Create a Start menu shortcut"
Name: "startup"; Description: "Start MOVE IT when I sign in to Windows"; Flags: unchecked

[InstallDelete]
; shortcuts left by 1.2
Type: files; Name: "{autoprograms}\MoveIt\MoveIt.lnk"
Type: dirifempty; Name: "{autoprograms}\MoveIt"
Type: files; Name: "{autodesktop}\MoveIt.lnk"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\MOVE IT"; Filename: "{app}\MoveIt.exe"; Tasks: startmenu
Name: "{autodesktop}\MOVE IT"; Filename: "{app}\MoveIt.exe"; Tasks: desktopicon

[Run]
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""MoveIt"""; Flags: runhidden; Check: IsAdminInstallMode
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""MoveIt"" dir=in action=allow program=""{app}\MoveIt.exe"" profile=private enable=yes"; Flags: runhidden; Check: IsAdminInstallMode
; one fixed value name = no duplicate startup entries; written for the signed-in user
Filename: "reg.exe"; Parameters: "add HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v MoveIt /t REG_SZ /d ""\""{app}\MoveIt.exe\"" --startup"" /f"; Flags: runhidden runasoriginaluser; Tasks: startup
Filename: "reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v MoveIt /f"; Flags: runhidden runasoriginaluser; Tasks: not startup
Filename: "{app}\MoveIt.exe"; Description: "Launch MOVE IT"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""MoveIt"""; Flags: runhidden; RunOnceId: "fw"; Check: IsAdminInstallMode

[Code]
procedure CurUninstallStepChanged(Step: TUninstallStep);
var D: String;
begin
  if Step <> usPostUninstall then exit;
  RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MoveIt');
  D := ExpandConstant('{localappdata}\MoveIt');
  if DirExists(D) and (SuppressibleMsgBox('Also delete MOVE IT settings and transfer history?' + #13#10 +
     'Your received files are not deleted.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES) then
  begin
    { only these files: the Incoming folder can still hold received files }
    DeleteFile(D + '\settings.json');
    DeleteFile(D + '\history.json');
    DeleteFile(D + '\crash.log');
    RemoveDir(D);
  end;
end;
