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

$everythingRoot = Join-Path $moduleRoot 'third-party/Everything'
$everythingHashes = @{
    'Everything.exe' = 'F191F756996A14A11E5445FA7103D302EFD510CF2FBF920E6C0C8ED51D512E36'
    'es.exe' = '3BE7185707E8023CD9295DBCB7A3FA4092A3D8F52B7FA92A0B84243AB40D12F3'
    'Everything64.dll' = '81B5BE18126ACD2C2B913F8F4A821E476B18393CDD3DEBD03387C50AFD8DB88F'
    'LICENSE.txt' = 'C13D19ADCBFD5D07E9512DE9DF99956A3423399ED1FADC5FD33186697AD8DF2F'
    'NOTICE.md' = '35BEFBE14AB7B24657E07B6EC3B481CF17526AD8BC3248211FFAAED39AC7C41B'
}
foreach ($name in $everythingHashes.Keys) {
    $asset = Join-Path $everythingRoot $name
    if (-not (Test-Path -LiteralPath $asset -PathType Leaf)) { throw "Pinned Everything asset is missing: $asset" }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $asset).Hash.ToUpperInvariant()
    if ($actual -ne $everythingHashes[$name]) { throw "Pinned Everything asset hash mismatch: $name" }
}

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
New-Item -ItemType Directory -Force -Path (Join-Path $resolvedResource 'third-party/Everything') | Out-Null
Copy-Item -LiteralPath (Join-Path $moduleRoot 'module.json') -Destination $resolvedResource -Force
Copy-Item -LiteralPath (Join-Path $moduleRoot 'icon.svg') -Destination $resolvedResource -Force
Copy-Item -LiteralPath $binary -Destination (Join-Path $resolvedResource 'bin/qing-launcher-module.exe') -Force
Copy-Item -Path (Join-Path $uiDist '*') -Destination (Join-Path $resolvedResource 'ui') -Recurse -Force
Copy-Item -Path (Join-Path $everythingRoot '*') -Destination (Join-Path $resolvedResource 'third-party/Everything') -Force
Write-Host "Qing Launcher module installed at $resolvedResource"
