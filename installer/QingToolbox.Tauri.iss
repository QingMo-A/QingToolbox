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
CloseApplications=no
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
english.UpdateCloseFailed=QingToolbox is still running. Exit it completely from the tray, then retry the installation.
chinesesimplified.UpdateCloseFailed=QingToolbox 仍在运行。请从托盘中完全退出后重试安装。
english.UpdateCloseCheckFailed=Setup could not verify that the installed QingToolbox has exited. No files were changed.
chinesesimplified.UpdateCloseCheckFailed=安装程序无法确认已安装的 QingToolbox 是否退出，因此尚未修改任何文件。

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:AdditionalShortcuts}"; Flags: unchecked

[Files]
; The validated host Release contains no official modules. Each module is
; independently installed and updated from its own .qmod package.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; PrepareToInstall first migrates modules from the bundled 0.3.0-alpha host.
; Then remove the old host-owned resource tree so no bundled copy survives.
Type: filesandordirs; Name: "{app}\resources"

[Registry]
; This marker is deliberately separate from the fixed Inno uninstall record:
; the Rust host requires both records plus its production manifest to agree
; before installing a downloaded update. The authenticated shutdown channel
; separately verifies the registered location/identity so an interrupted update
; can still be repaired. Portable/debug and legacy WPF copies are not eligible.
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
Filename: "{app}\QingToolbox.exe"; WorkingDir: "{app}"; Flags: nowait; Check: ShouldRelaunchAfterUpdate

[Code]
const
  ProductUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}_is1';
  TauriMarkerKey = 'Software\QingMo-A\QingToolbox\Tauri';
  UpdateHandoffTokenValue = 'UpdateHandoffToken';
  UpdateHandoffArgumentPrefix = '--qing-update-shutdown=';
  ProcessSynchronize = $00100000;
  ProcessQueryLimitedInformation = $00001000;
  ToolhelpSnapshotProcess = $00000002;
  InvalidHandleValue = -1;
  MaxProcessPath = 32768;
  WaitObject0 = $00000000;
  InAppExitTimeoutMs = 30000;
  ManualExitTimeoutMs = 15000;
  LegacyExitGraceMs = 5000;
type
  TProcessEntry32 = record
    dwSize: DWORD;
    cntUsage: DWORD;
    th32ProcessID: DWORD;
    th32DefaultHeapID: DWORD;
    th32ModuleID: DWORD;
    cntThreads: DWORD;
    th32ParentProcessID: DWORD;
    pcPriClassBase: LongInt;
    dwFlags: DWORD;
    szExeFile: array[0..259] of Char;
  end;
var
  LegacyInstallDir: String;
  BackupDir: String;
  LegacyMigrationPending: Boolean;

