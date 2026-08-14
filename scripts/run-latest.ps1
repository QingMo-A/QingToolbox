[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$Profile = 'Shell',
    [switch]$SkipUpdate,
    [switch]$NoLaunch,
    [switch]$EnableWebDevTools,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'local-environment-common.ps1')

function Add-ProcessPath {
    param([Parameter(Mandatory = $true)][string]$Directory)
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { return }
    $entries = @($env:Path -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if (-not ($entries | Where-Object { $_.TrimEnd('\') -ieq $Directory.TrimEnd('\') })) {
        $env:Path = "$Directory;$env:Path"
    }
}

function Resolve-NodeToolchain {
    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    if ($null -eq $node) {
        $candidates = New-Object 'System.Collections.Generic.List[string]'
        if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
            $candidates.Add((Join-Path $env:ProgramFiles 'nodejs'))
        }
        if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
            $candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'nodejs'))
        }
        if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
            $candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\nodejs'))
        }
        if (-not [string]::IsNullOrWhiteSpace($env:USERPROFILE)) {
            $candidates.Add((Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin'))
        }
        foreach ($candidate in $candidates) {
            if (Test-Path -LiteralPath (Join-Path $candidate 'node.exe') -PathType Leaf) {
                Add-ProcessPath -Directory $candidate
                $node = Get-Command node.exe -ErrorAction SilentlyContinue
                if ($null -ne $node) { break }
            }
        }
    }
    if ($null -eq $node) {
        throw 'Node.js was not found. Install Node.js 24 LTS once; the launcher will discover it automatically afterwards.'
    }
    Add-ProcessPath -Directory (Split-Path -Parent $node.Source)
    $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
    return [pscustomobject]@{ Node = $node.Source; Npm = if ($null -eq $npm) { $null } else { $npm.Source } }
}

function Stop-WorkspaceDevelopmentHost {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)
    & (Join-Path $PSScriptRoot 'stop-dev-host.ps1') -RepositoryRoot $RepositoryRoot
}

function Repair-WebAssets {
    param(
        [Parameter(Mandatory = $true)]$Toolchain,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )
    $verify = Join-Path $PSScriptRoot 'verify-web-ui-assets.ps1'
    try {
        & $verify -NodePath $Toolchain.Node
        return
    }
    catch {
        Write-Warning "Web workspace assets need repair: $($_.Exception.Message)"
    }
    if ([string]::IsNullOrWhiteSpace($Toolchain.Npm)) {
        $webRoot = Join-Path $RepositoryRoot 'QingToolbox.WebUI'
        $vueTypeCheck = Join-Path $webRoot 'node_modules\vue-tsc\bin\vue-tsc.js'
        $vite = Join-Path $webRoot 'node_modules\vite\bin\vite.js'
        if (-not (Test-Path -LiteralPath $vueTypeCheck -PathType Leaf) -or
            -not (Test-Path -LiteralPath $vite -PathType Leaf)) {
            throw 'Web assets are stale, npm is unavailable, and local Web dependencies are missing. Repair or reinstall Node.js 24 LTS once, then run this launcher again.'
        }
        Write-Warning 'npm is unavailable; repairing with the existing verified local Web dependencies.'
        Push-Location $webRoot
        try {
            & $Toolchain.Node $vueTypeCheck --noEmit
            if ($LASTEXITCODE -ne 0) { throw 'WebUI type checking failed during automatic repair.' }
            & $Toolchain.Node $vite build
            if ($LASTEXITCODE -ne 0) { throw 'WebUI production build failed during automatic repair.' }
            & $Toolchain.Node (Join-Path $webRoot 'tools\assets.mjs') generate
            if ($LASTEXITCODE -ne 0) { throw 'WebUI asset identity generation failed during automatic repair.' }
        }
        finally { Pop-Location }
        & $verify -NodePath $Toolchain.Node
        return
    }
    Write-Host 'Rebuilding Web workspace assets automatically...'
    & (Join-Path $PSScriptRoot 'build-web-ui.ps1') -SkipTests
    & $verify -NodePath $Toolchain.Node
}

