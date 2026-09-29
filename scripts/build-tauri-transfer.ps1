[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$moduleRoot = Join-Path $repoRoot 'QingToolbox.Tauri/native-transfer'
$appRoot = Join-Path $repoRoot 'QingToolbox.Tauri'
$resourceRoot = Join-Path $appRoot 'src-tauri/resources/modules/qing.qingtransfer'
$cargoBin = Join-Path $HOME '.cargo\bin'
if (Test-Path -LiteralPath $cargoBin) { $env:Path = "$cargoBin;$env:Path" }

$cargoCommand = Get-Command cargo.exe -ErrorAction SilentlyContinue
$cargoPath = if ($cargoCommand) { $cargoCommand.Source } else { Join-Path $cargoBin 'cargo.exe' }
if (-not (Test-Path -LiteralPath $cargoPath -PathType Leaf)) {
    throw 'cargo was not found. Install Rust stable before building QingTransfer.'
}

& $cargoPath build --manifest-path (Join-Path $moduleRoot 'Cargo.toml') --release --locked
if ($LASTEXITCODE -ne 0) { throw "QingTransfer Rust module build failed with exit code $LASTEXITCODE" }

$binary = Join-Path $moduleRoot 'target/release/qing-transfer-module.exe'
if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "QingTransfer binary was not produced: $binary" }

# Resolve the fixed destination before cleanup. This guard is intentionally
# strict because the following operation removes generated module output.
$resolvedResource = [IO.Path]::GetFullPath($resourceRoot)
$expectedSuffix = [IO.Path]::GetFullPath((Join-Path $appRoot 'src-tauri/resources/modules/qing.qingtransfer'))
if ($resolvedResource -ne $expectedSuffix) {
    throw "Refusing to write outside the QingTransfer resource root: $resolvedResource"
}
if (Test-Path -LiteralPath $resolvedResource) {
    Get-ChildItem -LiteralPath $resolvedResource -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Force -Path $resolvedResource | Out-Null
}
New-Item -ItemType Directory -Force -Path (Join-Path $resolvedResource 'bin'), (Join-Path $resolvedResource 'i18n') | Out-Null
Copy-Item -LiteralPath (Join-Path $moduleRoot 'module.json') -Destination $resolvedResource -Force
Copy-Item -LiteralPath (Join-Path $moduleRoot 'icon.svg') -Destination $resolvedResource -Force
Copy-Item -LiteralPath $binary -Destination (Join-Path $resolvedResource 'bin/qing-transfer-module.exe') -Force
Copy-Item -Path (Join-Path $moduleRoot 'i18n/*') -Destination (Join-Path $resolvedResource 'i18n') -Force
Write-Host "QingTransfer module installed at $resolvedResource"
