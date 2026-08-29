[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$moduleRoot = Join-Path $repoRoot 'QingToolbox.Tauri/native-launcher'
$uiRoot = Join-Path $moduleRoot 'ui-src'
$appRoot = Join-Path $repoRoot 'QingToolbox.Tauri'
$resourceRoot = Join-Path $appRoot 'src-tauri/resources/modules/qing.launcher'
$cargoBin = Join-Path $HOME '.cargo\bin'
if (Test-Path -LiteralPath $cargoBin) { $env:Path = "$cargoBin;$env:Path" }

$cargoCommand = Get-Command cargo -ErrorAction SilentlyContinue
$cargoPath = if ($cargoCommand) { $cargoCommand.Source } else { Join-Path $cargoBin 'cargo.exe' }
if (-not (Test-Path -LiteralPath $cargoPath)) { throw 'cargo was not found. Install Rust stable before building Qing Launcher.' }

$npmCommand = Get-Command npm -ErrorAction SilentlyContinue
if (-not $npmCommand) { throw 'npm was not found. Install Node.js before building Qing Launcher.' }

Push-Location $uiRoot
try {
    npm ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "Launcher UI dependency install failed with exit code $LASTEXITCODE" }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "Launcher UI build failed with exit code $LASTEXITCODE" }
} finally { Pop-Location }

& $cargoPath build --manifest-path (Join-Path $moduleRoot 'Cargo.toml') --release --locked
if ($LASTEXITCODE -ne 0) { throw "Launcher Rust module build failed with exit code $LASTEXITCODE" }

$binary = Join-Path $moduleRoot 'target/release/qing-launcher-module.exe'
$uiDist = Join-Path $uiRoot 'dist'
if (-not (Test-Path -LiteralPath $binary)) { throw "Launcher binary was not produced: $binary" }
if (-not (Test-Path -LiteralPath (Join-Path $uiDist 'index.html'))) { throw "Launcher UI was not produced: $uiDist" }

# The destination is a fixed development resource root. Resolve it before the
# bounded cleanup so a malformed repository path cannot broaden the delete.
$resolvedResource = [IO.Path]::GetFullPath($resourceRoot)
$expectedSuffix = [IO.Path]::GetFullPath((Join-Path $appRoot 'src-tauri/resources/modules/qing.launcher'))
if ($resolvedResource -ne $expectedSuffix) { throw "Refusing to write outside the Qing Launcher resource root: $resolvedResource" }
if (Test-Path -LiteralPath $resolvedResource) {
    Get-ChildItem -LiteralPath $resolvedResource -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Force -Path $resolvedResource | Out-Null
}
New-Item -ItemType Directory -Force -Path (Join-Path $resolvedResource 'bin') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $resolvedResource 'ui') | Out-Null
Copy-Item -LiteralPath (Join-Path $moduleRoot 'module.json') -Destination $resolvedResource -Force
Copy-Item -LiteralPath (Join-Path $moduleRoot 'icon.svg') -Destination $resolvedResource -Force
Copy-Item -LiteralPath $binary -Destination (Join-Path $resolvedResource 'bin/qing-launcher-module.exe') -Force
Copy-Item -Path (Join-Path $uiDist '*') -Destination (Join-Path $resolvedResource 'ui') -Recurse -Force
Write-Host "Qing Launcher module installed at $resolvedResource"
