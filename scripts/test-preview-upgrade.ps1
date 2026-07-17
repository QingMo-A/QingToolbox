[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PreviousInstallerPath,
    [Parameter(Mandatory = $true)][string]$CurrentInstallerPath,
    [string]$PreviousHostManifestPath,
    [string]$TestRoot,
    [switch]$KeepTestFiles,
    [switch]$AllowIsolatedLocalRun
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
if (-not $isCi -and -not $AllowIsolatedLocalRun) { throw 'Local upgrade testing requires -AllowIsolatedLocalRun after safety review.' }
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
    $matchingEntries = @(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' |
        Where-Object { $_.PSChildName -eq $appId })
    if ($matchingEntries.Count -ne 1) { throw "Expected one QingToolbox uninstall entry, found $($matchingEntries.Count)." }
    $programsFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
    if ([string]::IsNullOrWhiteSpace($programsFolder)) { throw 'Windows Start Menu Programs folder could not be resolved.' }
    $startMenu = Join-Path $programsFolder 'QingToolbox'
    $shortcuts = @(Get-ChildItem -LiteralPath $startMenu -Filter '*.lnk' -File -ErrorAction SilentlyContinue)
    if ($shortcuts.Count -ne 2 -or @($shortcuts.Name | Sort-Object -Unique).Count -ne 2) {
        throw "Expected one application and one uninstall Start Menu shortcut, found $($shortcuts.Count)."
    }
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
    $shortcuts = @(Get-ChildItem -LiteralPath $startMenu -Filter '*.lnk' -File -ErrorAction SilentlyContinue)
    if ($shortcuts.Count -ne 2 -or @($shortcuts.Name | Sort-Object -Unique).Count -ne 2) {
        throw "Repair changed or duplicated Start Menu shortcuts: $($shortcuts.Count)."
    }
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
    Write-Host 'Preview 1 -> Preview 2 upgrade, repair, downgrade guard, and user-state preservation passed.'
}
finally {
    $env:LOCALAPPDATA=$oldLocal;$env:APPDATA=$oldRoaming
    if(-not$KeepTestFiles -and (Test-Path $TestRoot)){Remove-Item -LiteralPath $TestRoot -Recurse -Force}
}