function Update-CheckoutIfSafe {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)
    $branch = (& git -C $RepositoryRoot branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -cne 'toolbox') {
        throw "Current branch is '$branch'; run-latest requires the toolbox worktree."
    }
    if ($SkipUpdate) {
        Write-Host 'Remote update skipped by request.'
        return
    }
    $changes = @(& git -C $RepositoryRoot status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the Git working tree.' }
    if ($changes.Count -gt 0) {
        Write-Warning 'Local changes are present. The launcher will preserve them and skip the remote update.'
        return
    }
    & git -C $RepositoryRoot fetch origin toolbox
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'The remote update could not be checked. Continuing with the current local checkout.'
        return
    }
    $counts = ((& git -C $RepositoryRoot rev-list --left-right --count origin/toolbox...HEAD) -split '\s+')
    if ($LASTEXITCODE -ne 0 -or $counts.Count -lt 2) { throw 'Unable to compare the local and remote toolbox revisions.' }
    $behind = [int]$counts[0]
    $ahead = [int]$counts[1]
    if ($behind -gt 0 -and $ahead -gt 0) {
        throw 'Local toolbox history has diverged from origin/toolbox. Resolve it explicitly; the launcher will not rewrite history.'
    }
    if ($behind -gt 0) {
        & git -C $RepositoryRoot merge --ff-only origin/toolbox
        if ($LASTEXITCODE -ne 0) { throw 'Fast-forward update from origin/toolbox failed.' }
    }
    elseif ($ahead -gt 0) {
        Write-Warning "The local toolbox branch is $ahead commit(s) ahead of origin/toolbox; launching the local revision."
    }
    else {
        Write-Host 'The toolbox checkout is already current.'
    }
}

$profileInfo = Resolve-LocalEnvironmentProfile -Environment Development -Profile $Profile
Assert-NoLocalProfileReparsePoints -ProfileInfo $profileInfo
$launchArguments = @('--environment', 'Development', '--profile', $Profile, '--repo-root', $profileInfo.RepoRoot)
if ($EnableWebDevTools) { $launchArguments += '--web-devtools' }
if ($ValidateOnly) {
    Write-Output "RepositoryRoot: $($profileInfo.RepoRoot)"
    foreach ($item in $launchArguments) { Write-Output "Argument: $item" }
    exit 0
}

$launcherDirectory = Join-Path $profileInfo.ProfileRoot 'launcher'
[IO.Directory]::CreateDirectory($launcherDirectory) > $null
$logPath = Join-Path $launcherDirectory ("run-latest-{0:yyyyMMdd-HHmmss}.log" -f [DateTime]::Now)
$transcribing = $false
try {
    Start-Transcript -LiteralPath $logPath -Force > $null
    $transcribing = $true
    Write-Host "Launcher diagnostics: $logPath"

    Write-Host '[1/5] Checking and safely updating the toolbox checkout...'
    Update-CheckoutIfSafe -RepositoryRoot $profileInfo.RepoRoot

    Write-Host '[2/5] Stopping only this workspace development host...'
    Stop-WorkspaceDevelopmentHost -RepositoryRoot $profileInfo.RepoRoot

    Write-Host '[3/5] Checking the Web workspace and repairing stale assets...'
    $toolchain = Resolve-NodeToolchain
    & $toolchain.Node --version
    Repair-WebAssets -Toolchain $toolchain -RepositoryRoot $profileInfo.RepoRoot

    Write-Host '[4/5] Building the solution and deploying the development module...'
    try {
        & (Join-Path $PSScriptRoot 'deploy-dev-modules.ps1') -Configuration $Configuration
    }
    catch {
        if ($_.Exception.Message -match '(?i)being used by another process|process cannot access|MSB302[17]|0x80070020') {
            Write-Warning 'A stale development process locked build output. Stopping it and retrying once.'
            Stop-WorkspaceDevelopmentHost -RepositoryRoot $profileInfo.RepoRoot
            & (Join-Path $PSScriptRoot 'deploy-dev-modules.ps1') -Configuration $Configuration
        }
        else { throw }
    }

    $shellTargetFramework = 'net10.0-windows10.0.17763.0'
    $shell = Join-Path $profileInfo.RepoRoot "QingToolbox.Shell\bin\$Configuration\$shellTargetFramework\QingToolbox.Shell.exe"
    if (-not (Test-Path -LiteralPath $shell -PathType Leaf)) {
        throw "Shell executable was not produced: $shell"
    }
    if ($NoLaunch) {
        Write-Host '[5/5] Build and automatic repairs completed; launch skipped by request.'
    }
    else {
        Write-Host '[5/5] Starting QingToolbox Development Shell...'
        $quotedRepositoryRoot = '"' + $profileInfo.RepoRoot.Replace('"', '\"') + '"'
        $processArguments = @('--environment', 'Development', '--profile', $Profile,
            '--repo-root', $quotedRepositoryRoot)
        if ($EnableWebDevTools) { $processArguments += '--web-devtools' }
        $process = Start-Process -FilePath $shell -ArgumentList $processArguments -PassThru
        Start-Sleep -Milliseconds 1800
        if ($process.HasExited) {
            throw "The Development Shell exited during startup with code $($process.ExitCode). Review its profile log and $logPath."
        }
        Write-Host "QingToolbox Development Shell started (PID $($process.Id))."
    }
}
catch {
    Write-Error "QingToolbox startup failed: $($_.Exception.Message)`nDiagnostics: $logPath"
    exit 1
}
finally {
    if ($transcribing) { Stop-Transcript > $null }
}

exit 0
