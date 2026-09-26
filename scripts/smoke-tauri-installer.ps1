[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [string]$TestRoot,
    [switch]$KeepTestFiles
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') {
    throw 'The product-AppId installer smoke runs only in a disposable GitHub Actions Windows profile.'
}

$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
if ([IO.Path]::GetExtension($installer) -ine '.exe') { throw 'InstallerPath must point to an .exe.' }
$root = if ([string]::IsNullOrWhiteSpace($TestRoot)) {
    Join-Path ([IO.Path]::GetTempPath()) "QingToolbox-Tauri-InstallerSmoke-$PID"
} else { [IO.Path]::GetFullPath($TestRoot) }
$installRoot = Join-Path $root 'install'
$sentinelRoot = Join-Path $root 'profile'
$installedExe = Join-Path $installRoot 'QingToolbox.exe'
$uninstaller = Join-Path $installRoot 'unins000.exe'
$markerKey = 'HKCU:\Software\QingMo-A\QingToolbox\Tauri'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}_is1'

function Invoke-Installer {
    param([string]$Path, [string[]]$Arguments)
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installer command failed ($($process.ExitCode)): $Path" }
}

function Normalize-DirectoryPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd(
        [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    )
}

if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root, $sentinelRoot | Out-Null
try {
    Invoke-Installer -Path $installer -Arguments @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS',
        "/DIR=$installRoot"
    )
    if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
        throw "Tauri installer did not create the host executable: $installedExe"
    }
    if (-not (Test-Path -LiteralPath $markerKey)) {
        throw 'Tauri installer did not create its production marker.'
    }
    $marker = Get-ItemProperty -LiteralPath $markerKey
    foreach ($expected in @{
        InstallKind = 'tauri-production'
        InstallerContractVersion = '1'
        AppId = '{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}'
        InstallLocation = $installRoot
        InstalledVersion = $null
        Distribution = 'production'
        Backend = 'rust'
        Framework = 'tauri-2'
        Frontend = 'vue-3'
        BuildProfile = 'release'
        ExecutableName = 'QingToolbox.exe'
        ManifestFileName = 'portable-manifest.json'
    }.GetEnumerator()) {
        $actual = [string]$marker.($expected.Key)
        if ($expected.Key -eq 'InstalledVersion') {
            if ([string]::IsNullOrWhiteSpace($actual)) { throw 'Tauri production marker has no InstalledVersion.' }
        } elseif ($actual -ne $expected.Value) {
            throw "Tauri production marker mismatch for $($expected.Key): $actual"
        }
    }
    if (-not (Test-Path -LiteralPath $uninstallKey)) {
        throw 'Tauri installer did not create the fixed AppId uninstall record.'
    }
    $uninstallRecord = Get-ItemProperty -LiteralPath $uninstallKey
    if ([string]$uninstallRecord.DisplayName -ne 'QingToolbox' -or
        (Normalize-DirectoryPath ([string]$uninstallRecord.InstallLocation)) -ne
            (Normalize-DirectoryPath $installRoot) -or
        [string]::IsNullOrWhiteSpace([string]$uninstallRecord.DisplayVersion)) {
        throw 'Tauri uninstall record does not match the candidate installation.'
    }
    if (Test-Path -LiteralPath (Join-Path $installRoot 'resources/modules')) {
        throw 'The installed host unexpectedly contains bundled modules.'
    }
    foreach ($relative in @('THIRD_PARTY_NOTICES.md', 'LICENSE')) {
        if (-not (Test-Path -LiteralPath (Join-Path $installRoot $relative) -PathType Leaf)) {
            throw "Installed Tauri payload is missing $relative"
        }
    }
    & (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/smoke-tauri-host.ps1') -ExecutablePath $installedExe
    # Simulate an upgrade from the published 0.3.0-alpha bundled-module
    # layout. A legacy WPF user module must be backed up, while a newer
    # already-installed Tauri module must be left untouched.
    $oldBundledRoot = Join-Path $installRoot 'resources/modules'
    $userModulesRoot = Join-Path $env:LOCALAPPDATA 'QingToolbox/Modules'
    foreach ($moduleId in @('qing.canary', 'qing.launcher', 'qing.pdf')) {
        if (Test-Path -LiteralPath (Join-Path $userModulesRoot $moduleId)) {
            throw "Installer migration smoke requires an empty CI module slot: $moduleId"
        }
        $source = Join-Path $oldBundledRoot $moduleId
        New-Item -ItemType Directory -Force -Path (Join-Path $source 'bin') | Out-Null
        $version = if ($moduleId -eq 'qing.pdf') { '0.1.0' } else { '0.3.0' }
        @{ id = $moduleId; version = $version; runtimeType = 'Process' } |
            ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $source 'module.json') -Encoding UTF8
        'old bundled executable' | Set-Content -LiteralPath (Join-Path $source 'bin/module.exe') -Encoding ASCII
    }
    $legacyUser = Join-Path $userModulesRoot 'qing.launcher'
    $newerUser = Join-Path $userModulesRoot 'qing.pdf'
    New-Item -ItemType Directory -Force -Path $legacyUser, $newerUser | Out-Null
    @{ id = 'qing.launcher'; version = '0.2.2'; runtimeType = 'InProcess' } |
        ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $legacyUser 'module.json') -Encoding UTF8
    @{ id = 'qing.pdf'; version = '9.9.9'; runtimeType = 'Process' } |
        ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $newerUser 'module.json') -Encoding UTF8
    Invoke-Installer -Path $installer -Arguments @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS',
        "/DIR=$installRoot"
    )
    if (Test-Path -LiteralPath $oldBundledRoot) {
        throw 'The upgrade retained bundled module files in the host installation.'
    }
    foreach ($moduleId in @('qing.canary', 'qing.launcher')) {
        $manifest = Join-Path $userModulesRoot "$moduleId/module.json"
        if (-not (Test-Path -LiteralPath $manifest -PathType Leaf) -or
            (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).runtimeType -ne 'Process') {
            throw "The upgrade did not migrate $moduleId into the user module root."
        }
    }
    if ((Get-Content -LiteralPath (Join-Path $newerUser 'module.json') -Raw | ConvertFrom-Json).version -ne '9.9.9') {
        throw 'The upgrade replaced a newer user-installed Tauri module.'
    }
    $legacyBackups = @(Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'QingToolbox-MigrationBackups') -Directory -Filter 'TauriModules-*' -ErrorAction SilentlyContinue |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'qing.launcher/module.json') -PathType Leaf })
    if ($legacyBackups.Count -ne 1 -or
        (Get-Content -LiteralPath (Join-Path $legacyBackups[0].FullName 'qing.launcher/module.json') -Raw | ConvertFrom-Json).runtimeType -ne 'InProcess') {
        throw 'The upgrade did not preserve the old WPF module in a migration backup.'
    }
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        throw "Tauri uninstaller was not created: $uninstaller"
    }
    Invoke-Installer -Path $uninstaller -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    if (Test-Path -LiteralPath $installedExe -PathType Leaf) {
        throw 'Tauri uninstaller left the host executable behind.'
    }
    if (Test-Path -LiteralPath $markerKey) {
        throw 'Tauri uninstaller left its production marker behind.'
    }
    Write-Host 'Tauri installer install/uninstall smoke test passed.'
}
finally {
    if (-not $KeepTestFiles -and (Test-Path -LiteralPath $root)) {
        Remove-Item -LiteralPath $root -Recurse -Force
    } elseif ($KeepTestFiles) {
        Write-Host "Installer smoke files: $root"
    }
}
