#ifndef AppVersion
  #error AppVersion must be provided by scripts/build-installer.ps1
#endif
#ifndef FileVersion
  #error FileVersion must be provided by scripts/build-installer.ps1
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
#ifndef BrandIconPath
  #error BrandIconPath must be provided by scripts/build-installer.ps1
#endif

[Setup]
AppId={{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}
AppName=QingToolbox
AppPublisher=QingMo-A
AppVersion={#AppVersion}
AppVerName=QingToolbox {#AppVersion} Preview
AppPublisherURL=https://github.com/QingMo-A/QingToolbox
AppSupportURL=https://github.com/QingMo-A/QingToolbox/issues
AppUpdatesURL=https://github.com/QingMo-A/QingToolbox/releases
VersionInfoCompany=QingMo-A
VersionInfoProductName=QingToolbox
VersionInfoDescription=QingToolbox Preview Installer
VersionInfoVersion={#FileVersion}
DefaultDirName={userpf}\QingToolbox
DefaultGroupName=QingToolbox
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
ShowLanguageDialog=auto
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
SetupIconFile={#BrandIconPath}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
english.DesktopShortcut=Create a desktop shortcut
chinesesimplified.DesktopShortcut=创建桌面快捷方式
english.AdditionalShortcuts=Additional shortcuts:
chinesesimplified.AdditionalShortcuts=附加快捷方式：
english.RunQingToolbox=Run QingToolbox
chinesesimplified.RunQingToolbox=运行 QingToolbox
english.UninstallQingToolbox=Uninstall QingToolbox
chinesesimplified.UninstallQingToolbox=卸载 QingToolbox

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:AdditionalShortcuts}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\QingToolbox"; Filename: "{app}\QingToolbox.Shell.exe"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallQingToolbox}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\QingToolbox"; Filename: "{app}\QingToolbox.Shell.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\QingToolbox.Shell.exe"; WorkingDir: "{app}"; Description: "{cm:RunQingToolbox}"; Flags: nowait postinstall skipifsilent
