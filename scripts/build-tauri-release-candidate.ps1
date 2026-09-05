[CmdletBinding()]
param(
    [string]$IsccPath,
    [switch]$Describe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$powerShell = (Get-Process -Id $PID).Path

$stageNames = @(
    'Verify Tauri host and all official modules',
    'Package Tauri modules',
    'Verify local environment contracts',
    'Build and smoke Tauri installer',
    'Verify Tauri candidate assets',
    'Verify final source state'
)

if ($Describe) {
    $stageNames | ForEach-Object { Write-Output "Stage: $_" }
    exit 0
}

function Invoke-CandidateStage {
    param(
        [Parameter(Mandatory = $true)][string]$StageName,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    Write-Host "`n==> $StageName"
    $global:LASTEXITCODE = 0
    try {
        & $Action
        $stageSucceeded = $?
        $stageExitCode = $global:LASTEXITCODE
    }
    catch {
        throw "Tauri candidate stage '$StageName' failed: $($_.Exception.Message)"
    }
    if (-not $stageSucceeded -or $stageExitCode -ne 0) {
        throw "Tauri candidate stage '$StageName' failed. " +
            "PowerShellSuccess=$stageSucceeded; ExitCode=$stageExitCode."
    }
}

function Invoke-CandidateScript {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [string[]]$Arguments = @()
    )

    if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) {
        throw "Tauri candidate dependency is missing: $ScriptPath"
    }
    & $powerShell -NoProfile -ExecutionPolicy Bypass -File $ScriptPath @Arguments
}

function Invoke-GitText {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $global:LASTEXITCODE = 0
    $lines = @(& git -C $repoRoot @Arguments)
    if (-not $? -or $global:LASTEXITCODE -ne 0) {
        throw "Git command failed: git $($Arguments -join ' ')"
    }
    return ($lines -join "`n").Trim()
}

function Get-TauriCandidateSource {
    $topLevel = [IO.Path]::GetFullPath((Invoke-GitText @('rev-parse', '--show-toplevel')))
    if (-not $topLevel.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The candidate gate is not running from the expected repository: $topLevel"
    }
    $branch = Invoke-GitText @('branch', '--show-current')
    $isToolboxCiCheckout = $env:GITHUB_ACTIONS -eq 'true' -and
        ($env:GITHUB_REF -eq 'refs/heads/toolbox' -or $env:GITHUB_BASE_REF -eq 'toolbox')
    if ($branch -ne 'toolbox' -and -not $isToolboxCiCheckout) {
        throw "Tauri candidates must be built from the toolbox branch, not '$branch'."
    }
    $commit = Invoke-GitText @('rev-parse', 'HEAD')
    if ($commit -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'Unable to resolve a full source commit for the Tauri candidate.'
    }
    $status = Invoke-GitText @('status', '--porcelain=v1', '--untracked-files=all')
    [pscustomobject]@{
        Branch = if ([string]::IsNullOrWhiteSpace($branch)) {
            'detached CI checkout for toolbox'
        } else {
            $branch
        }
        Commit = $commit.ToLowerInvariant()
        Status = $status
        IsClean = [string]::IsNullOrWhiteSpace($status)
    }
}

function Assert-CleanSource {
    param([Parameter(Mandatory = $true)]$Source)

    if (-not $Source.IsClean) {
        throw "Refusing to build a Tauri candidate from a dirty worktree:`n$($Source.Status)"
    }
}

