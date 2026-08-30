[CmdletBinding()]
param(
    [switch]$SkipModuleBuild,
    [switch]$Smoke,
    [switch]$Zip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\tauri-production'))
$stageRoot = Join-Path $artifactRoot 'QingToolbox'
$portableScript = Join-Path $PSScriptRoot 'build-tauri-portable.ps1'

if (-not (Test-Path -LiteralPath $portableScript -PathType Leaf)) {
    throw "Tauri release builder was not found: $portableScript"
}

# Keep one packaging implementation for both preview and production. The
# production entry point differs only in its fixed artifact channel and its
# release manifest label; this avoids two copies of module staging logic
# drifting apart.
$arguments = @{
    OutputDirectory = $artifactRoot
    Distribution = 'production'
}
if ($SkipModuleBuild) { $arguments.SkipModuleBuild = $true }
if ($Smoke) { $arguments.Smoke = $true }
if ($Zip) { $arguments.Zip = $true }

& $portableScript @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Tauri production package failed with exit code $LASTEXITCODE."
}

$manifestPath = Join-Path $stageRoot 'portable-manifest.json'
$executablePath = Join-Path $stageRoot 'QingToolbox.exe'
foreach ($path in @($manifestPath, $executablePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Tauri production package is incomplete: $path"
    }
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.distribution -ne 'production' -or $manifest.buildProfile -ne 'release') {
    throw 'Tauri production manifest is not marked as a release production package.'
}
if ($manifest.backend -ne 'rust' -or $manifest.framework -ne 'tauri-2' -or $manifest.frontend -ne 'vue-3') {
    throw 'Tauri production manifest does not describe the Rust/Tauri/Vue host.'
}

Write-Host "`nTauri production package prepared."
Write-Host "Directory: $stageRoot"
Write-Host "Executable: $executablePath"
Write-Host "Manifest: $manifestPath"
