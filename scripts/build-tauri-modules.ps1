[CmdletBinding()]
param([switch]$ForceRebuild)
$ErrorActionPreference = 'Stop'
$cache = Join-Path $PSScriptRoot 'tauri-build-cache.mjs'
foreach ($module in @('canary', 'launcher', 'pdf', 'transfer', 'texttools', 'windowtopmost', 'powerguard', 'screenpin')) {
    & node $cache check $module
    if (-not $ForceRebuild -and $LASTEXITCODE -eq 0) {
        Write-Host "Reusing unchanged Tauri module: $module"
        continue
    }
    $before = & node $cache fingerprint $module
    if ($LASTEXITCODE -ne 0) { throw "Cannot fingerprint $module" }
    & (Join-Path $PSScriptRoot "build-tauri-$module.ps1")
    if ($LASTEXITCODE -ne 0) { throw "Module build failed: $module" }
    & node $cache save $module $before
    if ($LASTEXITCODE -ne 0) { throw "Cannot cache module: $module" }
}
$global:LASTEXITCODE = 0
