; AutoFix Scout installer (Inno Setup 6). Built by CI: see .github/workflows/build.yml.
;   ISCC.exe /DAppVersion=0.1.0 /DSourceX64=publish\win-x64 /DSourceArm64=publish\win-arm64 /DOutDir=dist installer\AutoFixScout.iss
; One setup for every Windows 10/11 PC: it installs the ARM64 build on ARM laptops and the x64 build elsewhere.
; Installed under Program Files on purpose: the elevated executor must sit where only administrators can write,
; otherwise any program could swap it before a UAC prompt (a risk with the unzipped test folder).

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef SourceX64
  #define SourceX64 "..\publish\win-x64"
#endif
#ifndef SourceArm64
  #define SourceArm64 "..\publish\win-arm64"
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif

[Setup]
AppId={{6F1C2A3E-8B4D-4E7A-9C51-2D0B7E4F9A13}
AppName=AutoFix Scout
AppVersion={#AppVersion}
AppVerName=AutoFix Scout {#AppVersion}
AppPublisher=AutoFix Scout
AppPublisherURL=https://github.com/ShRike3601/autofix-scout
DefaultDirName={autopf}\AutoFix Scout
DefaultGroupName=AutoFix Scout
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible or arm64
ArchitecturesInstallIn64BitMode=x64compatible or arm64
MinVersion=10.0.17763
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir={#OutDir}
OutputBaseFilename=AutoFixScout-Setup-{#AppVersion}
UninstallDisplayName=AutoFix Scout
UninstallDisplayIcon={app}\AutoFixScout.UI.exe
CloseApplications=force
RestartApplications=no
UsePreviousAppDir=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceArm64}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsArm64
Source: "{#SourceX64}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: not IsArm64

[Icons]
Name: "{group}\AutoFix Scout"; Filename: "{app}\AutoFixScout.UI.exe"
Name: "{group}\Uninstall AutoFix Scout"; Filename: "{uninstallexe}"
Name: "{autodesktop}\AutoFix Scout"; Filename: "{app}\AutoFixScout.UI.exe"; Tasks: desktopicon

[Run]
; runasoriginaluser: the app itself must never run elevated (only the executor does, per action, with a UAC prompt).
Filename: "{app}\AutoFixScout.UI.exe"; Description: "Start AutoFix Scout now"; Flags: nowait postinstall skipifsilent runasoriginaluser
; After a silent update (/RELAUNCH=1 is passed by the app's own "Update and restart") the new version starts again.
Filename: "{app}\AutoFixScout.UI.exe"; Flags: nowait runasoriginaluser; Check: RelaunchRequested

; An interactive uninstall asks whether to delete %LOCALAPPDATA%\AutoFixScout (session log, reports) and C:\ProgramData\AutoFixScout
; (driver save points); the answer is No by default. A silent uninstall (an update) always keeps both.

[Code]
// One copy only: a newer setup first removes the installed version silently (same AppId, same folder,
// one Start menu entry), then installs. User data in %LOCALAPPDATA% and C:\ProgramData is kept by the uninstaller.
const
  UninstKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F1C2A3E-8B4D-4E7A-9C51-2D0B7E4F9A13}_is1';

function InstalledValue(Name: String): String;
begin
  Result := '';
  if not RegQueryStringValue(HKLM, UninstKey, Name, Result) then
    if not RegQueryStringValue(HKCU, UninstKey, Name, Result) then
      Result := '';
end;

// Compares "1.2.3" or "1.2.3-dev" by the numeric part; returns <0, 0, >0.
function CompareVersions(A, B: String): Integer;
var PA, PB: Integer; SA, SB: String;
begin
  Result := 0;
  if Pos('-', A) > 0 then A := Copy(A, 1, Pos('-', A) - 1);
  if Pos('-', B) > 0 then B := Copy(B, 1, Pos('-', B) - 1);
  while (Result = 0) and ((A <> '') or (B <> '')) do
  begin
    PA := Pos('.', A); PB := Pos('.', B);
    if PA > 0 then begin SA := Copy(A, 1, PA - 1); Delete(A, 1, PA); end else begin SA := A; A := ''; end;
    if PB > 0 then begin SB := Copy(B, 1, PB - 1); Delete(B, 1, PB); end else begin SB := B; B := ''; end;
    Result := StrToIntDef(SA, 0) - StrToIntDef(SB, 0);
  end;
end;

function InitializeSetup(): Boolean;
var Old: String;
begin
  Result := True;
  Old := InstalledValue('DisplayVersion');
  if (Old <> '') and (CompareVersions('{#AppVersion}', Old) < 0) then
    Result := SuppressibleMsgBox('A newer version of AutoFix Scout (' + Old + ') is already installed. ' +
      'Do you want to replace it with the older version {#AppVersion}?', mbConfirmation, MB_YESNO, IDNO) = IDYES;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Uninst: String; Code: Integer;
begin
  Result := '';
  Uninst := RemoveQuotes(InstalledValue('UninstallString'));
  if (Uninst <> '') and FileExists(Uninst) then
    if not Exec(Uninst, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      Log('Previous version could not be removed first (code ' + IntToStr(Code) + '); installing over it.');
end;

// Relaunch after a silent update: only when the app itself started this setup with /RELAUNCH=1.
function RelaunchRequested: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

// Interactive uninstall only: ask whether history, reports and backups should go too. A silent uninstall is what
// an update does first, and it must keep that data.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    if MsgBox('Also delete AutoFix Scout''s history, reports and saved backups?' + #13#10 + #13#10 +
              'The saved backups are needed to put drivers and settings back. After they are deleted, changes made by AutoFix Scout can no longer be undone.',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{localappdata}\AutoFixScout'), True, True, True);
      DelTree(ExpandConstant('{commonappdata}\AutoFixScout'), True, True, True);
    end;
end;
