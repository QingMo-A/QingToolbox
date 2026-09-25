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
    foreach ($relative in @(
        'resources/modules/qing.launcher/module.json',
        'resources/modules/qing.pdf/module.json',
        'resources/modules/qing.qingtransfer/module.json',
        'resources/modules/qing.launcher/third-party/Everything/LICENSE.txt',
        'resources/modules/qing.launcher/third-party/Everything/NOTICE.md',
        'resources/modules/qing.pdf/third-party/qpdf/LICENSE.txt',
        'resources/modules/qing.pdf/third-party/qpdf/NOTICE.md',
        'THIRD_PARTY_NOTICES.md',
        'LICENSE'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $installRoot $relative) -PathType Leaf)) {
            throw "Installed Tauri payload is missing $relative"
        }
    }
    & (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts/smoke-tauri-host.ps1') -ExecutablePath $installedExe
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