function GetFileAttributesW(const FileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function OpenProcess(dwDesiredAccess: DWORD; bInheritHandle: BOOL;
  dwProcessId: DWORD): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(hHandle: THandle; dwMilliseconds: DWORD): DWORD;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';
function CreateToolhelp32Snapshot(dwFlags, th32ProcessID: DWORD): THandle;
  external 'CreateToolhelp32Snapshot@kernel32.dll stdcall';
function Process32FirstW(hSnapshot: THandle; var lppe: TProcessEntry32): BOOL;
  external 'Process32FirstW@kernel32.dll stdcall';
function Process32NextW(hSnapshot: THandle; var lppe: TProcessEntry32): BOOL;
  external 'Process32NextW@kernel32.dll stdcall';
function QueryFullProcessImageNameW(hProcess: THandle; dwFlags: DWORD;
  lpExeName: String; var lpdwSize: DWORD): BOOL;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';

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

function ShouldRelaunchAfterUpdate(): Boolean;
begin
  Result := WizardSilent and
    (CompareText(ExpandConstant('{param:QINGRELAUNCH|0}'), '1') = 0);
end;

function IsHexDigit(const Character: Char): Boolean;
begin
  Result := ((Character >= '0') and (Character <= '9')) or
    ((Character >= 'a') and (Character <= 'f')) or
    ((Character >= 'A') and (Character <= 'F'));
end;

function IsValidUpdateHandoffToken(const Token: String): Boolean;
var
  Index: Integer;
begin
  Result := Length(Token) = 64;
  if not Result then exit;
  for Index := 1 to Length(Token) do
    if not IsHexDigit(Token[Index]) then begin
      Result := False;
      exit;
    end;
end;

function ProcessEntryName(const Entry: TProcessEntry32): String;
var
  Index: Integer;
begin
  Result := '';
  for Index := 0 to 259 do begin
    if Entry.szExeFile[Index] = #0 then exit;
    Result := Result + Entry.szExeFile[Index];
  end;
end;

function TryFindInstalledProcess(const ProcessName: String; var ProcessId: DWORD): Boolean;
var
  Snapshot, ProcessHandle: THandle;
  Entry: TProcessEntry32;
  ExecutablePath: String;
  PathLength: DWORD;
begin
  Result := False;
  ProcessId := 0;
  Snapshot := CreateToolhelp32Snapshot(ToolhelpSnapshotProcess, 0);
  if Snapshot = InvalidHandleValue then RaiseException('Cannot inspect running processes.');
  try
    Entry.dwSize := SizeOf(Entry);
    if not Process32FirstW(Snapshot, Entry) then exit;
    repeat
      if CompareText(ProcessEntryName(Entry), ProcessName) = 0 then begin
        ProcessHandle := OpenProcess(ProcessQueryLimitedInformation, False,
          Entry.th32ProcessID);
        if ProcessHandle <> 0 then begin
          try
            SetLength(ExecutablePath, MaxProcessPath);
            PathLength := MaxProcessPath;
            if QueryFullProcessImageNameW(ProcessHandle, 0, ExecutablePath,
              PathLength) then begin
              SetLength(ExecutablePath, PathLength);
              if SameDirectory(ExtractFileDir(ExecutablePath),
                ExpandConstant('{app}')) then begin
                ProcessId := Entry.th32ProcessID;
                Result := True;
                exit;
              end;
            end;
          finally
            CloseHandle(ProcessHandle);
          end;
        end;
      end;
    until not Process32NextW(Snapshot, Entry);
  finally
    CloseHandle(Snapshot);
  end;
end;

function WaitForProcessExit(const ProcessId, TimeoutMs: DWORD): Boolean;
var
  ProcessHandle: THandle;
begin
  ProcessHandle := OpenProcess(ProcessSynchronize, False, ProcessId);
  if ProcessHandle = 0 then begin
    Result := True;
    exit;
  end;
  try
    Result := WaitForSingleObject(ProcessHandle, TimeoutMs) = WaitObject0;
  finally
    CloseHandle(ProcessHandle);
  end;
end;

function RequestInstalledHostShutdown(const ProcessId: DWORD): Boolean;
var
  Token: String;
  ExitCode: Integer;
  ProcessHandle: THandle;
begin
  Result := False;
  if not RegQueryStringValue(HKCU64, TauriMarkerKey,
    UpdateHandoffTokenValue, Token) or not IsValidUpdateHandoffToken(Token) then exit;
  ProcessHandle := OpenProcess(ProcessSynchronize, False, ProcessId);
  if ProcessHandle = 0 then begin
    Result := True;
    exit;
  end;
  try
    if not Exec(ExpandConstant('{app}\QingToolbox.exe'),
      UpdateHandoffArgumentPrefix + Token, ExpandConstant('{app}'),
      SW_HIDE, ewNoWait, ExitCode) then exit;
    Result := WaitForSingleObject(ProcessHandle, ManualExitTimeoutMs) = WaitObject0;
  finally
    CloseHandle(ProcessHandle);
  end;
  if Result then RegDeleteValue(HKCU64, TauriMarkerKey, UpdateHandoffTokenValue);
end;

function PrepareInstalledHostForOverwrite(): String;
var
  ProcessId, RequestedProcessId, LegacyProcessId: DWORD;
  RequestedProcessText: String;
  FallbackTimeout: DWORD;
begin
  Result := '';
  RequestedProcessText := ExpandConstant('{param:QINGHOSTPID|0}');
  RequestedProcessId := StrToIntDef(RequestedProcessText, 0);
  { Always find the host by its installed path first. A supplied PID is only
    a hint, never authority to close or wait on an unrelated/dev process. }
  if TryFindInstalledProcess('QingToolbox.exe', ProcessId) then begin
    if WizardSilent or (ProcessId = RequestedProcessId) then
      FallbackTimeout := InAppExitTimeoutMs
    else FallbackTimeout := LegacyExitGraceMs;
    Log(Format('Requesting graceful shutdown of installed host PID %d.', [ProcessId]));
    if not RequestInstalledHostShutdown(ProcessId) and
       not WaitForProcessExit(ProcessId, FallbackTimeout) then begin
      Result := ExpandConstant('{cm:UpdateCloseFailed}');
      exit;
    end;
    Log('The installed host exited before file replacement.');
  end;
  if TryFindInstalledProcess('QingToolbox.Shell.exe', LegacyProcessId) and
     not WaitForProcessExit(LegacyProcessId, LegacyExitGraceMs) then
    Result := ExpandConstant('{cm:UpdateCloseFailed}');
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

function IsLegacyInProcessModule(const Directory, ModuleId: String): Boolean;
var
  ManifestBytes: AnsiString;
  Manifest: String;
begin
  Result := False;
  if not LoadStringFromFile(Directory + '\module.json', ManifestBytes) then exit;
  Manifest := ManifestBytes;
  StringChangeEx(Manifest, ' ', '', True);
  StringChangeEx(Manifest, #9, '', True);
  StringChangeEx(Manifest, #13, '', True);
  StringChangeEx(Manifest, #10, '', True);
  Manifest := Lowercase(Manifest);
  Result := (Pos('"id":"' + Lowercase(ModuleId) + '"', Manifest) > 0) and
    (Pos('"runtimetype":"inprocess"', Manifest) > 0);
end;

procedure MigrateBundledModule(const ModuleId, SourceRoot, UserRoot, StageRoot,
  BackupRoot: String);
var
  Source, Destination, Stage, Backup: String;
  SourceAttributes: Cardinal;
  ReplaceLegacy: Boolean;
begin
  Source := SourceRoot + '\' + ModuleId;
  Destination := UserRoot + '\' + ModuleId;
  if not DirExists(Source) then exit;
  if not FileExists(Source + '\module.json') then
    RaiseException('Bundled module manifest is missing: ' + Source);
  ReplaceLegacy := DirExists(Destination) and
    IsLegacyInProcessModule(Destination, ModuleId);
  if DirExists(Destination) or FileExists(Destination) then
    if not ReplaceLegacy then begin
      Log('Preserving existing user-installed module: ' + ModuleId);
      exit;
    end;
  SourceAttributes := GetFileAttributesW(Source);
  if (SourceAttributes = $FFFFFFFF) or ((SourceAttributes and $400) <> 0) then
    RaiseException('Bundled module directory is unavailable or a reparse point: ' + Source);
  if ReplaceLegacy then begin
    SourceAttributes := GetFileAttributesW(Destination);
    if (SourceAttributes = $FFFFFFFF) or ((SourceAttributes and $400) <> 0) then
      RaiseException('Legacy user module directory is unavailable or a reparse point: ' + Destination);
  end;
  Stage := StageRoot + '\' + ModuleId;
  BackupTree(Source, Stage);
  if ReplaceLegacy then begin
    Backup := BackupRoot + '\' + ModuleId;
    if not ForceDirectories(BackupRoot) or not RenameFile(Destination, Backup) then
      RaiseException('Could not back up legacy WPF module: ' + ModuleId);
  end;
  if not RenameFile(Stage, Destination) then begin
    if ReplaceLegacy then RenameFile(Backup, Destination);
    RaiseException('Could not migrate bundled module to the user module directory: ' + ModuleId);
  end;
  if ReplaceLegacy then Log('Backed up legacy WPF module to: ' + Backup);
  Log('Migrated formerly bundled module to user installation: ' + ModuleId);
end;

procedure MigrateLegacyBundledModules();
var
  RegisteredLocation, SourceRoot, UserRoot, StageRoot, BackupRoot, Suffix: String;
begin
  if not RegQueryStringValue(HKCU64, 'Software\QingMo-A\QingToolbox\Tauri',
     'InstallLocation', RegisteredLocation) or
     not SameDirectory(ExpandConstant('{app}'), RegisteredLocation) then exit;
  SourceRoot := ExpandConstant('{app}\resources\modules');
  if not DirExists(SourceRoot) then exit;
  if (GetFileAttributesW(SourceRoot) and $400) <> 0 then
    RaiseException('Bundled module root is a reparse point: ' + SourceRoot);
  UserRoot := ExpandConstant('{localappdata}\QingToolbox\Modules');
  Suffix := GetDateTimeString('yyyymmdd-hhnnss', '-', ':') + '-' + IntToStr(Random(1000000));
  StageRoot := ExpandConstant('{localappdata}\QingToolbox\ModuleMigration-') + Suffix;
  BackupRoot := ExpandConstant('{localappdata}\QingToolbox-MigrationBackups\TauriModules-') + Suffix;
  if DirExists(StageRoot) or not ForceDirectories(UserRoot) or
     not ForceDirectories(StageRoot) then
    RaiseException('Could not create the user module migration directory.');
  MigrateBundledModule('qing.canary', SourceRoot, UserRoot, StageRoot, BackupRoot);
  MigrateBundledModule('qing.launcher', SourceRoot, UserRoot, StageRoot, BackupRoot);
  MigrateBundledModule('qing.pdf', SourceRoot, UserRoot, StageRoot, BackupRoot);
  MigrateBundledModule('qing.qingtransfer', SourceRoot, UserRoot, StageRoot, BackupRoot);
  MigrateBundledModule('qing.texttools', SourceRoot, UserRoot, StageRoot, BackupRoot);
  MigrateBundledModule('qing.windowtopmost', SourceRoot, UserRoot, StageRoot, BackupRoot);
  MigrateBundledModule('qing.powerguard', SourceRoot, UserRoot, StageRoot, BackupRoot);
  MigrateBundledModule('qing.screenpin', SourceRoot, UserRoot, StageRoot, BackupRoot);
  RemoveDir(StageRoot);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  SettingsFile, BackupBase, RegisteredLocation: String;
  ExitCode: Integer;
begin
  Result := '';
  try
    Result := PrepareInstalledHostForOverwrite();
    if Result <> '' then exit;
  except
    Result := ExpandConstant('{cm:UpdateCloseCheckFailed}');
    exit;
  end;
  try
    MigrateLegacyBundledModules();
  except
    Result := 'Cannot preserve bundled modules before the host-only upgrade: ' + GetExceptionMessage;
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
