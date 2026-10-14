[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$QingToolboxHostRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$moduleRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$modulesRoot = [IO.Path]::GetFullPath((Join-Path $moduleRoot '../..'))
$hostRoot = [IO.Path]::GetFullPath($QingToolboxHostRoot)
$uiRoot = Join-Path $moduleRoot 'ui-src'
$parent = [IO.Path]::GetFullPath((Join-Path $hostRoot 'QingToolbox.Tauri/src-tauri/resources/modules'))
$stage = [IO.Path]::GetFullPath((Join-Path $parent 'qing.passwordnotes'))
if ([IO.Path]::GetDirectoryName($stage) -ne $parent) { throw 'Unexpected module staging path.' }
$binary = Join-Path $stage 'bin/qing-passwordnotes-module.exe'
$running = @(Get-Process -Name qing-passwordnotes-module -ErrorAction SilentlyContinue | Where-Object { try { [IO.Path]::GetFullPath($_.Path) -eq $binary } catch { $false } })
if ($running.Count) { throw 'Unload Password Notes in Dev before replacing its files; do not delete the module.' }
$oldHost = $env:QINGTOOLBOX_HOST_ROOT
try {
    $env:QINGTOOLBOX_HOST_ROOT = $hostRoot
    Push-Location $uiRoot
    try {
        if (-not (Test-Path -LiteralPath 'node_modules/.bin/vite.cmd')) { npm ci --ignore-scripts --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' } }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'UI build failed.' }
    } finally { Pop-Location }
    cargo build --manifest-path (Join-Path $moduleRoot 'Cargo.toml') --release --locked
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
    if (Test-Path -LiteralPath $stage) {
        $items = @(Get-Item -LiteralPath $stage -Force; Get-ChildItem -LiteralPath $stage -Force -Recurse)
        if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Refusing to replace resources with reparse points.' }
        # The exact absolute target is a direct child of the validated resource parent.
        Get-ChildItem -LiteralPath $stage -Force | Remove-Item -Recurse -Force
    }
    New-Item -ItemType Directory -Path $stage, (Join-Path $stage 'bin'), (Join-Path $stage 'ui'), (Join-Path $stage 'i18n') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $moduleRoot 'module.json'), (Join-Path $moduleRoot 'icon.svg'), (Join-Path $moduleRoot 'README.md'), (Join-Path $modulesRoot 'LICENSE') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $moduleRoot 'target/release/qing-passwordnotes-module.exe') -Destination $binary
    Copy-Item -Path (Join-Path $uiRoot 'dist/*') -Destination (Join-Path $stage 'ui') -Recurse
    Copy-Item -Path (Join-Path $moduleRoot 'i18n/*') -Destination (Join-Path $stage 'i18n')
    node (Join-Path $modulesRoot 'scripts/module-licenses.mjs') $moduleRoot (Join-Path $stage 'LICENSES.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Rust license collection failed.' }
    Copy-Item -LiteralPath (Join-Path $uiRoot 'node_modules/vue/LICENSE') -Destination (Join-Path $stage 'VUE-LICENSE.txt')
    foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $uiRoot 'node_modules/@tauri-apps/api') -File | Where-Object { $_.Name -like 'LICENSE*' })) { Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $stage ('TAURI-API-'+$file.Name)) }
    Write-Host "Password Notes development resources updated: $stage"
} finally { $env:QINGTOOLBOX_HOST_ROOT = $oldHost }
