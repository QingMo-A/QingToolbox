[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$moduleRoot = Join-Path $repoRoot 'QingToolbox.Tauri/native-screenpin'
$uiRoot = Join-Path $moduleRoot 'ui-src'
$appRoot = Join-Path $repoRoot 'QingToolbox.Tauri'
$resourceRoot = Join-Path $appRoot 'src-tauri/resources/modules/qing.screenpin'
$cargoBin = Join-Path $HOME '.cargo\bin'
if (Test-Path -LiteralPath $cargoBin) { $env:Path = "$cargoBin;$env:Path" }
$cargoCommand = Get-Command cargo.exe -ErrorAction SilentlyContinue
$cargoPath = if ($cargoCommand) { $cargoCommand.Source } else { Join-Path $cargoBin 'cargo.exe' }
if (-not (Test-Path -LiteralPath $cargoPath -PathType Leaf)) { throw 'cargo was not found. Install Rust stable before building Screen Pin.' }
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw 'npm was not found. Install Node.js before building Screen Pin.' }
Push-Location $uiRoot
try {
    npm ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "Screen Pin UI dependency install failed with exit code $LASTEXITCODE" }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "Screen Pin UI build failed with exit code $LASTEXITCODE" }
} finally { Pop-Location }
& $cargoPath build --manifest-path (Join-Path $moduleRoot 'Cargo.toml') --release --locked
if ($LASTEXITCODE -ne 0) { throw "Screen Pin Rust module build failed with exit code $LASTEXITCODE" }
$binary = Join-Path $moduleRoot 'target/release/qing-screenpin-module.exe'; $uiDist = Join-Path $uiRoot 'dist'
if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Screen Pin binary was not produced: $binary" }
if (-not (Test-Path -LiteralPath (Join-Path $uiDist 'index.html') -PathType Leaf)) { throw "Screen Pin UI was not produced: $uiDist" }
$resolvedResource = [IO.Path]::GetFullPath($resourceRoot); $expectedSuffix = [IO.Path]::GetFullPath((Join-Path $appRoot 'src-tauri/resources/modules/qing.screenpin'))
if ($resolvedResource -ne $expectedSuffix) { throw "Refusing to write outside the Screen Pin resource root: $resolvedResource" }
if (Test-Path -LiteralPath $resolvedResource) { Get-ChildItem -LiteralPath $resolvedResource -Force | Remove-Item -Recurse -Force } else { New-Item -ItemType Directory -Force -Path $resolvedResource | Out-Null }
New-Item -ItemType Directory -Force -Path (Join-Path $resolvedResource 'bin'), (Join-Path $resolvedResource 'ui'), (Join-Path $resolvedResource 'i18n') | Out-Null
Copy-Item -LiteralPath (Join-Path $moduleRoot 'module.json') -Destination $resolvedResource -Force; Copy-Item -LiteralPath (Join-Path $moduleRoot 'icon.svg') -Destination $resolvedResource -Force
Copy-Item -LiteralPath $binary -Destination (Join-Path $resolvedResource 'bin/qing-screenpin-module.exe') -Force; Copy-Item -Path (Join-Path $uiDist '*') -Destination (Join-Path $resolvedResource 'ui') -Recurse -Force; Copy-Item -Path (Join-Path $moduleRoot 'i18n/*') -Destination (Join-Path $resolvedResource 'i18n') -Force
Write-Host "Screen Pin module installed at $resolvedResource"
