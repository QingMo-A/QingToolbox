[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$Smoke,
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/tauri-modules'))
} else {
    [IO.Path]::GetFullPath($OutputDirectory)
}
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$artifactsPrefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $outputRoot.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot -eq $artifactsRoot) {
    throw "Refusing to write module packages outside the repository artifacts directory: $outputRoot"
}

$moduleIds = @(
    'qing.canary',
    'qing.launcher',
    'qing.pdf',
    'qing.qingtransfer',
    'qing.texttools',
    'qing.windowtopmost',
    'qing.powerguard',
    'qing.screenpin'
)
$packer = Join-Path $PSScriptRoot 'package-tauri-module.ps1'
if (-not (Test-Path -LiteralPath $packer -PathType Leaf)) {
    throw "Tauri module packer is missing: $packer"
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
foreach ($existing in @(Get-ChildItem -LiteralPath $outputRoot -Force -ErrorAction SilentlyContinue)) {
    if ($existing.Name -ne 'README.md') {
        Remove-Item -LiteralPath $existing.FullName -Recurse -Force
    }
}

$packages = New-Object 'System.Collections.Generic.List[object]'
foreach ($moduleId in $moduleIds) {
    $arguments = @{
        ModuleId = $moduleId
        OutputDirectory = $outputRoot
    }
    if ($SkipBuild) { $arguments.SkipBuild = $true }
    if ($Smoke) { $arguments.Smoke = $true }
    & $packer @arguments
    if (-not $?) {
        throw "Tauri module package failed for $moduleId."
    }

    $manifestPath = Join-Path $repoRoot "QingToolbox.Tauri/src-tauri/resources/modules/$moduleId/module.json"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $version = ([string]$manifest.version).Trim()
    $fileName = "$moduleId-$version-tauri.qmod"
    $packagePath = Join-Path $outputRoot $fileName
    $checksumPath = "$packagePath.sha256"
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        throw "Tauri module package or checksum is missing for $moduleId."
    }
    [void]$packages.Add([ordered]@{
        moduleId = $moduleId
        version = $version
        file = $fileName
        sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
        size = (Get-Item -LiteralPath $packagePath).Length
    })
}

$index = [ordered]@{
    schemaVersion = 1
    packageFormat = 'qmod'
    moduleProfile = 'tauri-process-v1'
    packages = $packages.ToArray()
}
$indexPath = Join-Path $outputRoot 'tauri-modules-manifest.json'
$index | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $indexPath -Encoding utf8

Write-Host "`nTauri module package set prepared."
Write-Host "Directory: $outputRoot"
Write-Host "Manifest:  $indexPath"
Write-Host "Packages:  $($packages.Count)"
