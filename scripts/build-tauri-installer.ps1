[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$Smoke,
    [switch]$Zip,
    [string]$IsccPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$tauriRoot = Join-Path $repoRoot 'QingToolbox.Tauri'
$iconPath = Join-Path $tauriRoot 'src-tauri/icons/icon.ico'
$productionRoot = Join-Path $repoRoot 'artifacts/tauri-production'
$sourceRoot = Join-Path $productionRoot 'QingToolbox'
$installerRoot = Join-Path $repoRoot 'artifacts/tauri-installer'
$outputRoot = Join-Path $installerRoot 'output'
$installerScript = Join-Path $repoRoot 'installer/QingToolbox.Tauri.iss'

function Resolve-Iscc {
    param([string]$ExplicitPath)
    $candidates = if ([string]::IsNullOrWhiteSpace($ExplicitPath)) {
        @(
            'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
            'C:\Program Files\Inno Setup 6\ISCC.exe',
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
        )
    } else { @($ExplicitPath) }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    throw 'ISCC.exe was not found. Install Inno Setup 6 or pass -IsccPath.'
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )
    Write-Host "`n==> $Label"
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE." }
}

function Resolve-Version {
    $cargo = Get-Content -LiteralPath (Join-Path $tauriRoot 'src-tauri/Cargo.toml') -Raw
    $match = [regex]::Match($cargo, '(?m)^version\s*=\s*"([^"]+)"')
    if (-not $match.Success) { throw 'Unable to resolve the Tauri host version.' }
    $match.Groups[1].Value
}

function Test-Manifest {
    param([Parameter(Mandatory = $true)][string]$Root)
    $manifestPath = Join-Path $Root 'portable-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Portable manifest is missing: $manifestPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($property in @('distribution', 'backend', 'framework', 'frontend', 'buildProfile', 'sourceCommit', 'sourceDirty', 'files')) {
        if ($null -eq $manifest.$property) { throw "Portable manifest is missing '$property'." }
    }
    if ($manifest.distribution -ne 'production' -or
        $manifest.backend -ne 'rust' -or
        $manifest.framework -ne 'tauri-2' -or
        $manifest.frontend -ne 'vue-3' -or
        $manifest.buildProfile -ne 'release') {
        throw 'Portable manifest is not a Rust/Tauri/Vue production Release.'
    }
    if ([bool]$manifest.sourceDirty) {
        throw 'Refusing to package a dirty Tauri source tree; commit the source first.'
    }
    $moduleRoot = Join-Path $Root 'resources/modules'
    if (-not (Test-Path -LiteralPath $moduleRoot -PathType Container)) {
        throw "Portable package is missing resources/modules: $moduleRoot"
    }
    $expectedModules = @(
        'qing.canary', 'qing.launcher', 'qing.pdf', 'qing.qingtransfer',
        'qing.texttools', 'qing.windowtopmost', 'qing.powerguard', 'qing.screenpin'
    )
    foreach ($module in $expectedModules) {
        $modulePath = Join-Path $moduleRoot $module
        if (-not (Test-Path -LiteralPath (Join-Path $modulePath 'module.json') -PathType Leaf)) {
            throw "Portable package is missing the manifest for $module."
        }
        if (-not (Test-Path -LiteralPath (Join-Path $modulePath 'bin') -PathType Container)) {
            throw "Portable package is missing the executable directory for $module."
        }
    }
    foreach ($entry in @($manifest.files)) {
        $relative = [string]$entry.path
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Contains('..') -or $relative.StartsWith('/') -or $relative.Contains('\')) {
            throw "Portable manifest contains an unsafe path: $relative"
        }
        $file = Join-Path $Root ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
        $resolved = [IO.Path]::GetFullPath($file)
        $prefix = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Portable manifest references a missing/out-of-root file: $relative"
        }
        $hash = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne ([string]$entry.sha256).ToLowerInvariant()) {
            throw "Portable manifest hash mismatch: $relative"
        }
    }
    return $manifest
}

if (-not (Test-Path -LiteralPath $installerScript -PathType Leaf)) { throw "Installer script is missing: $installerScript" }
if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) { throw "Tauri icon is missing: $iconPath" }

if (-not $SkipBuild) {
    Invoke-Checked -Label 'Build production Tauri directory' -Action {
        & (Join-Path $repoRoot 'scripts/build-tauri-production.ps1') -Smoke
    }
}

$manifest = Test-Manifest -Root $sourceRoot
$currentCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if (-not $?) {
    throw 'Unable to resolve the current source commit before packaging the Tauri installer.'
}
if ([string]::IsNullOrWhiteSpace($currentCommit) -or
    -not [string]::Equals($currentCommit, [string]$manifest.sourceCommit, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The production directory is stale: it was built from $($manifest.sourceCommit), but HEAD is $currentCommit. Rebuild it before packaging."
}
$version = [string]$manifest.version
$fileVersion = if ($version -match '^([0-9]+)\.([0-9]+)\.([0-9]+)') {
    "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
} else { '0.1.0.0' }
$resolvedIscc = Resolve-Iscc -ExplicitPath $IsccPath
$innoRoot = Split-Path -Parent $resolvedIscc
foreach ($languageFile in @(
    (Join-Path $innoRoot 'Default.isl'),
    (Join-Path $innoRoot 'Languages/ChineseSimplified.isl')
)) {
    if (-not (Test-Path -LiteralPath $languageFile -PathType Leaf)) {
        throw "Required Inno Setup language file is missing: $languageFile"
    }
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
Get-ChildItem -LiteralPath $outputRoot -Force -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force
$baseName = "QingToolbox-$version-win-x64-tauri-setup"
Invoke-Checked -Label 'Compile Tauri Inno installer' -Action {
    & $resolvedIscc `
        "/DAppVersion=$version" `
        "/DFileVersion=$fileVersion" `
        "/DSourceDir=$sourceRoot" `
        "/DOutputDir=$outputRoot" `
        "/DOutputBaseFilename=$baseName" `
        "/DBrandIconPath=$iconPath" `
        $installerScript
}

$installerPath = Join-Path $outputRoot "$baseName.exe"
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Tauri installer was not produced: $installerPath"
}
$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToUpperInvariant()
"$hash  $(Split-Path -Leaf $installerPath)" |
    Set-Content -LiteralPath "$installerPath.sha256" -Encoding ASCII

if ($Zip) {
    $zipPath = Join-Path $installerRoot "$baseName.zip"
    if (Test-Path -LiteralPath $zipPath -PathType Leaf) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $outputRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Installer archive: $zipPath"
}

if ($Smoke) {
    Invoke-Checked -Label 'Smoke Tauri installer install/uninstall' -Action {
        & (Join-Path $repoRoot 'scripts/smoke-tauri-installer.ps1') -InstallerPath $installerPath
    }
}

Write-Host "`nTauri installer candidate prepared."
Write-Host "Installer: $installerPath"
Write-Host "SHA256:   $hash"
Write-Host "Source:   $($manifest.sourceCommit)"