function Assert-FileSha256Sidecar {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string]$SidecarPath
    )

    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
        throw "Tauri candidate asset is missing: $FilePath"
    }
    if (-not (Test-Path -LiteralPath $SidecarPath -PathType Leaf)) {
        throw "Tauri candidate checksum is missing: $SidecarPath"
    }
    $sidecar = [IO.File]::ReadAllText($SidecarPath).Trim()
    $expectedName = [IO.Path]::GetFileName($FilePath)
    $match = [regex]::Match($sidecar, '^([0-9A-Fa-f]{64})  ([^\r\n]+)$')
    if (-not $match.Success -or $match.Groups[2].Value -cne $expectedName) {
        throw "Tauri candidate checksum sidecar has an invalid identity: $SidecarPath"
    }
    $actual = (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash
    if (-not $actual.Equals($match.Groups[1].Value, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Tauri candidate checksum mismatch: $expectedName"
    }
    return $actual.ToUpperInvariant()
}

function Assert-TauriCandidateAssets {
    param([Parameter(Mandatory = $true)][string]$ExpectedCommit)

    $productionRoot = Join-Path $repoRoot 'artifacts\tauri-production\QingToolbox'
    $manifestPath = Join-Path $productionRoot 'portable-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Tauri production manifest is missing: $manifestPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([bool]$manifest.sourceDirty -or
        -not ([string]$manifest.sourceCommit).Equals(
            $ExpectedCommit, [StringComparison]::OrdinalIgnoreCase) -or
        [string]$manifest.distribution -ne 'production' -or
        [string]$manifest.backend -ne 'rust' -or
        [string]$manifest.framework -ne 'tauri-2' -or
        [string]$manifest.frontend -ne 'vue-3' -or
        [string]$manifest.buildProfile -ne 'release') {
        throw 'Tauri production manifest does not match the clean candidate source and runtime contract.'
    }

    $version = [string]$manifest.version
    if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
        throw "Tauri production manifest has an invalid version: $version"
    }
    $outputRoot = Join-Path $repoRoot 'artifacts\tauri-installer\output'
    $installerName = "QingToolbox-$version-win-x64-tauri-setup.exe"
    $legacyName = "QingToolbox-$version-win-x64-setup.exe"
    $installerPath = Join-Path $outputRoot $installerName
    $sidecarPath = "$installerPath.sha256"
    if (Test-Path -LiteralPath (Join-Path $outputRoot $legacyName)) {
        throw "Legacy WPF installer leaked into the Tauri candidate output: $legacyName"
    }
    $unexpected = @(Get-ChildItem -LiteralPath $outputRoot -File | Where-Object {
        $_.Name -notin @($installerName, "$installerName.sha256")
    })
    if ($unexpected.Count -ne 0) {
        throw "Unexpected files exist in the Tauri candidate output: $($unexpected.Name -join ', ')"
    }
    $hash = Assert-FileSha256Sidecar -FilePath $installerPath -SidecarPath $sidecarPath
    [pscustomobject]@{
        Version = $version
        InstallerPath = $installerPath
        SidecarPath = $sidecarPath
        Sha256 = $hash
        ManifestPath = $manifestPath
    }
}

$initialSource = Get-TauriCandidateSource
Assert-CleanSource $initialSource

Invoke-CandidateStage -StageName $stageNames[0] -Action {
    Invoke-CandidateScript -ScriptPath (Join-Path $PSScriptRoot 'verify-tauri.ps1') `
        -Arguments @('-BuildDesktop', '-SmokeDesktop', '-SmokeEverything')
}
Invoke-CandidateStage -StageName $stageNames[1] -Action {
    Invoke-CandidateScript -ScriptPath (Join-Path $PSScriptRoot 'package-tauri-modules.ps1') `
        -Arguments @('-SkipBuild', '-Smoke')
}
Invoke-CandidateStage -StageName $stageNames[2] -Action {
    Invoke-CandidateScript `
        -ScriptPath (Join-Path $PSScriptRoot 'test-local-environment-contracts.ps1')
}
Invoke-CandidateStage -StageName $stageNames[3] -Action {
    $arguments = @('-Smoke')
    if (-not [string]::IsNullOrWhiteSpace($IsccPath)) {
        $arguments += @('-IsccPath', $IsccPath)
    }
    Invoke-CandidateScript -ScriptPath (Join-Path $PSScriptRoot 'build-tauri-installer.ps1') `
        -Arguments $arguments
}
$candidate = $null
Invoke-CandidateStage -StageName $stageNames[4] -Action {
    $script:candidate = Assert-TauriCandidateAssets -ExpectedCommit $initialSource.Commit
}
Invoke-CandidateStage -StageName $stageNames[5] -Action {
    $finalSource = Get-TauriCandidateSource
    Assert-CleanSource $finalSource
    if ($finalSource.Commit -ne $initialSource.Commit) {
        throw 'HEAD changed while the Tauri candidate gate was running.'
    }
}

Write-Host "`nTauri release candidate gate passed."
Write-Host "Version:       $($candidate.Version)"
Write-Host "Source commit: $($initialSource.Commit)"
Write-Host "Branch:        $($initialSource.Branch)"
Write-Host "Installer:     $($candidate.InstallerPath)"
Write-Host "SHA256:        $($candidate.Sha256)"
Write-Host "Manifest:      $($candidate.ManifestPath)"
Write-Host 'Signing:       required before public release'
Write-Host 'Installer ID:  migration AppId (production cut-over not performed)'
