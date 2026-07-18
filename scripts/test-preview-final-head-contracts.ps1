[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'preview-final-head-helpers.ps1')

function Assert-Throws {
    param([Parameter(Mandatory = $true)][scriptblock]$Action)

    try { & $Action }
    catch { return }
    throw 'Expected the action to fail closed.'
}

$jsonCases = @(
    @{ Lines = @('[]'); ExpectedIds = @() },
    @{ Lines = @('[{"databaseId":1}]'); ExpectedIds = @(1L) },
    @{ Lines = @('[{"databaseId":1},{"databaseId":2}]'); ExpectedIds = @(1L, 2L) },
    @{ Lines = @('[{"databaseId":1},', '{"databaseId":2}]'); ExpectedIds = @(1L, 2L) }
)
foreach ($case in $jsonCases) {
    $items = @(ConvertFrom-GhJsonLines -Lines $case.Lines)
    $actualIds = @($items | ForEach-Object { [long]$_.databaseId })
    if ($actualIds.Count -ne $case.ExpectedIds.Count -or
        (Compare-Object -ReferenceObject $case.ExpectedIds -DifferenceObject $actualIds)) {
        throw "GitHub run JSON enumeration mismatch for: $($case.Lines -join '')"
    }
    if (@($items | Where-Object { $_ -is [array] }).Count -ne 0) {
        throw 'GitHub run JSON enumeration produced a nested array.'
    }
}

$beforeIds = [Collections.Generic.HashSet[long]]::new()
[void]$beforeIds.Add(41L)
Assert-Throws { Assert-NewWorkflowRunId -RunId 41L -ExistingRunIds $beforeIds }
Assert-NewWorkflowRunId -RunId 42L -ExistingRunIds $beforeIds

$head = '0123456789abcdef'
function New-TestRun([long]$Id, [string]$Event = 'workflow_dispatch',
    [string]$Branch = 'toolbox', [string]$Sha = $head) {
    [pscustomobject]@{
        databaseId = $Id
        event = $Event
        headBranch = $Branch
        headSha = $Sha
    }
}

$runs = @(
    (New-TestRun 41L),
    (New-TestRun 42L 'push'),
    (New-TestRun 43L 'workflow_dispatch' 'main'),
    (New-TestRun 44L 'workflow_dispatch' 'toolbox' 'wrong-sha'),
    (New-TestRun 45L)
)
$exact = @(Get-ExactNewWorkflowRuns -Runs $runs -ExistingRunIds $beforeIds -HeadSha $head)
if ($exact.Count -ne 1 -or $exact[0].databaseId -ne 45L) {
    throw 'Exact workflow run filtering accepted an invalid identity or rejected the valid identity.'
}

$valid = New-TestRun 46L
Assert-ExactWorkflowRun -Run $valid -HeadSha $head
Assert-Throws { Assert-ExactWorkflowRun -Run (New-TestRun 47L 'push') -HeadSha $head }
Assert-Throws { Assert-ExactWorkflowRun -Run (New-TestRun 48L 'workflow_dispatch' 'main') -HeadSha $head }
Assert-Throws { Assert-ExactWorkflowRun -Run (New-TestRun 49L 'workflow_dispatch' 'toolbox' 'wrong-sha') -HeadSha $head }

if ($null -ne (Resolve-UniqueExactWorkflowRun -Candidates @())) {
    throw 'Zero exact workflow candidates did not resolve to null.'
}
$single = Resolve-UniqueExactWorkflowRun -Candidates @($valid)
if ($single.databaseId -ne 46L) { throw 'One exact workflow candidate was not returned.' }
Assert-Throws { Resolve-UniqueExactWorkflowRun -Candidates @($valid, (New-TestRun 50L)) }

Write-Host 'Preview final HEAD PowerShell contracts passed.'
