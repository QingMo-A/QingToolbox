[CmdletBinding()]
param([string]$QingToolboxHostRoot = $env:QINGTOOLBOX_HOST_ROOT, [switch]$MetadataOnly, [switch]$Build)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
& node (Join-Path $PSScriptRoot 'module-catalog.mjs') (Join-Path $root 'modules')
if ($LASTEXITCODE -ne 0) { throw 'Module catalog validation failed.' }
& node --test (Join-Path $PSScriptRoot 'module-catalog.test.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Module catalog tests failed.' }
if ($MetadataOnly) { return }
$env:Path = (Join-Path $HOME '.cargo/bin') + [IO.Path]::PathSeparator + $env:Path
$mapping = [ordered]@{canary='Canary'; launcher='Launcher'; liveactivity='LiveActivity'; pdf='QingPdf'; powerguard='PowerGuard'; screenpin='ScreenPin'; texttools='TextTools'; windowtopmost='WindowTopmost'}
foreach ($scope in $mapping.Keys) {
    if ($Build) { & (Join-Path $PSScriptRoot 'build-module.ps1') -Module $scope -QingToolboxHostRoot $QingToolboxHostRoot }
    $manifest = Join-Path $root "modules/$($mapping[$scope])/Cargo.toml"
    & cargo fmt --manifest-path $manifest -- --check
    if ($LASTEXITCODE -ne 0) { throw "Rust format check failed: $scope" }
    & cargo test --manifest-path $manifest --locked
    if ($LASTEXITCODE -ne 0) { throw "Rust tests failed: $scope" }
}
Write-Host 'Native module verification passed. No real shutdown or device-trust changes were performed.'
