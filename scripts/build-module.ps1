[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('canary','launcher','pdf','convert','texttools','windowtopmost','powerguard','screenpin','liveactivity','passwordnotes')]
    [string]$Module,
    [string]$QingToolboxHostRoot = $env:QINGTOOLBOX_HOST_ROOT
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$oldHost = $env:QINGTOOLBOX_HOST_ROOT
$oldModules = $env:QINGTOOLBOX_MODULES_ROOT
try {
    if (-not [string]::IsNullOrWhiteSpace($QingToolboxHostRoot)) { $env:QINGTOOLBOX_HOST_ROOT = [IO.Path]::GetFullPath($QingToolboxHostRoot) }
    $hostRoot = & node (Join-Path $PSScriptRoot 'host-ui.mjs')
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($hostRoot)) { throw 'Compatible toolbox host checkout not found.' }
    $env:QINGTOOLBOX_HOST_ROOT = $hostRoot
    $env:QINGTOOLBOX_MODULES_ROOT = $root
    if ($Module -in @('liveactivity','passwordnotes')) {
        # New module builds belong to the modules branch, not a host release.
        $directory = if ($Module -eq 'liveactivity') { 'LiveActivity' } else { 'PasswordNotes' }
        & (Join-Path $root "modules/$directory/build.ps1") -QingToolboxHostRoot $hostRoot
    } else {
        & (Join-Path $hostRoot "scripts/build-tauri-$Module.ps1")
    }
    if ($LASTEXITCODE -ne 0) { throw "Module build failed: $Module" }
} finally {
    $env:QINGTOOLBOX_HOST_ROOT = $oldHost
    $env:QINGTOOLBOX_MODULES_ROOT = $oldModules
}
