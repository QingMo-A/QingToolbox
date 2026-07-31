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

$canaryPath = Join-Path $PSScriptRoot 'test-development-web-shell.ps1'
$workflowPath = Join-Path (Split-Path -Parent $PSScriptRoot) '.github\workflows\preview-release-validation.yml'
$canary = Get-Content -LiteralPath $canaryPath -Raw
$workflow = Get-Content -LiteralPath $workflowPath -Raw

if ($canary -notmatch '\[int\]\$TimeoutSeconds\s*=\s*(?<seconds>\d+)' -or
    [int]$Matches.seconds -lt 120) {
    throw 'Development Web Shell canary default timeout must be at least 120 seconds.'
}
foreach ($required in @(
    '-TimeoutSeconds 120',
    '-DiagnosticsDirectory "${{ runner.temp }}\QingToolboxWebShellCanary"',
    '${{ runner.temp }}\QingToolboxWebShellCanary\**',
    '"${{ runner.temp }}\QingToolboxWebShellCanary"')) {
    if (-not $workflow.Contains($required)) { throw "Preview workflow is missing Web Shell canary contract: $required" }
}
$canaryStep = [regex]::Match($workflow,
    '(?ms)^\s{6}- name: Run Development Web Shell canary\s+.*?(?=^\s{6}- name:)').Value
if ([string]::IsNullOrWhiteSpace($canaryStep) -or
    $canaryStep -match 'continue-on-error|\|\|\s*true|SilentlyContinue') {
    throw 'Development Web Shell canary must remain a blocking workflow step.'
}
$uploadStep = [regex]::Match($workflow,
    '(?ms)^\s{6}- name: Upload Preview validation diagnostics\s+.*?(?=^\s{6}- name:)').Value
if ($uploadStep -notmatch 'if:\s*always\(\)' -or
    -not $uploadStep.Contains('${{ runner.temp }}\QingToolboxWebShellCanary\**')) {
    throw 'Always-run diagnostics upload must include the Web Shell canary directory.'
}
$releaseArtifactStep = [regex]::Match($workflow,
    '(?ms)^\s{6}- name: Upload Preview validation artifacts\s+.*$').Value
foreach ($required in @('installer_file }}', 'installer_file }}.sha256', 'manifest_file }}')) {
    if (-not $releaseArtifactStep.Contains($required)) { throw "Release artifact contract is missing: $required" }
}
if ($releaseArtifactStep -match 'QingToolboxWebShellCanary|portable|\.qmod') {
    throw 'Canary diagnostics or retired distributions leaked into release artifacts.'
}

$shellRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'QingToolbox.Shell'
$mainWindow = Get-Content -LiteralPath (Join-Path $shellRoot 'MainWindow.xaml') -Raw
$shellTheme = Get-Content -LiteralPath (Join-Path $shellRoot 'Resources\ShellTheme.xaml') -Raw
if ($mainWindow -notmatch '\{StaticResource\s+SubtleBorderBrush\}' -or
    $shellTheme -notmatch 'x:Key="SubtleBorderBrush"') {
    throw 'MainWindow SubtleBorderBrush references must resolve through ShellTheme.xaml.'
}

Write-Host 'Preview final HEAD PowerShell contracts passed.'
