[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$canaryRoot = Join-Path $repoRoot 'QingToolbox.Tauri/native-module-canary'
$appRoot = Join-Path $repoRoot 'QingToolbox.Tauri'
$resourceRoot = Join-Path $appRoot 'src-tauri/resources/modules/qing.canary'
$cargoBin = Join-Path $HOME '.cargo\bin'
if (Test-Path -LiteralPath $cargoBin) {
    $env:Path = "$cargoBin;$env:Path"
}

$cargo = Get-Command cargo -ErrorAction SilentlyContinue
if (-not $cargo) {
    $cargoPath = Join-Path $HOME '.cargo\bin\cargo.exe'
    if (-not (Test-Path -LiteralPath $cargoPath)) {
        throw 'cargo was not found. Install Rust stable before building the canary.'
    }
} else {
    $cargoPath = $cargo.Source
}

& $cargoPath build --manifest-path (Join-Path $canaryRoot 'Cargo.toml') --release --locked
if ($LASTEXITCODE -ne 0) {
    throw "canary build failed with exit code $LASTEXITCODE"
}

$binary = Join-Path $canaryRoot 'target/release/qing-module-canary.exe'
if (-not (Test-Path -LiteralPath $binary)) {
    throw "canary binary was not produced: $binary"
}

New-Item -ItemType Directory -Force -Path (Join-Path $resourceRoot 'bin') | Out-Null
Copy-Item -LiteralPath (Join-Path $canaryRoot 'module.json') -Destination $resourceRoot -Force
Copy-Item -LiteralPath (Join-Path $canaryRoot 'icon.svg') -Destination $resourceRoot -Force
Copy-Item -LiteralPath $binary -Destination (Join-Path $resourceRoot 'bin/qing-module-canary.exe') -Force
if (Test-Path -LiteralPath (Join-Path $canaryRoot 'ui')) {
    Copy-Item -LiteralPath (Join-Path $canaryRoot 'ui') -Destination $resourceRoot -Recurse -Force
}
Write-Host "Tauri canary installed at $resourceRoot"
