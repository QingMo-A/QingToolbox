[CmdletBinding()]
param(
    [switch]$BuildDesktop,
    [switch]$SkipCanary,
    [switch]$SmokeDesktop
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$appRoot = Join-Path $repoRoot 'QingToolbox.Tauri'
$rustRoot = Join-Path $appRoot 'src-tauri'
$cargoBin = Join-Path $HOME '.cargo\bin'
if (Test-Path -LiteralPath $cargoBin) {
    $env:Path = "$cargoBin;$env:Path"
}

if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'package.json'))) {
    throw "Tauri app was not found: $appRoot"
}

$canaryExecutable = Join-Path $appRoot 'src-tauri/resources/modules/qing.canary/bin/qing-module-canary.exe'
if (-not $SkipCanary) {
    & (Join-Path $repoRoot 'scripts/build-tauri-canary.ps1')
    & (Join-Path $repoRoot 'scripts/smoke-tauri-canary.ps1') -ExecutablePath $canaryExecutable
}

Push-Location $appRoot
try {
    npm run typecheck
    npm run build
} finally {
    Pop-Location
}

$cargoCommand = Get-Command cargo -ErrorAction SilentlyContinue
if ($cargoCommand) {
    $cargoPath = $cargoCommand.Source
} else {
    $cargoPath = Join-Path $HOME '.cargo\bin\cargo.exe'
}
if (-not (Test-Path -LiteralPath $cargoPath)) {
    throw 'cargo was not found. Install Rust stable before running the Tauri verification.'
}

Push-Location $rustRoot
$previousCanaryPath = $env:QING_TAURI_CANARY_PATH
$env:QING_TAURI_CANARY_PATH = $canaryExecutable
try {
    & $cargoPath fmt --all -- --check
    if ($LASTEXITCODE -ne 0) { throw "cargo fmt check failed with exit code $LASTEXITCODE" }
    & $cargoPath test --locked
    if ($LASTEXITCODE -ne 0) { throw "cargo test failed with exit code $LASTEXITCODE" }
    & $cargoPath check --locked
    if ($LASTEXITCODE -ne 0) { throw "cargo check failed with exit code $LASTEXITCODE" }
    & $cargoPath clippy --locked --all-targets -- -D warnings
    if ($LASTEXITCODE -ne 0) { throw "cargo clippy failed with exit code $LASTEXITCODE" }
} finally {
    if ($null -eq $previousCanaryPath) {
        Remove-Item Env:QING_TAURI_CANARY_PATH -ErrorAction SilentlyContinue
    } else {
        $env:QING_TAURI_CANARY_PATH = $previousCanaryPath
    }
    Pop-Location
}

if ($BuildDesktop) {
    Push-Location $appRoot
    try {
        npm run tauri -- build --debug
        if ($LASTEXITCODE -ne 0) { throw "tauri build failed with exit code $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
}

if ($SmokeDesktop) {
    $desktopExecutable = Join-Path $rustRoot 'target/debug/qingtoolbox-tauri.exe'
    if (-not (Test-Path -LiteralPath $desktopExecutable)) {
        throw "Tauri desktop executable was not found: $desktopExecutable"
    }
    & (Join-Path $repoRoot 'scripts/smoke-tauri-host.ps1') -ExecutablePath $desktopExecutable
}

Write-Host 'Tauri verification passed.'
