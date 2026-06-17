#define AppName "TrainDeck Bridge"
#ifndef AppVersion
#define AppVersion "0.1.0"
#endif
#ifndef SourceDir
#define SourceDir "..\dist\release\TrainDeckBridge-win-x64"
#endif
#ifndef OutputDir
#define OutputDir "..\dist\release\packages"
#endif

[Setup]
AppId={{A4CDBED7-5668-4B5E-9B7D-75B8E4D43F90}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Maple Vibe Inc.
AppPublisherURL=https://github.com/ShaneioCantrai/TrainDeck
AppSupportURL=https://github.com/ShaneioCantrai/TrainDeck/issues
AppUpdatesURL=https://github.com/ShaneioCantrai/TrainDeck/releases
DefaultDirName={localappdata}\Programs\TrainDeck Bridge
DefaultGroupName=TrainDeck
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=TrainDeckBridgeSetup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\TrainDeck.BridgeApp.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\TrainDeck Bridge"; Filename: "{app}\TrainDeck.BridgeApp.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall TrainDeck Bridge"; Filename: "{uninstallexe}"
Name: "{autodesktop}\TrainDeck Bridge"; Filename: "{app}\TrainDeck.BridgeApp.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\TrainDeck.BridgeApp.exe"; Description: "Launch TrainDeck Bridge"; Flags: nowait postinstall skipifsilent
