[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PreviousInstallerPath,
    [Parameter(Mandatory = $true)][string]$CurrentInstallerPath,
    [string]$PreviousHostManifestPath,
    [string]$TestRoot,
    [switch]$KeepTestFiles
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$appId = '{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}_is1'
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
$markerKey = 'HKCU:\Software\QingMo-A\QingToolbox'
if ([string]::IsNullOrWhiteSpace($PreviousHostManifestPath)) {
    $PreviousHostManifestPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'installer\baselines\0.1.0-alpha-host-payload.json'
}
if ([string]::IsNullOrWhiteSpace($TestRoot)) { $TestRoot = Join-Path $env:TEMP ("QingToolbox-upgrade-" + [guid]::NewGuid().ToString('N')) }
$TestRoot = [IO.Path]::GetFullPath($TestRoot)
$isCi = $env:GITHUB_ACTIONS -eq 'true'
if (-not $isCi) {
    throw 'The real upgrade test requires a disposable GitHub Actions Windows profile; local Known Folders cannot be safely redirected.'
}
$allowedTempRoots = @([IO.Path]::GetFullPath($env:TEMP))
if ($isCi -and -not [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    $allowedTempRoots += [IO.Path]::GetFullPath($env:RUNNER_TEMP)
}
$isUnderAllowedTemp = $false
foreach ($root in $allowedTempRoots) {
    $prefix = $root.TrimEnd('\') + '\'
    if ($TestRoot.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $isUnderAllowedTemp = $true; break }
}
if (-not $isUnderAllowedTemp) { throw 'Upgrade TestRoot must be under a trusted temporary directory.' }
if (Test-Path $uninstallKey) { throw 'A real QingToolbox uninstall entry exists; refusing isolated upgrade test.' }
if (Get-Process QingToolbox.Shell -ErrorAction SilentlyContinue) { throw 'A QingToolbox.Shell process is running; refusing isolated upgrade test.' }
$previous = [IO.Path]::GetFullPath($PreviousInstallerPath); $current = [IO.Path]::GetFullPath($CurrentInstallerPath)
foreach($installer in @($previous,$current)){if(-not(Test-Path $installer -PathType Leaf)-or(Get-Item $installer).Length-le 0){throw "Installer is missing: $installer"}}
$install = Join-Path $TestRoot 'Install'; $profile = Join-Path $TestRoot 'Profile'
$oldLocal=$env:LOCALAPPDATA; $oldRoaming=$env:APPDATA
function Invoke-Setup([string]$path,[string]$log){
    $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',("/DIR=$install"),("/LOG=$log"))
    $process=Start-Process -FilePath $path -ArgumentList $arguments -Wait -PassThru
    return $process.ExitCode
}
function Assert-FileVersion([string]$expected){$info=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $install 'QingToolbox.Shell.exe'));if($info.FileVersion-ne$expected){throw "Shell FileVersion mismatch: $($info.FileVersion)"};return $info}
function Assert-InstallerIdentityAndShortcuts {
    param([string]$Phase)
    if (-not (Test-Path $uninstallKey)) { throw "$Phase removed the fixed Inno uninstall entry." }
    $entries = @(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' |
        Where-Object { $_.PSChildName -eq $appId })
    if ($entries.Count -ne 1) { throw "$Phase expected one QingToolbox uninstall entry, found $($entries.Count)." }
    $currentPrograms = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
    $commonPrograms = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)
    if ([string]::IsNullOrWhiteSpace($currentPrograms) -or [string]::IsNullOrWhiteSpace($commonPrograms)) {
        throw "$Phase could not resolve Windows Start Menu Known Folders."
    }
    $currentGroup = Join-Path $currentPrograms 'QingToolbox'
    $currentShortcuts = @(Get-ChildItem -LiteralPath $currentGroup -Filter '*.lnk' -File -ErrorAction SilentlyContinue)
    if ($currentShortcuts.Count -ne 2 -or @($currentShortcuts.Name | Sort-Object -Unique).Count -ne 2) {
        throw "$Phase expected one application and one uninstall shortcut, found $($currentShortcuts.Count)."
    }
    $commonGroup = Join-Path $commonPrograms 'QingToolbox'
    $commonShortcuts = @(Get-ChildItem -LiteralPath $commonGroup -Filter '*.lnk' -File -ErrorAction SilentlyContinue)
    if ($commonShortcuts.Count -ne 0) { throw "$Phase left $($commonShortcuts.Count) unexpected public Start Menu shortcuts." }
    return [pscustomobject]@{ CurrentGroup = $currentGroup; CommonGroup = $commonGroup }
}
try {
    New-Item -ItemType Directory -Path $TestRoot,$profile -Force | Out-Null
    $env:LOCALAPPDATA=Join-Path $profile 'LocalAppData'; $env:APPDATA=Join-Path $profile 'AppData'
    New-Item -ItemType Directory -Path $env:LOCALAPPDATA,$env:APPDATA -Force | Out-Null
    if((Invoke-Setup $previous (Join-Path $TestRoot 'preview1-install.log'))-ne 0){throw 'Preview 1 installation failed.'}
    [void](Assert-FileVersion '0.1.0.0')
    $preview1Process = Start-Process -FilePath (Join-Path $install 'QingToolbox.Shell.exe') -PassThru
    Start-Sleep -Milliseconds 750
    if ($preview1Process.HasExited) { throw 'Preview 1 Shell exited before the in-place upgrade could exercise process replacement.' }
    $settings=Join-Path $env:APPDATA 'QingToolbox\settings.json'; $module=Join-Path $env:LOCALAPPDATA 'QingToolbox\Modules\sentinel\module.json'
    $data=Join-Path $env:APPDATA 'QingToolbox\Data\sentinel.dat'; $cache=Join-Path $env:LOCALAPPDATA 'QingToolbox\Cache\sentinel.cache'
    foreach($parent in @((Split-Path $settings -Parent),(Split-Path $module -Parent),(Split-Path $data -Parent),(Split-Path $cache -Parent))){New-Item -ItemType Directory -Path $parent -Force|Out-Null}
    '{"language":"zh-CN","startupPresentationMode":0,"mainWindowCloseBehavior":1,"startupModules":[{"moduleId":"sentinel","version":"1.0.0","payloadSha256":"AA","payloadFileCount":1}]}'|Set-Content $settings -Encoding UTF8
    'module'|Set-Content $module -Encoding ASCII; 'data'|Set-Content $data -Encoding ASCII; 'cache'|Set-Content $cache -Encoding ASCII
    $unknown=Join-Path $install 'user-owned-unknown.txt'; 'unknown'|Set-Content $unknown -Encoding ASCII
    $sentinels=@($settings,$module,$data,$cache,$unknown);$hashes=@{};foreach($item in $sentinels){$hashes[$item]=(Get-FileHash $item -Algorithm SHA256).Hash}
    if((Invoke-Setup $current (Join-Path $TestRoot 'upgrade.log'))-ne 0){throw 'Preview 2 in-place upgrade failed.'}
    $preview1Process.Refresh()
    if (-not $preview1Process.HasExited) { throw 'Preview 1 Shell remained running after the in-place upgrade.' }
    $info=Assert-FileVersion '0.2.0.0';if($info.ProductVersion-notlike'0.2.0-alpha*'){throw "Shell ProductVersion mismatch: $($info.ProductVersion)"}
    if(-not(Test-Path (Join-Path $install 'host-payload.manifest.json'))){throw 'Host payload manifest is missing after upgrade.'}
    foreach($item in $sentinels){if(-not(Test-Path $item)-or(Get-FileHash $item -Algorithm SHA256).Hash-ne$hashes[$item]){throw "Upgrade did not preserve sentinel: $item"}}
    if(-not(Test-Path $uninstallKey)){throw 'Fixed Inno uninstall entry is missing after upgrade.'}
    $entry=Get-ItemProperty $uninstallKey;if($entry.DisplayVersion-ne'0.2.0-alpha'){throw "Uninstall DisplayVersion mismatch: $($entry.DisplayVersion)"}
    if ([IO.Path]::GetFullPath([string]$entry.InstallLocation).TrimEnd('\') -ne $install.TrimEnd('\')) { throw 'Upgrade registered a different installation directory.' }
    $shortcutRoots = Assert-InstallerIdentityAndShortcuts 'Upgrade'
    $previousManifest = Get-Content -LiteralPath $PreviousHostManifestPath -Raw | ConvertFrom-Json
    $currentManifest = Get-Content -LiteralPath (Join-Path $install 'host-payload.manifest.json') -Raw | ConvertFrom-Json
    $currentOwned = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($owned in $currentManifest.entries) { [void]$currentOwned.Add(([string]$owned.relativePath).Replace('\','/')) }
    $obsolete = @($previousManifest.entries | Where-Object {
        -not $currentOwned.Contains(([string]$_.relativePath).Replace('\','/'))
    })
    if ($obsolete.Count -eq 0) { throw 'The Preview 1 baseline did not exercise obsolete host cleanup.' }
    foreach ($owned in $obsolete) {
        $relative = ([string]$owned.relativePath).Replace('/', '\')
        if (Test-Path -LiteralPath (Join-Path $install $relative)) { throw "Obsolete Preview 1 host file remained after upgrade: $relative" }
    }
    if((Invoke-Setup $current (Join-Path $TestRoot 'repair.log'))-ne 0){throw 'Preview 2 repair installation failed.'}
    foreach($item in $sentinels){if(-not(Test-Path $item)-or(Get-FileHash $item -Algorithm SHA256).Hash-ne$hashes[$item]){throw "Repair did not preserve sentinel: $item"}}
    $repairEntry = Get-ItemProperty $uninstallKey
    if ($repairEntry.DisplayVersion -ne '0.2.0-alpha' -or
        [IO.Path]::GetFullPath([string]$repairEntry.InstallLocation).TrimEnd('\') -ne $install.TrimEnd('\')) {
        throw 'Repair changed the registered version or installation directory.'
    }
    [void](Assert-InstallerIdentityAndShortcuts 'Repair')
    New-Item -Path $markerKey -Force|Out-Null;Set-ItemProperty $markerKey InstalledVersion '0.3.0-alpha'
    $before=(Get-FileHash (Join-Path $install 'QingToolbox.Shell.exe') -Algorithm SHA256).Hash
    $downgrade=Invoke-Setup $current (Join-Path $TestRoot 'downgrade.log')
    if($downgrade-eq 0){throw 'Downgrade guard accepted a newer installed version.'}
    if((Get-FileHash (Join-Path $install 'QingToolbox.Shell.exe') -Algorithm SHA256).Hash-ne$before){throw 'Rejected downgrade changed host files.'}
    Set-ItemProperty $markerKey InstalledVersion '0.2.0-alpha'; Remove-Item -LiteralPath $unknown -Force
    $uninstaller=(Get-ItemProperty $uninstallKey).UninstallString.Trim('"');$p=Start-Process $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -Wait -PassThru
    if($p.ExitCode-ne 0){throw 'Preview 2 uninstall failed.'}
    foreach($item in @($settings,$module,$data,$cache)){if(-not(Test-Path $item)){throw "Uninstall removed user data: $item"}}
    if(Test-Path $uninstallKey){throw 'Uninstall entry remained after uninstall.'}
    if ((Test-Path -LiteralPath $shortcutRoots.CurrentGroup) -or
        (Test-Path -LiteralPath $shortcutRoots.CommonGroup)) {
        throw 'Uninstall left a QingToolbox Start Menu group behind.'
    }
    Write-Host 'Preview 1 -> Preview 2 upgrade, repair, downgrade guard, and user-state preservation passed.'
}
finally {
    $env:LOCALAPPDATA=$oldLocal;$env:APPDATA=$oldRoaming
    if(-not$KeepTestFiles -and (Test-Path $TestRoot)){Remove-Item -LiteralPath $TestRoot -Recurse -Force}
}
