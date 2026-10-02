[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ModuleId,
    [string]$QingToolboxHostRoot = $env:QINGTOOLBOX_HOST_ROOT,
    [string]$OutputDirectory,
    [switch]$SkipBuild,
    [switch]$Smoke
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
    $parameters = @{ ModuleId = $ModuleId }
    if ($OutputDirectory) { $parameters.OutputDirectory = $OutputDirectory }
    if ($SkipBuild) { $parameters.SkipBuild = $true }
    if ($Smoke) { $parameters.Smoke = $true }
    & (Join-Path $hostRoot 'scripts/package-tauri-module.ps1') @parameters
    if ($LASTEXITCODE -ne 0) { throw "Module packaging failed: $ModuleId" }
} finally {
    $env:QINGTOOLBOX_HOST_ROOT = $oldHost
    $env:QINGTOOLBOX_MODULES_ROOT = $oldModules
}
