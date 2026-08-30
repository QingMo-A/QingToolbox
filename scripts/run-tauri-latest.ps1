[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$SkipUpdate,
    [switch]$NoLaunch,
    [switch]$SkipModuleBuild,
    [switch]$Smoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$global:LASTEXITCODE = 0

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$tauriRoot = Join-Path $repoRoot 'QingToolbox.Tauri'
$rustRoot = Join-Path $tauriRoot 'src-tauri'
$artifactExe = Join-Path $repoRoot 'artifacts/tauri-production/QingToolbox/QingToolbox.exe'

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )
    Write-Host "`n==> $Label"
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE." }
}

function Assert-TauriCheckout {
    $branch = (& git -C $repoRoot branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -cne 'toolbox') {
        throw "Current branch is '$branch'; the Tauri development entry requires the toolbox branch."
    }
    foreach ($marker in @(
        'QingToolbox.Tauri/package.json',
        'QingToolbox.Tauri/src-tauri/Cargo.toml',
        'scripts/build-tauri-production.ps1'
    )) {
        if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $marker) -PathType Leaf)) {
            throw "Tauri checkout is incomplete; missing $marker."
        }
    }
}

function Update-TauriCheckoutIfSafe {
    if ($SkipUpdate) {
        Write-Host 'Remote update skipped by request.'
        return
    }
    $changes = @(& git -C $repoRoot status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the Git working tree.' }
    if ($changes.Count -gt 0) {
        Write-Warning 'Local changes are present; preserving them and skipping the remote update.'
        return
    }
    & git -C $repoRoot fetch origin toolbox
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'The remote toolbox revision could not be checked; continuing locally.'
        return
    }
    $counts = ((& git -C $repoRoot rev-list --left-right --count origin/toolbox...HEAD) -split '\s+')
    if ($LASTEXITCODE -ne 0 -or $counts.Count -lt 2) { throw 'Unable to compare toolbox revisions.' }
    $behind = [int]$counts[0]
    $ahead = [int]$counts[1]
    if ($behind -gt 0 -and $ahead -gt 0) {
        throw 'Local toolbox history diverged from origin/toolbox; resolve it explicitly.'
    }
    if ($behind -gt 0) {
        & git -C $repoRoot merge --ff-only origin/toolbox
        if ($LASTEXITCODE -ne 0) { throw 'Fast-forward update from origin/toolbox failed.' }
    } elseif ($ahead -gt 0) {
        Write-Warning "Local toolbox is $ahead commit(s) ahead of origin/toolbox; using the local revision."
    } else {
        Write-Host 'The toolbox checkout is already current.'
    }
}

function Resolve-Node {
    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($node) { return $node.Source }
    $candidates = @(
        (Join-Path ${env:ProgramFiles} 'nodejs/node.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'nodejs/node.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs/nodejs/node.exe'),
        (Join-Path $HOME '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe')
    )
    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    throw 'Node.js was not found. Install Node.js 24 LTS before starting the Tauri host.'
}

function Resolve-Cargo {
    $cargo = Get-Command cargo.exe -ErrorAction SilentlyContinue
    if ($cargo) { return $cargo.Source }
    $candidate = Join-Path $HOME '.cargo/bin/cargo.exe'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    throw 'cargo was not found. Install Rust stable before starting the Tauri host.'
}

Assert-TauriCheckout
Update-TauriCheckoutIfSafe
$nodePath = Resolve-Node
$cargoPath = Resolve-Cargo
$env:Path = "$(Split-Path -Parent $cargoPath);$(Split-Path -Parent $nodePath);$env:Path"

if ($Configuration -eq 'Release') {
    $arguments = @{}
    if ($SkipModuleBuild) { $arguments.SkipModuleBuild = $true }
    if ($Smoke) { $arguments.Smoke = $true }
    Invoke-Checked -Label 'Build Tauri production candidate' -Action {
        & (Join-Path $PSScriptRoot 'build-tauri-production.ps1') @arguments
    }
    if ($NoLaunch) {
        Write-Host "Release candidate prepared: $artifactExe"
        exit 0
    }
    if (-not (Test-Path -LiteralPath $artifactExe -PathType Leaf)) {
        throw "Tauri production executable is missing: $artifactExe"
    }
    $workingDirectory = Split-Path -Parent $artifactExe
    $process = Start-Process -FilePath $artifactExe -WorkingDirectory $workingDirectory -PassThru
    Start-Sleep -Milliseconds 1200
    if ($process.HasExited) {
        throw "Tauri production host exited during startup with code $($process.ExitCode)."
    }
    Write-Host "QingToolbox Tauri Release started (PID $($process.Id))."
    exit 0
}

if (-not $SkipModuleBuild) {
    foreach ($module in @('canary', 'launcher', 'pdf', 'transfer', 'texttools', 'windowtopmost', 'powerguard', 'screenpin')) {
        Invoke-Checked -Label "Build Tauri $module module" -Action {
            & (Join-Path $PSScriptRoot "build-tauri-$module.ps1")
        }
    }
}
Push-Location $tauriRoot
try {
    Invoke-Checked -Label 'Typecheck Tauri Vue frontend' -Action { npm run typecheck }
    if ($NoLaunch) {
        Invoke-Checked -Label 'Build Tauri Vue frontend' -Action { npm run build }
        Write-Host 'Tauri Debug build completed; launch skipped by request.'
        exit 0
    }
    $env:QING_TAURI_ALLOW_WORKSPACE_RESOURCES = '1'
    $env:QING_TAURI_STARTUP_PRESENTATION = 'main'
    $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if (-not $npm) { throw 'npm.cmd was not found.' }
    $process = Start-Process -FilePath $npm.Source -WorkingDirectory $tauriRoot -ArgumentList @('run', 'tauri', '--', 'dev') -PassThru
    Start-Sleep -Milliseconds 1800
    if ($process.HasExited) {
        throw "Tauri Debug host exited during startup with code $($process.ExitCode)."
    }
    Write-Host "QingToolbox Tauri Debug started (PID $($process.Id))."
}
finally {
    Pop-Location
}
