[CmdletBinding()]
param(
    [switch]$BuildDesktop,
    [switch]$SkipCanary,
    [switch]$SmokeDesktop,
    [switch]$SmokeEverything,
    [switch]$SkipPdf,
    [switch]$SkipTransfer,
    [switch]$SkipTextTools,
    [switch]$SkipWindowTopmost,
    [switch]$SkipPowerGuard,
    [switch]$SkipScreenPin
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

$cargoCommand = Get-Command cargo -ErrorAction SilentlyContinue
if ($cargoCommand) {
    $cargoPath = $cargoCommand.Source
} else {
    $cargoPath = Join-Path $HOME '.cargo\bin\cargo.exe'
}
if (-not (Test-Path -LiteralPath $cargoPath)) {
    throw 'cargo was not found. Install Rust stable before running the Tauri verification.'
}

$canaryExecutable = Join-Path $appRoot 'src-tauri/resources/modules/qing.canary/bin/qing-module-canary.exe'
$launcherExecutable = Join-Path $appRoot 'src-tauri/resources/modules/qing.launcher/bin/qing-launcher-module.exe'
$pdfExecutable = Join-Path $appRoot 'src-tauri/resources/modules/qing.pdf/bin/qing-pdf-module.exe'
$transferExecutable = Join-Path $appRoot 'src-tauri/resources/modules/qing.qingtransfer/bin/qing-transfer-module.exe'
$textToolsExecutable = Join-Path $appRoot 'src-tauri/resources/modules/qing.texttools/bin/qing-texttools-module.exe'
$windowTopmostExecutable = Join-Path $appRoot 'src-tauri/resources/modules/qing.windowtopmost/bin/qing-windowtopmost-module.exe'
$powerGuardExecutable = Join-Path $appRoot 'src-tauri/resources/modules/qing.powerguard/bin/qing-powerguard-module.exe'
$screenPinExecutable = Join-Path $appRoot 'src-tauri/resources/modules/qing.screenpin/bin/qing-screenpin-module.exe'
$launcherRoot = Join-Path $appRoot 'native-launcher'
$pdfRoot = Join-Path $appRoot 'native-pdf'
$transferRoot = Join-Path $appRoot 'native-transfer'
$textToolsRoot = Join-Path $appRoot 'native-texttools'
$windowTopmostRoot = Join-Path $appRoot 'native-windowtopmost'
$powerGuardRoot = Join-Path $appRoot 'native-powerguard'
$screenPinRoot = Join-Path $appRoot 'native-screenpin'
if (-not $SkipCanary) {
    & (Join-Path $repoRoot 'scripts/build-tauri-canary.ps1')
    & (Join-Path $repoRoot 'scripts/smoke-tauri-canary.ps1') -ExecutablePath $canaryExecutable
}

& (Join-Path $repoRoot 'scripts/build-tauri-launcher.ps1')
if (-not $SkipPdf) {
    & (Join-Path $repoRoot 'scripts/build-tauri-pdf.ps1')
    & (Join-Path $repoRoot 'scripts/smoke-tauri-pdf.ps1') -ExecutablePath $pdfExecutable
}
if (-not $SkipTransfer) {
    & (Join-Path $repoRoot 'scripts/build-tauri-transfer.ps1')
    & (Join-Path $repoRoot 'scripts/smoke-tauri-transfer.ps1') -ExecutablePath $transferExecutable
}
if (-not $SkipTextTools) {
    & (Join-Path $repoRoot 'scripts/build-tauri-texttools.ps1')
    & (Join-Path $repoRoot 'scripts/smoke-tauri-texttools.ps1') -ExecutablePath $textToolsExecutable
}
if (-not $SkipWindowTopmost) {
    & (Join-Path $repoRoot 'scripts/build-tauri-windowtopmost.ps1')
    & (Join-Path $repoRoot 'scripts/smoke-tauri-windowtopmost.ps1') -ExecutablePath $windowTopmostExecutable
}
if (-not $SkipPowerGuard) {
    & (Join-Path $repoRoot 'scripts/build-tauri-powerguard.ps1')
    & (Join-Path $repoRoot 'scripts/smoke-tauri-powerguard.ps1') -ExecutablePath $powerGuardExecutable
}
if (-not $SkipScreenPin) {
    & (Join-Path $repoRoot 'scripts/build-tauri-screenpin.ps1')
    & (Join-Path $repoRoot 'scripts/smoke-tauri-screenpin.ps1') -ExecutablePath $screenPinExecutable
}
if ($SmokeEverything) {
    & (Join-Path $repoRoot 'scripts/smoke-tauri-launcher.ps1') -ExecutablePath $launcherExecutable -Everything
} else {
    & (Join-Path $repoRoot 'scripts/smoke-tauri-launcher.ps1') -ExecutablePath $launcherExecutable
}

Push-Location $launcherRoot
try {
    & $cargoPath fmt -- --check
    if ($LASTEXITCODE -ne 0) { throw "Launcher cargo fmt check failed with exit code $LASTEXITCODE" }
    & $cargoPath test --locked
    if ($LASTEXITCODE -ne 0) { throw "Launcher cargo test failed with exit code $LASTEXITCODE" }
    & $cargoPath clippy --locked --all-targets -- -D warnings
    if ($LASTEXITCODE -ne 0) { throw "Launcher cargo clippy failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

if (-not $SkipPdf) {
    Push-Location $pdfRoot
    try {
        & $cargoPath fmt -- --check
        if ($LASTEXITCODE -ne 0) { throw "Qing PDF cargo fmt check failed with exit code $LASTEXITCODE" }
        & $cargoPath test --locked
        if ($LASTEXITCODE -ne 0) { throw "Qing PDF cargo test failed with exit code $LASTEXITCODE" }
        & $cargoPath clippy --locked --all-targets -- -D warnings
        if ($LASTEXITCODE -ne 0) { throw "Qing PDF cargo clippy failed with exit code $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
}

if (-not $SkipTransfer) {
    Push-Location $transferRoot
    try {
        & $cargoPath fmt -- --check
        if ($LASTEXITCODE -ne 0) { throw "QingTransfer cargo fmt check failed with exit code $LASTEXITCODE" }
        & $cargoPath test --locked
        if ($LASTEXITCODE -ne 0) { throw "QingTransfer cargo test failed with exit code $LASTEXITCODE" }
        & $cargoPath clippy --locked --all-targets -- -D warnings
        if ($LASTEXITCODE -ne 0) { throw "QingTransfer cargo clippy failed with exit code $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
}

if (-not $SkipTextTools) {
    Push-Location $textToolsRoot
    try {
        & $cargoPath fmt -- --check
        if ($LASTEXITCODE -ne 0) { throw "Text Tools cargo fmt check failed with exit code $LASTEXITCODE" }
        & $cargoPath test --locked
        if ($LASTEXITCODE -ne 0) { throw "Text Tools cargo test failed with exit code $LASTEXITCODE" }
        & $cargoPath clippy --locked --all-targets -- -D warnings
        if ($LASTEXITCODE -ne 0) { throw "Text Tools cargo clippy failed with exit code $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
}

if (-not $SkipWindowTopmost) {
    Push-Location $windowTopmostRoot
    try {
        & $cargoPath fmt -- --check
        if ($LASTEXITCODE -ne 0) { throw "Window Topmost cargo fmt check failed with exit code $LASTEXITCODE" }
        & $cargoPath test --locked
        if ($LASTEXITCODE -ne 0) { throw "Window Topmost cargo test failed with exit code $LASTEXITCODE" }
        & $cargoPath clippy --locked --all-targets -- -D warnings
        if ($LASTEXITCODE -ne 0) { throw "Window Topmost cargo clippy failed with exit code $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
}

if (-not $SkipPowerGuard) {
    Push-Location $powerGuardRoot
    try {
        & $cargoPath fmt -- --check
        if ($LASTEXITCODE -ne 0) { throw "PowerGuard cargo fmt check failed with exit code $LASTEXITCODE" }
        & $cargoPath test --locked
        if ($LASTEXITCODE -ne 0) { throw "PowerGuard cargo test failed with exit code $LASTEXITCODE" }
        & $cargoPath clippy --locked --all-targets -- -D warnings
        if ($LASTEXITCODE -ne 0) { throw "PowerGuard cargo clippy failed with exit code $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
}

if (-not $SkipScreenPin) {
    Push-Location $screenPinRoot
    try {
        & $cargoPath fmt -- --check
        if ($LASTEXITCODE -ne 0) { throw "Screen Pin cargo fmt check failed with exit code $LASTEXITCODE" }
        & $cargoPath test --locked
        if ($LASTEXITCODE -ne 0) { throw "Screen Pin cargo test failed with exit code $LASTEXITCODE" }
        & $cargoPath clippy --locked --all-targets -- -D warnings
        if ($LASTEXITCODE -ne 0) { throw "Screen Pin cargo clippy failed with exit code $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
}

Push-Location $appRoot
try {
    npm run typecheck
    npm run build
} finally {
    Pop-Location
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
    if ($LASTEXITCODE -ne 0) { throw "Tauri host smoke failed with exit code $LASTEXITCODE" }

    $nodeCommand = Get-Command node -ErrorAction SilentlyContinue
    if (-not $nodeCommand) {
        throw 'node was not found. Install Node.js before running the module-window smoke test.'
    }
    & $nodeCommand.Source (Join-Path $repoRoot 'scripts/smoke-tauri-module-window.mjs') $desktopExecutable
    if ($LASTEXITCODE -ne 0) { throw "Tauri module-window smoke failed with exit code $LASTEXITCODE" }
}

Write-Host 'Tauri verification passed.'
