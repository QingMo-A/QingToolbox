[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('canary','launcher','pdf','texttools','windowtopmost','powerguard','screenpin','liveactivity')]
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
    if ($Module -eq 'liveactivity') {
        # New module builds belong to the modules branch, not a host release.
        & (Join-Path $root 'modules/LiveActivity/build.ps1') -QingToolboxHostRoot $hostRoot
    } else {
        & (Join-Path $hostRoot "scripts/build-tauri-$Module.ps1")
    }
    if ($LASTEXITCODE -ne 0) { throw "Module build failed: $Module" }
} finally {
    $env:QINGTOOLBOX_HOST_ROOT = $oldHost
    $env:QINGTOOLBOX_MODULES_ROOT = $oldModules
}
