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
; Keep the existing QingToolbox product identity for clean installs, WPF
; upgrades and subsequent Tauri updates. The doubled brace is Inno's literal
; GUID syntax.
AppId={{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}
AppName=QingToolbox
AppPublisher=QingMo-A
AppVersion={#AppVersion}
#ifdef LegacyUpgradeTest
AppVerName=QingToolbox {#AppVersion} (Legacy upgrade test - unsigned)
#else
AppVerName=QingToolbox {#AppVersion} (Tauri Alpha - unsigned)
#endif
AppPublisherURL=https://github.com/QingMo-A/QingToolbox
AppSupportURL=https://github.com/QingMo-A/QingToolbox/issues
AppUpdatesURL=https://github.com/QingMo-A/QingToolbox/releases
VersionInfoCompany=QingMo-A
VersionInfoProductName=QingToolbox
#ifdef LegacyUpgradeTest
VersionInfoDescription=QingToolbox unsigned legacy WPF upgrade test
#else
VersionInfoDescription=QingToolbox Rust/Tauri Alpha
#endif
VersionInfoVersion={#FileVersion}
#ifdef LegacyUpgradeTest
DefaultDirName={code:GetLegacyUpgradeInstallDir}
DisableDirPage=yes
#else
DefaultDirName={localappdata}\Programs\QingToolbox
#endif
UsePreviousAppDir=yes
DefaultGroupName=QingToolbox
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
ShowLanguageDialog=auto
Compression=lzma2
SolidCompression=yes
UninstallDisplayName=QingToolbox
UninstallDisplayIcon={app}\QingToolbox.exe
CloseApplications=force
CloseApplicationsFilter=QingToolbox.exe,qing-*-module.exe,QingToolbox.Shell.exe,QingToolbox.ModuleHost.exe,QingToolbox.StartupMaintenance.exe
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
#ifdef LegacyUpgradeTest
english.RunQingToolbox=Run QingToolbox (legacy upgrade test)
chinesesimplified.RunQingToolbox=运行 QingToolbox（旧版覆盖测试）
#else
english.RunQingToolbox=Run QingToolbox
chinesesimplified.RunQingToolbox=运行 QingToolbox
#endif

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

[Registry]
; This marker is deliberately separate from the fixed Inno uninstall record:
; the Rust host requires both records plus its production manifest to agree
; before it offers an update handoff. Portable/debug copies and the legacy WPF
; installer therefore cannot satisfy this contract.
Root: HKCU; Subkey: "Software\QingMo-A\QingToolbox\Tauri"; ValueType: string; ValueName: "InstallKind"; ValueData: "tauri-production"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\QingMo-A\QingToolbox\Tauri"; ValueType: string; ValueName: "AppId"; ValueData: "{{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}"
Root: HKCU; Subkey: "Software\QingMo-A\QingToolbox\Tauri"; ValueType: string; ValueName: "InstallerContractVersion"; ValueData: "1"
Root: HKCU; Subkey: "Software\QingMo-A\QingToolbox\Tauri"; ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"
Root: HKCU; Subkey: "Software\QingMo-A\QingToolbox\Tauri"; ValueType: string; ValueName: "InstalledVersion"; ValueData: "{#AppVersion}"
Root: HKCU; Subkey: "Software\QingMo-A\QingToolbox\Tauri"; ValueType: string; ValueName: "Distribution"; ValueData: "production"
Root: HKCU; Subkey: "Software\QingMo-A\QingToolbox\Tauri"; ValueType: string; ValueName: "Backend"; ValueData: "rust"
Root: HKCU; Subkey: "Software\QingMo-A\QingToolbox\Tauri"; ValueType: string; ValueName: "Framework"; ValueData: "tauri-2"
Root: HKCU; Subkey: "Software\QingMo-A\QingToolbox\Tauri"; ValueType: string; ValueName: "Frontend"; ValueData: "vue-3"
Root: HKCU; Subkey: "Software\QingMo-A\QingToolbox\Tauri"; ValueType: string; ValueName: "BuildProfile"; ValueData: "release"
Root: HKCU; Subkey: "Software\QingMo-A\QingToolbox\Tauri"; ValueType: string; ValueName: "ExecutableName"; ValueData: "QingToolbox.exe"
Root: HKCU; Subkey: "Software\QingMo-A\QingToolbox\Tauri"; ValueType: string; ValueName: "ManifestFileName"; ValueData: "portable-manifest.json"

[Icons]
Name: "{group}\QingToolbox"; Filename: "{app}\QingToolbox.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\QingToolbox"; Filename: "{app}\QingToolbox.exe"; WorkingDir: "{app}"; Check: ShouldUpdateDesktopShortcut

[Run]
; On the first WPF-to-Tauri upgrade, retire only the legacy host's own login
; registrations before the new host can register its preferred startup mode.
Filename: "{app}\QingToolbox.StartupMaintenance.exe"; Parameters: "--remove-owned-startup"; WorkingDir: "{app}"; Flags: runhidden waituntilterminated; Check: ShouldRunLegacyStartupCleanup
Filename: "{app}\QingToolbox.exe"; WorkingDir: "{app}"; Description: "{cm:RunQingToolbox}"; Flags: nowait postinstall skipifsilent

[Code]
const
  ProductUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}_is1';
var
  LegacyInstallDir: String;
  BackupDir: String;
  LegacyMigrationPending: Boolean;

function SameDirectory(const A, B: String): Boolean;
begin
  Result := CompareText(RemoveBackslashUnlessRoot(A), RemoveBackslashUnlessRoot(B)) = 0;
end;

function InitializeSetup(): Boolean;
var
  Location: String;
begin
  Result := True;
#ifdef LegacyUpgradeTest
  Result := False;
  if not RegQueryStringValue(HKCU64, ProductUninstallKey, 'InstallLocation', Location) then begin
    MsgBox('This upgrade-test package requires an existing per-user QingToolbox installation.', mbError, MB_OK);
    exit;
  end;
  Location := RemoveBackslashUnlessRoot(Location);
  if (Length(Location) <= 3) or (Copy(Location, 2, 2) <> ':\') then exit;
  LegacyInstallDir := RemoveBackslashUnlessRoot(ExpandFileName(Location));
  if SameDirectory(LegacyInstallDir, ExpandConstant('{win}')) or
     SameDirectory(LegacyInstallDir, ExpandConstant('{sys}')) or
     SameDirectory(LegacyInstallDir, ExpandConstant('{localappdata}')) or
     SameDirectory(LegacyInstallDir, ExpandConstant('{userappdata}')) or
     SameDirectory(LegacyInstallDir, GetEnv('USERPROFILE')) then exit;
  if not FileExists(LegacyInstallDir + '\unins000.exe') then exit;
  if not FileExists(LegacyInstallDir + '\QingToolbox.Shell.exe') then begin
    MsgBox('The registered directory does not contain the legacy WPF host. This package is only for the first WPF migration.', mbError, MB_OK);
    exit;
  end;
  Result := True;
#endif
end;

function GetLegacyUpgradeInstallDir(Param: String): String;
begin
  Result := LegacyInstallDir;
end;

function ShouldUpdateDesktopShortcut(): Boolean;
begin
  Result := WizardIsTaskSelected('desktopicon') or FileExists(ExpandConstant('{autodesktop}\QingToolbox.lnk'));
end;

function ShouldRunLegacyStartupCleanup(): Boolean;
begin
  Result := LegacyMigrationPending and
    FileExists(ExpandConstant('{app}\QingToolbox.StartupMaintenance.exe'));
end;

procedure BackupTree(const Source, Destination: String);
var
  FindRec: TFindRec;
  SourceFile, DestinationFile: String;
begin
  if not ForceDirectories(Destination) then RaiseException('Cannot create migration backup: ' + Destination);
  if FindFirst(Source + '\*', FindRec) then begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then begin
          SourceFile := Source + '\' + FindRec.Name;
          DestinationFile := Destination + '\' + FindRec.Name;
          if (FindRec.Attributes and $400) <> 0 then
            RaiseException('Migration backup refuses a reparse point: ' + SourceFile);
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            BackupTree(SourceFile, DestinationFile)
          else if not FileCopy(SourceFile, DestinationFile, True) then
            RaiseException('Migration backup failed: ' + SourceFile);
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  SettingsFile, BackupBase, RegisteredLocation: String;
  ExitCode: Integer;
  Locator, Services, Processes: Variant;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Services.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name="QingToolbox.Shell.exe" OR Name="QingToolbox.exe"');
    if Processes.Count > 0 then begin
      Result := 'Please exit QingToolbox completely (including its tray icon and development copy), then retry the upgrade.';
      exit;
    end;
  except
    Result := 'Cannot verify that QingToolbox is closed. Upgrade stopped before touching the installation.';
    exit;
  end;
  if not FileExists(ExpandConstant('{app}\QingToolbox.Shell.exe')) then exit;
  if RegKeyExists(HKCU64, 'Software\QingMo-A\QingToolbox\Tauri') then exit;
  if not RegQueryStringValue(HKCU64, ProductUninstallKey, 'InstallLocation', RegisteredLocation) or
     not SameDirectory(ExpandConstant('{app}'), RegisteredLocation) then begin
    Result := 'An old QingToolbox executable exists outside its registered installation. Migration stopped.';
    exit;
  end;
  LegacyInstallDir := RemoveBackslashUnlessRoot(ExpandFileName(RegisteredLocation));
  if BackupDir <> '' then exit;
  try
    BackupBase := ExpandConstant('{localappdata}\QingToolbox-MigrationBackups');
    if Pos(Lowercase(AddBackslash(LegacyInstallDir)), Lowercase(AddBackslash(BackupBase))) = 1 then
      RaiseException('The backup directory must be outside the installation.');
    BackupDir := BackupBase + '\' + GetDateTimeString('yyyymmdd-hhnnss', '-', ':');
    if DirExists(BackupDir) then RaiseException('Backup directory already exists. Retry after one second.');
    BackupTree(LegacyInstallDir, BackupDir + '\installation');
    SettingsFile := ExpandConstant('{userappdata}\QingToolbox\settings.json');
    if FileExists(SettingsFile) and not FileCopy(SettingsFile, BackupDir + '\settings.json', True) then
      RaiseException('Cannot back up shared settings.');
    if not Exec(ExpandConstant('{sys}\reg.exe'),
      'export "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}_is1" "' + BackupDir + '\uninstall.reg" /y',
      '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then RaiseException('Cannot export uninstall registration.');
    if ExitCode <> 0 then RaiseException('Cannot export uninstall registration.');
    if not SaveStringToFile(BackupDir + '\install-location.txt', LegacyInstallDir, False) then
      RaiseException('Cannot save migration backup metadata.');
    LegacyMigrationPending := True;
    Log('Legacy installation backup: ' + BackupDir);
  except
    Result := GetExceptionMessage;
    BackupDir := '';
  end;
end;
