#ifndef AppVersion
  #error AppVersion must be provided by scripts/build-installer.ps1
#endif
#ifndef SourceDir
  #error SourceDir must be provided by scripts/build-installer.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be provided by scripts/build-installer.ps1
#endif
#ifndef OutputBaseFilename
  #error OutputBaseFilename must be provided by scripts/build-installer.ps1
#endif

[Setup]
AppId={{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}
AppName=QingToolbox
AppPublisher=QingMo-A
AppVersion={#AppVersion}
DefaultDirName={userpf}\QingToolbox
DefaultGroupName=QingToolbox
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
UninstallDisplayName=QingToolbox
UninstallDisplayIcon={app}\QingToolbox.Shell.exe
CloseApplications=yes
CloseApplicationsFilter=QingToolbox.Shell.exe
RestartApplications=no
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\QingToolbox"; Filename: "{app}\QingToolbox.Shell.exe"; WorkingDir: "{app}"
Name: "{group}\卸载 QingToolbox - Uninstall QingToolbox"; Filename: "{uninstallexe}"
Name: "{autodesktop}\QingToolbox"; Filename: "{app}\QingToolbox.Shell.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\QingToolbox.Shell.exe"; WorkingDir: "{app}"; Description: "Run QingToolbox"; Flags: nowait postinstall skipifsilent
