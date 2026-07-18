function ConvertFrom-GhJsonLines {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Lines
    )

    $json = $Lines -join "`n"
    if ([string]::IsNullOrWhiteSpace($json)) {
        throw 'GitHub CLI returned an empty JSON response.'
    }

    $items = $json | ConvertFrom-Json
    foreach ($item in $items) {
        Write-Output $item
    }
}

function Assert-NewWorkflowRunId {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][long]$RunId,
        [Parameter(Mandatory = $true)][Collections.Generic.HashSet[long]]$ExistingRunIds
    )

    if ($ExistingRunIds.Contains($RunId)) {
        throw "Dispatched workflow run $RunId already existed before dispatch; refusing stale run evidence."
    }
}

function Get-ExactNewWorkflowRuns {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Runs,
        [Parameter(Mandatory = $true)][Collections.Generic.HashSet[long]]$ExistingRunIds,
        [Parameter(Mandatory = $true)][string]$HeadSha,
        [string]$Branch = 'toolbox'
    )

    foreach ($run in $Runs) {
        $runId = [long]$run.databaseId
        if (-not $ExistingRunIds.Contains($runId) -and
            $run.event -eq 'workflow_dispatch' -and
            $run.headBranch -eq $Branch -and
            $run.headSha -eq $HeadSha) {
            Write-Output $run
        }
    }
}

function Resolve-UniqueExactWorkflowRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Candidates
    )

    if ($Candidates.Count -gt 1) {
        throw 'More than one new exact-HEAD workflow run was found; refusing an ambiguous result.'
    }
    if ($Candidates.Count -eq 0) {
        return $null
    }
    return $Candidates[0]
}

function Assert-ExactWorkflowRun {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$HeadSha,
        [string]$Branch = 'toolbox'
    )

    if ($Run.event -ne 'workflow_dispatch') {
        throw "Run $($Run.databaseId) event is '$($Run.event)', not workflow_dispatch."
    }
    if ($Run.headBranch -ne $Branch) {
        throw "Run $($Run.databaseId) branch is '$($Run.headBranch)', not $Branch."
    }
    if ($Run.headSha -ne $HeadSha) {
        throw "Run $($Run.databaseId) head SHA $($Run.headSha) does not match final HEAD $HeadSha."
    }
}
