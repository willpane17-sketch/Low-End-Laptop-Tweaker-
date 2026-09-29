; TICKSHIFT installer (Inno Setup 6.3+).
; Build via build/publish.ps1, or:  ISCC /DAppVersion=1.0.0 /DSourceDir=..\artifacts\publish\win-x64 installer\Tickshift.iss
;
; Design decisions
;  * Per-machine install into Program Files (admin once, at install). This keeps Tickshift.ElevatedHelper.exe
;    in an admin-protected folder, so a standard-user process cannot replace the binary that later runs elevated.
;  * The application itself always runs as a standard user (asInvoker); the post-install launch uses runasoriginaluser.
;  * No services, scheduled tasks, drivers or hidden autostart entries are installed.
;  * Uninstall removes the program and TICKSHIFT's own HKCU Run value, and offers (default: No) to delete its data folder.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif
#define AppName "TICKSHIFT"
#define AppExe "Tickshift.exe"

[Setup]
AppId={{6F3C2B1A-8D4E-4C7B-9A21-5E0D7C9B4F13}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=TICKSHIFT
AppComments=Performance, when every second matters.
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installer
OutputBaseFilename=TICKSHIFT-Setup-{#AppVersion}-x64
SetupIconFile=..\assets\tickshift.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
; Authenticode signing is configured by build/publish.ps1 through /S (see docs/SIGNING.md).
#ifdef Signed
SignTool=tickshift
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
function InitializeUninstall(): Boolean;
begin
  Result := MsgBox('Before uninstalling, you can restore every setting TICKSHIFT changed:' + #13#10 +
    'open TICKSHIFT › Recovery Centre › "Restore everything".' + #13#10#13#10 +
    'Continue uninstalling now?', mbConfirmation, MB_YESNO) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { Remove only TICKSHIFT's own "Launch with Windows" value; nothing else in the Run key is touched. }
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'TICKSHIFT');

    DataDir := ExpandConstant('{localappdata}\Tickshift');
    if DirExists(DataDir) then
      if MsgBox('Also delete TICKSHIFT''s settings, history and recovery records?' + #13#10 +
                '(' + DataDir + ')' + #13#10#13#10 +
                'Keep them if you might reinstall and want to undo earlier changes.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
