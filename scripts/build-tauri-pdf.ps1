[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$moduleRoot = Join-Path $repoRoot 'QingToolbox.Tauri/native-pdf'
$uiRoot = Join-Path $moduleRoot 'ui-src'
$appRoot = Join-Path $repoRoot 'QingToolbox.Tauri'
$resourceRoot = Join-Path $appRoot 'src-tauri/resources/modules/qing.pdf'
$cargoBin = Join-Path $HOME '.cargo\bin'
if (Test-Path -LiteralPath $cargoBin) { $env:Path = "$cargoBin;$env:Path" }

$cargoCommand = Get-Command cargo.exe -ErrorAction SilentlyContinue
$cargoPath = if ($cargoCommand) { $cargoCommand.Source } else { Join-Path $cargoBin 'cargo.exe' }
if (-not (Test-Path -LiteralPath $cargoPath -PathType Leaf)) {
    throw 'cargo was not found. Install Rust stable before building Qing PDF.'
}
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    throw 'npm was not found. Install Node.js before building Qing PDF.'
}

Push-Location $uiRoot
try {
    npm ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "Qing PDF UI dependency install failed with exit code $LASTEXITCODE" }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "Qing PDF UI build failed with exit code $LASTEXITCODE" }
} finally { Pop-Location }

& $cargoPath build --manifest-path (Join-Path $moduleRoot 'Cargo.toml') --release --locked
if ($LASTEXITCODE -ne 0) { throw "Qing PDF Rust module build failed with exit code $LASTEXITCODE" }

$binary = Join-Path $moduleRoot 'target/release/qing-pdf-module.exe'
$uiDist = Join-Path $uiRoot 'dist'
if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) { throw "Qing PDF binary was not produced: $binary" }
if (-not (Test-Path -LiteralPath (Join-Path $uiDist 'index.html') -PathType Leaf)) { throw "Qing PDF UI was not produced: $uiDist" }

$qpdfRoot = Join-Path $moduleRoot 'third-party/qpdf'
$qpdfHashes = @{
    'concrt140.dll' = '54716F0738AF891F283D213B5C8D11B25896BB8EE3097D301EAE718560CF974E'
    'LICENSE.txt' = 'CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30'
    'msvcp140_1.dll' = '206C931BF90FDAD8816DE3B5E2EF80B2BCAA9406C89ECC05FE6FDDFFE251E982'
    'msvcp140_2.dll' = 'D50D7883F20D1DC6191768D3746F52DD9CAC89C346FFAED5BE1F110C2F34A838'
    'msvcp140_atomic_wait.dll' = '3D0CBFAA1BF3EECF5A3F4491D2960EE803CB994F30292C6ADC4A07C498F60E2B'
    'msvcp140_codecvt_ids.dll' = '8A65C7596EF2E6938731F5A1058E7E40145B6D97967CC649231A076B9A608D78'
    'msvcp140.dll' = '7C26614E1D733892C2DEAC7E245CE115504B1D80592DD0A01B08E3E5A55F89CA'
    'NOTICE.md' = '449C00A17B73956B7A6BC55EF2EDE796367A7A843492FD528E437CA6C8614646'
    'qpdf.exe' = '57C003E868FB66CD343FD5AFB91BE8C2277F56434EEA8635762499731BF9F60D'
    'qpdf30.dll' = '36FE5B2023F244E5785D96B8372DBA26B75EA51B63ADF4D4F1E66AD0AA1C8A61'
    'SHA256SUMS' = 'EB9EA6CA58642E5A36C6158E0F343B0002FC5AEA79CD717B66DE77C6C764776E'
    'vcruntime140_1.dll' = 'A7146C08F89FE5B04541AB507CDB59FF7B44534D4BA3C668A426C6450A03434E'
    'vcruntime140.dll' = 'D1F4225DF2CD877DBF130D5668A021DCE3F94118455FF5EC952061C30AFC9CE7'
}
foreach ($name in $qpdfHashes.Keys) {
    $asset = Join-Path $qpdfRoot $name
    if (-not (Test-Path -LiteralPath $asset -PathType Leaf)) { throw "Pinned qpdf asset is missing: $asset" }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $asset).Hash.ToUpperInvariant()
    if ($actual -ne $qpdfHashes[$name]) { throw "Pinned qpdf asset hash mismatch: $name" }
}

$resolvedResource = [IO.Path]::GetFullPath($resourceRoot)
$expectedSuffix = [IO.Path]::GetFullPath((Join-Path $appRoot 'src-tauri/resources/modules/qing.pdf'))
if ($resolvedResource -ne $expectedSuffix) { throw "Refusing to write outside the Qing PDF resource root: $resolvedResource" }
if (Test-Path -LiteralPath $resolvedResource) {
    Get-ChildItem -LiteralPath $resolvedResource -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Force -Path $resolvedResource | Out-Null
}
New-Item -ItemType Directory -Force -Path (Join-Path $resolvedResource 'bin'), (Join-Path $resolvedResource 'ui'), (Join-Path $resolvedResource 'i18n'), (Join-Path $resolvedResource 'third-party/qpdf') | Out-Null
Copy-Item -LiteralPath (Join-Path $moduleRoot 'module.json') -Destination $resolvedResource -Force
Copy-Item -LiteralPath (Join-Path $moduleRoot 'icon.svg') -Destination $resolvedResource -Force
Copy-Item -LiteralPath $binary -Destination (Join-Path $resolvedResource 'bin/qing-pdf-module.exe') -Force
Copy-Item -Path (Join-Path $uiDist '*') -Destination (Join-Path $resolvedResource 'ui') -Recurse -Force
Copy-Item -Path (Join-Path $moduleRoot 'i18n/*') -Destination (Join-Path $resolvedResource 'i18n') -Force
Copy-Item -Path (Join-Path $qpdfRoot '*') -Destination (Join-Path $resolvedResource 'third-party/qpdf') -Force
Write-Host "Qing PDF module installed at $resolvedResource"
