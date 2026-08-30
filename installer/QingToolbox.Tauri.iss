#ifndef AppVersion
  #error AppVersion must be provided by scripts/build-tauri-installer.ps1
#endif
#ifndef FileVersion
  #error FileVersion must be provided by scripts/build-tauri-installer.ps1
#endif
#ifndef SourceDir
  #error SourceDir must be provided by scripts/build-tauri-installer.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be provided by scripts/build-tauri-installer.ps1
#endif
#ifndef OutputBaseFilename
  #error OutputBaseFilename must be provided by scripts/build-tauri-installer.ps1
#endif
#ifndef BrandIconPath
  #error BrandIconPath must be provided by scripts/build-tauri-installer.ps1
#endif

[Setup]
; This is a migration-only application id. It intentionally does not claim
; the legacy WPF installer id until the Tauri host has completed its parity
; and signed-release audit. The doubled opening brace is the Inno syntax for
; embedding a literal GUID instead of expanding a constant.
AppId={{C9E5A4D1-1E8E-4F39-8F70-9D8D1C4B7A61}
AppName=QingToolbox Tauri
AppPublisher=QingMo-A
AppVersion={#AppVersion}
AppVerName=QingToolbox {#AppVersion} (Tauri)
AppPublisherURL=https://github.com/QingMo-A/QingToolbox
AppSupportURL=https://github.com/QingMo-A/QingToolbox/issues
AppUpdatesURL=https://github.com/QingMo-A/QingToolbox/releases
VersionInfoCompany=QingMo-A
VersionInfoProductName=QingToolbox
VersionInfoDescription=QingToolbox Rust/Tauri migration candidate
VersionInfoVersion={#FileVersion}
DefaultDirName={localappdata}\Programs\QingToolbox-Tauri
UsePreviousAppDir=yes
DefaultGroupName=QingToolbox Tauri
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
ShowLanguageDialog=auto
Compression=lzma2
SolidCompression=yes
UninstallDisplayName=QingToolbox Tauri
UninstallDisplayIcon={app}\QingToolbox.exe
CloseApplications=force
CloseApplicationsFilter=QingToolbox.exe,qing-*-module.exe
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
english.RunQingToolbox=Run QingToolbox Tauri
chinesesimplified.RunQingToolbox=运行 QingToolbox Tauri

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:AdditionalShortcuts}"; Flags: unchecked

[Files]
; The source is a validated portable Release directory. Keep the complete
; resources/modules tree so the Rust host cannot fall back to legacy modules.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; `resources` is entirely host-owned. Remove it before copying so an upgrade
; cannot retain a stale module executable or an obsolete hashed Web asset.
Type: filesandordirs; Name: "{app}\resources"

[Icons]
Name: "{group}\QingToolbox Tauri"; Filename: "{app}\QingToolbox.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\QingToolbox Tauri"; Filename: "{app}\QingToolbox.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\QingToolbox.exe"; WorkingDir: "{app}"; Description: "{cm:RunQingToolbox}"; Flags: nowait postinstall skipifsilent
