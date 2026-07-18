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
DefaultDirName={userpf}\QingToolbox
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

function GetFileAttributesW(FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

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
