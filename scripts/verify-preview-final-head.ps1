[CmdletBinding()]
param(
    [string]$Workflow = "preview-release-validation.yml",
    [int]$DiscoveryTimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$runId = $null
$runUrl = $null
$head = $null
. (Join-Path $PSScriptRoot 'preview-final-head-helpers.ps1')

function Invoke-Git {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    $output = @(& git -C $repoRoot @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Git failed: git $($Arguments -join ' ')`n$($output -join "`n")" }
    return $output
}

function Get-Run {
    param([Parameter(Mandatory = $true)][long]$Id)
    $json = & gh run view $Id --repo QingMo-A/QingToolbox `
        --json databaseId,event,headBranch,headSha,status,conclusion,url 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Unable to inspect workflow run $Id.`n$($json -join "`n")" }
    $runs = @(ConvertFrom-GhJsonLines -Lines $json)
    if ($runs.Count -ne 1) { throw "Expected one workflow run from gh run view, found $($runs.Count)." }
    return $runs[0]
}

try {
    $branch = ([string](Invoke-Git @('branch', '--show-current'))).Trim()
    if ($branch -ne 'toolbox') { throw "Final Preview verification requires toolbox; current branch: '$branch'." }
    $dirty = @(Invoke-Git @('status', '--porcelain=v1', '--untracked-files=all') |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($dirty.Count -ne 0) { throw "Final Preview verification requires a clean worktree.`n$($dirty -join "`n")" }

    [void](Invoke-Git @('fetch', '--quiet', '--no-tags', 'origin', 'toolbox'))
    $head = ([string](Invoke-Git @('rev-parse', 'HEAD'))).Trim().ToLowerInvariant()
    $origin = ([string](Invoke-Git @('rev-parse', 'refs/remotes/origin/toolbox'))).Trim().ToLowerInvariant()
    if ($head -ne $origin) { throw "HEAD is not synchronized with origin/toolbox. HEAD $head, origin $origin." }

    & gh auth status
    if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI authentication is unavailable.' }

    $beforeJson = & gh run list --repo QingMo-A/QingToolbox --workflow $Workflow `
        --branch toolbox --event workflow_dispatch --limit 100 `
        --json databaseId 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Unable to list existing workflow runs.`n$($beforeJson -join "`n")" }
    $beforeIds = [Collections.Generic.HashSet[long]]::new()
    $existingRuns = @(ConvertFrom-GhJsonLines -Lines $beforeJson)
    foreach ($run in $existingRuns) { [void]$beforeIds.Add([long]$run.databaseId) }

    $dispatchOutput = @(& gh workflow run $Workflow --repo QingMo-A/QingToolbox --ref toolbox 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Workflow dispatch failed.`n$($dispatchOutput -join "`n")" }
    Write-Host ($dispatchOutput -join "`n")
    $dispatchText = $dispatchOutput -join "`n"
    $urlMatch = [regex]::Match($dispatchText, '/actions/runs/(?<id>\d+)')
    if ($urlMatch.Success) { $runId = [long]$urlMatch.Groups['id'].Value }

    if ($null -eq $runId) {
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds($DiscoveryTimeoutSeconds)
        do {
            Start-Sleep -Seconds 2
            $candidateJson = & gh run list --repo QingMo-A/QingToolbox --workflow $Workflow `
                --branch toolbox --event workflow_dispatch --limit 20 `
                --json databaseId,event,headBranch,headSha,status,conclusion,url 2>&1
            if ($LASTEXITCODE -ne 0) { throw "Unable to discover the dispatched workflow run.`n$($candidateJson -join "`n")" }
            $listedRuns = @(ConvertFrom-GhJsonLines -Lines $candidateJson)
            $candidates = @(Get-ExactNewWorkflowRuns -Runs $listedRuns `
                -ExistingRunIds $beforeIds -HeadSha $head)
            $candidate = Resolve-UniqueExactWorkflowRun -Candidates $candidates
            if ($null -ne $candidate) { $runId = [long]$candidate.databaseId }
        } while ($null -eq $runId -and [DateTimeOffset]::UtcNow -lt $deadline)
        if ($null -eq $runId) { throw "The newly dispatched exact-HEAD workflow run was not found within $DiscoveryTimeoutSeconds seconds." }
    }

    Assert-NewWorkflowRunId -RunId $runId -ExistingRunIds $beforeIds

    $initialRun = Get-Run $runId
    $runUrl = $initialRun.url
    Assert-ExactWorkflowRun -Run $initialRun -HeadSha $head
    Write-Host "Watching exact Preview validation run $runId for $head"
    Write-Host $runUrl
    & gh run watch $runId --repo QingMo-A/QingToolbox --exit-status
    $watchExitCode = $LASTEXITCODE

    $finalRun = Get-Run $runId
    $runUrl = $finalRun.url
    Assert-ExactWorkflowRun -Run $finalRun -HeadSha $head
    if ($watchExitCode -ne 0 -or $finalRun.status -ne 'completed' -or $finalRun.conclusion -ne 'success') {
        throw "Exact-HEAD workflow validation did not succeed."
    }

    Write-Host "Preview final HEAD verification passed."
    Write-Host "Run ID:     $runId"
    Write-Host "Run URL:    $runUrl"
    Write-Host "Head SHA:   $($finalRun.headSha)"
    Write-Host "Status:     $($finalRun.status)"
    Write-Host "Conclusion: $($finalRun.conclusion)"
}
catch {
    $failure = $_
    Write-Host "Run ID:     $runId"
    Write-Host "Run URL:    $runUrl"
    Write-Host "Head SHA:   $head"
    if ($null -ne $runId) {
        try {
            $failedRun = Get-Run $runId
            Write-Host "Status:     $($failedRun.status)"
            Write-Host "Conclusion: $($failedRun.conclusion)"
        }
        catch { Write-Host "Status:     unavailable`nConclusion: unavailable" }
    }
    Write-Error -ErrorRecord $failure
    exit 1
}
