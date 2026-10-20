; Omebly Explorer installer (Inno Setup 6, free). One installer per processor type:
;   iscc /DArch=x64 installer\OmeblyExplorer.iss      (also arm64, x86)
; Installs for the current user only (no admin): %LOCALAPPDATA%\Programs\Omebly Explorer, a Start menu entry,
; an uninstaller in Settings > Apps. Uninstalling gives folders back to File Explorer first.
#ifndef Arch
  #define Arch "x64"
#endif
#define AppVersion "1.0.1"

[Setup]
AppId={{6E4F6B0C-0B1E-4C1D-9A55-0E5B0A11E0E1}
AppName=Omebly Explorer
AppVersion={#AppVersion}
AppPublisher=Omebly
AppPublisherURL=https://omebly.com/tools/explorer/
DefaultDirName={localappdata}\Programs\Omebly Explorer
DefaultGroupName=Omebly Explorer
PrivilegesRequired=lowest
OutputDir=..\publish\installers
OutputBaseFilename=OmeblyExplorer-Setup-{#Arch}
SetupIconFile=..\src\OmeblyExplorer\Assets\OmeblyExplorer.ico
UninstallDisplayIcon={app}\OmeblyExplorer.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
#if Arch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#elif Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#endif
MinVersion=10.0.17763

[Files]
Source: "..\publish\{#Arch}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Omebly Explorer"; Filename: "{app}\OmeblyExplorer.exe"
Name: "{autodesktop}\Omebly Explorer"; Filename: "{app}\OmeblyExplorer.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Put Omebly Explorer on the desktop"; Flags: unchecked

[Run]
Filename: "{app}\OmeblyExplorer.exe"; Description: "Open Omebly Explorer"; Flags: nowait postinstall skipifsilent
; After a one-click update (a quiet install), the new version opens by itself.
Filename: "{app}\OmeblyExplorer.exe"; Flags: nowait skipifnotsilent

[UninstallRun]
; Folders and Win+E go back to File Explorer before the files are removed.
Filename: "{app}\OmeblyExplorer.exe"; Parameters: "--give-back"; Flags: waituntilterminated runhidden; RunOnceId: "GiveBack"
