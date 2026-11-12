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
CloseApplications=yes

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

; Uninstall leaves %LOCALAPPDATA%\AutoFixScout (session log, reports) and C:\ProgramData\AutoFixScout (driver save
; points) on purpose: a save point may still be needed to put a driver back.
