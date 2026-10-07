[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$QingToolboxHostRoot)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$moduleRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$modulesRoot = [IO.Path]::GetFullPath((Join-Path $moduleRoot '../..'))
$hostRoot = [IO.Path]::GetFullPath($QingToolboxHostRoot)
$uiRoot = Join-Path $moduleRoot 'ui-src'
$resourceParent = [IO.Path]::GetFullPath((Join-Path $hostRoot 'QingToolbox.Tauri/src-tauri/resources/modules'))
$resourceRoot = [IO.Path]::GetFullPath((Join-Path $resourceParent 'qing.liveactivity'))
if ([IO.Path]::GetDirectoryName($resourceRoot) -ne $resourceParent) {
    throw "Refusing to replace resources outside the module root: $resourceRoot"
}
$installedBinary = Join-Path $resourceRoot 'bin/qing-liveactivity-module.exe'
$running = @(Get-CimInstance Win32_Process -Filter "Name='qing-liveactivity-module.exe'" | Where-Object {
    $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -eq $installedBinary
})
if ($running.Count -gt 0) {
    throw "Unload Qing Island in the development toolbox before replacing its files (PID $($running.ProcessId -join ', ')). Settings will be preserved."
}
$env:Path = (Join-Path $HOME '.cargo/bin') + [IO.Path]::PathSeparator + $env:Path
$oldHost = $env:QINGTOOLBOX_HOST_ROOT
try {
    $env:QINGTOOLBOX_HOST_ROOT = $hostRoot
    Push-Location $uiRoot
    try {
        if (-not (Test-Path -LiteralPath (Join-Path $uiRoot 'node_modules/.bin/vite.cmd'))) {
            npm ci
            if ($LASTEXITCODE -ne 0) { throw 'Qing Island UI dependency install failed.' }
        }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Qing Island UI build failed.' }
    } finally { Pop-Location }
    cargo build --manifest-path (Join-Path $moduleRoot 'Cargo.toml') --release --locked
    if ($LASTEXITCODE -ne 0) { throw 'Qing Island native build failed.' }
    $binary = Join-Path $moduleRoot 'target/release/qing-liveactivity-module.exe'
    $uiDist = Join-Path $uiRoot 'dist'
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $uiDist 'index.html') -PathType Leaf)) {
        throw 'Qing Island build outputs are missing.'
    }
    if (Test-Path -LiteralPath $resourceRoot) {
        $reparse = @(Get-Item -LiteralPath $resourceRoot -Force; Get-ChildItem -LiteralPath $resourceRoot -Force -Recurse) |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }
        if ($reparse) { throw 'Refusing to replace reparse-point module resources.' }
        # Absolute target and parent containment were checked above.
        Get-ChildItem -LiteralPath $resourceRoot -Force | Remove-Item -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $resourceRoot, (Join-Path $resourceRoot 'bin'),
        (Join-Path $resourceRoot 'ui'), (Join-Path $resourceRoot 'i18n') | Out-Null
    Copy-Item -LiteralPath (Join-Path $moduleRoot 'module.json'), (Join-Path $moduleRoot 'icon.svg'),
        (Join-Path $moduleRoot 'README.md'), (Join-Path $modulesRoot 'LICENSE') -Destination $resourceRoot
    Copy-Item -LiteralPath $binary -Destination $installedBinary
    Copy-Item -Path (Join-Path $uiDist '*') -Destination (Join-Path $resourceRoot 'ui') -Recurse
    Copy-Item -Path (Join-Path $moduleRoot 'i18n/*') -Destination (Join-Path $resourceRoot 'i18n')
    node (Join-Path $modulesRoot 'scripts/module-licenses.mjs') $moduleRoot (Join-Path $resourceRoot 'LICENSES.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Qing Island dependency license collection failed.' }
    Write-Host "Qing Island development resources updated: $resourceRoot"
} finally { $env:QINGTOOLBOX_HOST_ROOT = $oldHost }
