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
#ifndef ReleaseDisplayName
  #error ReleaseDisplayName must be provided by scripts/build-installer.ps1
#endif
#ifndef ObsoleteIncludePath
  #error ObsoleteIncludePath must be provided by scripts/build-installer.ps1
#endif

[Setup]
AppId={{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}
AppName=QingToolbox
AppPublisher=QingMo-A
AppVersion={#AppVersion}
AppVerName=QingToolbox {#AppVersion} {#ReleaseDisplayName}
AppPublisherURL=https://github.com/QingMo-A/QingToolbox
AppSupportURL=https://github.com/QingMo-A/QingToolbox/issues
AppUpdatesURL=https://github.com/QingMo-A/QingToolbox/releases
VersionInfoCompany=QingMo-A
VersionInfoProductName=QingToolbox
VersionInfoDescription=QingToolbox Preview Installer
VersionInfoVersion={#FileVersion}
DefaultDirName={code:GetDefaultInstallDir}
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
english.ConflictingInstallLocations=QingToolbox has conflicting valid installation records. Setup will not choose a directory automatically. Repair or remove the old installation record, then try again.
chinesesimplified.ConflictingInstallLocations=QingToolbox 存在多个冲突的有效安装记录。安装程序不会自动选择目录。请修复或移除旧安装记录后重试。

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; GroupDescription: "{cm:AdditionalShortcuts}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\QingToolbox"; Filename: "{app}\QingToolbox.Shell.exe"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallQingToolbox}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\QingToolbox"; Filename: "{app}\QingToolbox.Shell.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\QingToolbox.Shell.exe"; WorkingDir: "{app}"; Flags: nowait; Check: ShouldRestoreRunningShell
Filename: "{app}\QingToolbox.Shell.exe"; WorkingDir: "{app}"; Description: "{cm:RunQingToolbox}"; Flags: nowait postinstall skipifsilent; Check: ShouldOfferPostInstallRun

[UninstallRun]
Filename: "{app}\QingToolbox.StartupMaintenance.exe"; Parameters: "--remove-owned-startup"; WorkingDir: "{app}"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveOwnedStartup"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "QingToolbox"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\QingMo-A\QingToolbox"; ValueType: string; ValueName: "InstalledVersion"; ValueData: "{#AppVersion}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\QingMo-A\QingToolbox"; ValueType: string; ValueName: "InstalledFileVersion"; ValueData: "{#FileVersion}"
Root: HKCU; Subkey: "Software\QingMo-A\QingToolbox"; ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"
Root: HKCU; Subkey: "Software\QingMo-A\QingToolbox"; ValueType: dword; ValueName: "InstallerSchemaVersion"; ValueData: "2"

[Code]
const
  InvalidFileAttributes = $FFFFFFFF;
  FileAttributeReparsePoint = $400;
  UninstallRegistryKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}_is1';
  ProductRegistryKey = 'Software\QingMo-A\QingToolbox';

var
  ResolvedPreviousInstallDir: String;
  PreviousInstallConflict: Boolean;
  WasQingToolboxRunningBeforeInstall: Boolean;

function GetFileAttributesW(FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function SamePath(const Left, Right: String): Boolean;
begin
  Result := CompareText(RemoveBackslashUnlessRoot(Left),
    RemoveBackslashUnlessRoot(Right)) = 0;
end;

function StripPairedQuotes(const Value: String): String;
begin
  Result := Trim(Value);
  if (Length(Result) >= 2) and (Result[1] = '"') and
     (Result[Length(Result)] = '"') then
    Result := Copy(Result, 2, Length(Result) - 2);
end;

function IsUnsafeInstallRoot(const Candidate: String): Boolean;
var
  WindowsRoot, SystemRoot, ProfileRoot, TempRoot: String;
begin
  WindowsRoot := RemoveBackslashUnlessRoot(ExpandConstant('{win}'));
  SystemRoot := RemoveBackslashUnlessRoot(ExpandConstant('{sys}'));
  ProfileRoot := RemoveBackslashUnlessRoot(ExpandConstant('{userprofile}'));
  TempRoot := RemoveBackslashUnlessRoot(ExpandConstant('{tmp}'));
  Result := (Length(Candidate) <= 3) or SamePath(Candidate, WindowsRoot) or
    SamePath(Candidate, SystemRoot) or SamePath(Candidate, ProfileRoot) or
    SamePath(Candidate, TempRoot);
end;

function NormalizeAndValidateInstallDir(const Source, RawValue: String;
  var Candidate: String): Boolean;
var
  Value, ShellPath: String;
begin
  Result := False;
  Value := StripPairedQuotes(RawValue);
  if Value = '' then begin
    Log('Rejected empty install directory candidate from ' + Source + '.');
    exit;
  end;
  Value := ExpandConstant(Value);
  StringChangeEx(Value, '/', '\', True);
  if (Length(Value) < 3) or (Value[2] <> ':') or
     ((Value[3] <> '\')) or (Copy(Value, 1, 2) = '\\') then begin
    Log('Rejected non-local or non-absolute install directory candidate from ' + Source + ': ' + Value);
    exit;
  end;
  Candidate := RemoveBackslashUnlessRoot(ExpandFileName(Value));
  if IsUnsafeInstallRoot(Candidate) then begin
    Log('Rejected unsafe install directory candidate from ' + Source + ': ' + Candidate);
    exit;
  end;
  ShellPath := AddBackslash(Candidate) + 'QingToolbox.Shell.exe';
  if (not DirExists(Candidate)) or (not FileExists(ShellPath)) then begin
    Log('Rejected missing or incomplete install directory candidate from ' + Source + ': ' + Candidate);
    exit;
  end;
  Log('Accepted install directory candidate from ' + Source + ': ' + Candidate);
  Result := True;
end;

procedure AddInstallDirCandidate(const Source, RawValue: String;
  var Selected: String; var Conflict: Boolean);
var
  Candidate: String;
begin
  if not NormalizeAndValidateInstallDir(Source, RawValue, Candidate) then exit;
  if Selected = '' then begin
    Selected := Candidate;
    Log('Selected previous installation directory from ' + Source + ': ' + Candidate);
  end else if not SamePath(Selected, Candidate) then begin
    Log('Conflicting previous installation directory from ' + Source + ': ' + Candidate);
    Conflict := True;
  end;
end;

function DirectoryFromDisplayIcon(const Value: String): String;
var
  IconPath: String;
  Comma: Integer;
  Index: Integer;
begin
  Result := '';
  IconPath := Trim(Value);
  Comma := 0;
  for Index := 1 to Length(IconPath) do
    if IconPath[Index] = ',' then Comma := Index;
  if Comma > 0 then IconPath := Copy(IconPath, 1, Comma - 1);
  IconPath := StripPairedQuotes(IconPath);
  if CompareText(ExtractFileName(IconPath), 'QingToolbox.Shell.exe') <> 0 then exit;
  Result := ExtractFileDir(IconPath);
end;

function DirectoryFromUninstallString(const Value: String): String;
var
  Command, Executable: String;
  ClosingQuote, ExeEnd: Integer;
  FileName: String;
begin
  Result := '';
  Command := Trim(Value);
  if Command = '' then exit;
  if Command[1] = '"' then begin
    ClosingQuote := Pos('"', Copy(Command, 2, Length(Command)));
    if ClosingQuote = 0 then exit;
    Executable := Copy(Command, 2, ClosingQuote - 1);
  end else begin
    ExeEnd := Pos('.exe', Lowercase(Command));
    if ExeEnd = 0 then exit;
    Executable := Copy(Command, 1, ExeEnd + 3);
  end;
  FileName := Lowercase(ExtractFileName(Executable));
  if (Copy(FileName, 1, 5) <> 'unins') or
     (Copy(FileName, Length(FileName) - 3, 4) <> '.exe') then exit;
  Result := ExtractFileDir(Executable);
end;

procedure ResolvePreviousInstallDirectory;
var
  Value: String;
begin
  ResolvedPreviousInstallDir := '';
  PreviousInstallConflict := False;
  if RegQueryStringValue(HKCU, UninstallRegistryKey, 'InstallLocation', Value) then
    AddInstallDirCandidate('fixed AppId InstallLocation', Value,
      ResolvedPreviousInstallDir, PreviousInstallConflict);
  if RegQueryStringValue(HKCU, ProductRegistryKey, 'InstallLocation', Value) then
    AddInstallDirCandidate('QingToolbox marker InstallLocation', Value,
      ResolvedPreviousInstallDir, PreviousInstallConflict);
  if RegQueryStringValue(HKCU, UninstallRegistryKey, 'DisplayIcon', Value) then
    AddInstallDirCandidate('fixed AppId DisplayIcon', DirectoryFromDisplayIcon(Value),
      ResolvedPreviousInstallDir, PreviousInstallConflict);
  if RegQueryStringValue(HKCU, UninstallRegistryKey, 'UninstallString', Value) then
    AddInstallDirCandidate('fixed AppId UninstallString', DirectoryFromUninstallString(Value),
      ResolvedPreviousInstallDir, PreviousInstallConflict);
  if ResolvedPreviousInstallDir = '' then
    Log('No trusted previous QingToolbox installation directory was found; using the default directory.');
end;

function GetDefaultInstallDir(Param: String): String;
begin
  if ResolvedPreviousInstallDir <> '' then Result := ResolvedPreviousInstallDir
  else Result := ExpandConstant('{userpf}\QingToolbox');
end;

function IsTargetShellRunning(const InstallDir: String): Boolean;
var
  Locator, Services, Processes, Process: Variant;
  I: Integer;
  TargetPath, ImagePath: String;
begin
  Result := False;
  if InstallDir = '' then exit;
  TargetPath := AddBackslash(InstallDir) + 'QingToolbox.Shell.exe';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Services.ExecQuery(
      'SELECT ExecutablePath FROM Win32_Process WHERE Name="QingToolbox.Shell.exe"');
    for I := 0 to Processes.Count - 1 do begin
      Process := Processes.ItemIndex(I);
      ImagePath := Process.ExecutablePath;
      if (ImagePath <> '') and SamePath(ExpandFileName(ImagePath), TargetPath) then begin
        Result := True;
        Log('The verified previous QingToolbox Shell is running and will be restored after upgrade.');
        exit;
      end;
    end;
  except
    Log('Unable to inspect one or more QingToolbox process image paths; no process will be forcefully terminated.');
  end;
end;

function ShouldRestoreRunningShell(): Boolean;
begin
  Result := WasQingToolboxRunningBeforeInstall;
end;

function ShouldOfferPostInstallRun(): Boolean;
begin
  Result := not WasQingToolboxRunningBeforeInstall;
end;

function HasReparsePoint(const RelativePath: String; IncludeLeaf: Boolean): Boolean;
var
  Remaining, Part, Current: String;
  Separator: Integer;
  Attributes: LongWord;
begin
  Result := False;
  Remaining := RelativePath;
  Current := ExpandConstant('{app}');
  while Remaining <> '' do begin
    Separator := Pos('\', Remaining);
    if Separator = 0 then begin
      Part := Remaining;
      Remaining := '';
    end else begin
      Part := Copy(Remaining, 1, Separator - 1);
      Remaining := Copy(Remaining, Separator + 1, Length(Remaining));
    end;
    if (Remaining = '') and (not IncludeLeaf) then exit;
    Current := AddBackslash(Current) + Part;
    Attributes := GetFileAttributesW(Current);
    if (Attributes <> InvalidFileAttributes) and
       ((Attributes and FileAttributeReparsePoint) <> 0) then begin
      Log('Skipping obsolete payload cleanup through reparse point: ' + Current);
      Result := True;
      exit;
    end;
  end;
end;

procedure SafeDeleteObsoleteHostFile(const RelativePath: String);
var
  Target: String;
begin
  if HasReparsePoint(RelativePath, False) then exit;
  Target := AddBackslash(ExpandConstant('{app}')) + RelativePath;
  if FileExists(Target) and (not DeleteFile(Target)) then
    Log('Unable to delete obsolete host file: ' + Target);
end;

procedure SafeRemoveObsoleteHostDirectory(const RelativePath: String);
var
  Target: String;
begin
  if HasReparsePoint(RelativePath, True) then exit;
  Target := AddBackslash(ExpandConstant('{app}')) + RelativePath;
  if DirExists(Target) and (not RemoveDir(Target)) then
    Log('Obsolete host directory was retained because it is not empty: ' + Target);
end;

function IsValidNumericIdentifier(const Value: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  if Value = '' then exit;
  if (Length(Value) > 1) and (Value[1] = '0') then exit;
  for I := 1 to Length(Value) do
    if (Value[I] < '0') or (Value[I] > '9') then exit;
  Result := True;
end;

function TryParseCore(const Value: String; var Major, Minor, Patch: Integer; var Pre: String): Boolean;
var
  Core: String;
  Dash, FirstDot, SecondDot: Integer;
  Rest, MajorText, MinorText, PatchText: String;
begin
  Result := False;
  Core := Value;
  Dash := Pos('-', Core);
  if Dash > 0 then begin
    Pre := Lowercase(Copy(Core, Dash + 1, Length(Core)));
    Core := Copy(Core, 1, Dash - 1);
    if Pre = '' then exit;
  end else
    Pre := '';
  FirstDot := Pos('.', Core);
  if FirstDot <= 1 then exit;
  Rest := Copy(Core, FirstDot + 1, Length(Core));
  SecondDot := Pos('.', Rest);
  if (SecondDot <= 1) or (Pos('.', Copy(Rest, SecondDot + 1, Length(Rest))) > 0) then exit;
  MajorText := Copy(Core, 1, FirstDot - 1);
  MinorText := Copy(Rest, 1, SecondDot - 1);
  PatchText := Copy(Rest, SecondDot + 1, Length(Rest));
  if (not IsValidNumericIdentifier(MajorText)) or
     (not IsValidNumericIdentifier(MinorText)) or
     (not IsValidNumericIdentifier(PatchText)) then exit;
  Major := StrToIntDef(MajorText, -1);
  Minor := StrToIntDef(MinorText, -1);
  Patch := StrToIntDef(PatchText, -1);
  Result := (Major >= 0) and (Minor >= 0) and (Patch >= 0);
end;

#include ObsoleteIncludePath

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    DeleteObsoleteHostPayload();
end;

function PreRank(const Value: String): Integer;
begin
  if Value = 'alpha' then Result := 0
  else if Value = 'beta' then Result := 1
  else if Value = 'rc' then Result := 2
  else Result := -1;
end;

function CompareSemVer(const Left, Right: String; var Valid: Boolean): Integer;
var
  LMajor, LMinor, LPatch, RMajor, RMinor, RPatch, LRank, RRank: Integer;
  LPre, RPre: String;
begin
  Valid := TryParseCore(Left, LMajor, LMinor, LPatch, LPre) and TryParseCore(Right, RMajor, RMinor, RPatch, RPre);
  Result := 0;
  if not Valid then exit;
  if LMajor <> RMajor then begin if LMajor < RMajor then Result := -1 else Result := 1; exit; end;
  if LMinor <> RMinor then begin if LMinor < RMinor then Result := -1 else Result := 1; exit; end;
  if LPatch <> RPatch then begin if LPatch < RPatch then Result := -1 else Result := 1; exit; end;
  if LPre = RPre then exit;
  if LPre = '' then begin Result := 1; exit; end;
  if RPre = '' then begin Result := -1; exit; end;
  LRank := PreRank(LPre); RRank := PreRank(RPre);
  if (LRank < 0) or (RRank < 0) then begin Valid := False; exit; end;
  if LRank < RRank then Result := -1 else Result := 1;
end;

function InitializeSetup(): Boolean;
var
  Installed, UninstallKey: String;
  Valid: Boolean;
  Comparison: Integer;
begin
  Result := True;
  ResolvePreviousInstallDirectory;
  if PreviousInstallConflict then begin
    SuppressibleMsgBox(ExpandConstant('{cm:ConflictingInstallLocations}'), mbError, MB_OK, IDOK);
    Result := False;
    exit;
  end;
  WasQingToolboxRunningBeforeInstall :=
    IsTargetShellRunning(ResolvedPreviousInstallDir);
  Installed := '';
  if not RegQueryStringValue(HKCU, 'Software\QingMo-A\QingToolbox', 'InstalledVersion', Installed) then begin
    UninstallKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}_is1';
    RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Installed);
  end;
  if Installed = '' then exit;
  Comparison := CompareSemVer(Installed, '{#AppVersion}', Valid);
  if not Valid then begin
    SuppressibleMsgBox('The installed QingToolbox version cannot be verified. Setup will not overwrite it.', mbError, MB_OK, IDOK);
    Result := False;
    exit;
  end;
  if Comparison > 0 then begin
    SuppressibleMsgBox('A newer QingToolbox version (' + Installed + ') is already installed. Downgrade was blocked.', mbError, MB_OK, IDOK);
    Result := False;
  end;
end;
