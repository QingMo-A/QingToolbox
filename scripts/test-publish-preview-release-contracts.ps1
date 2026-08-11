[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$batPath = Join-Path $repoRoot "publish-preview-release.bat"
$orchestratorPath = Join-Path $PSScriptRoot "publish-preview-release.ps1"

function Assert-Contains {
    param([Parameter(Mandatory = $true)][string]$Text, [Parameter(Mandatory = $true)][string]$Needle, [string]$Message)

    if ($Text.IndexOf($Needle, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        if ([string]::IsNullOrWhiteSpace($Message)) { $Message = "Missing contract: $Needle" }
        throw $Message
    }
}

function Assert-Ordered {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$First,
        [Parameter(Mandatory = $true)][string]$Second,
        [string]$Message
    )

    $firstIndex = $Text.IndexOf($First, [StringComparison]::OrdinalIgnoreCase)
    $secondIndex = $Text.IndexOf($Second, [StringComparison]::OrdinalIgnoreCase)
    if ($firstIndex -lt 0 -or $secondIndex -lt 0 -or $firstIndex -ge $secondIndex) {
        if ([string]::IsNullOrWhiteSpace($Message)) { $Message = "Contract order is invalid: '$First' must precede '$Second'." }
        throw $Message
    }
}

if (-not (Test-Path -LiteralPath $batPath -PathType Leaf)) { throw "Release BAT was not found: $batPath" }
if (-not (Test-Path -LiteralPath $orchestratorPath -PathType Leaf)) { throw "Release orchestrator was not found: $orchestratorPath" }
$bat = Get-Content -LiteralPath $batPath -Raw
$orchestrator = Get-Content -LiteralPath $orchestratorPath -Raw

# BAT must show the remote release before asking twice, and pass both entries as
# quoted data to the new script.  No call/eval-style command construction is allowed.
if (([regex]::Matches($bat, '(?im)^\s*set\s+/p\s+')).Count -ne 2) {
    throw "Release BAT must prompt for the target version exactly twice."
}
Assert-Contains $bat 'gh release list --repo QingMo-A/QingToolbox --limit 1' 'BAT does not display the latest published Release first.'
Assert-Contains $bat '-json tagName,name' 'BAT does not request the remote Release tag/version.'
if ($bat -match 'gh release view --repo QingMo-A/QingToolbox(?!.*\bv)') {
    throw 'BAT uses gh release view without a tag, which fails when every public Release is a prerelease.'
}
Assert-Contains $bat '-ReadVersionFromEnvironment' 'BAT does not use the fixed environment-data handoff.'
if ($bat -match '%QING_RELEASE_VERSION_(?:FIRST|SECOND)%') {
    throw 'Release BAT interpolates an untrusted version into a command line.'
}
Assert-Contains $bat 'DisableDelayedExpansion' 'BAT must disable delayed expansion while reading untrusted input.'
if (([regex]::Matches($bat, '(?im)^\s*pause\s*$')).Count -lt 4) {
    throw 'Release BAT must pause on validation/query errors and after the hand-off.'
}
if ($bat -match '(?i)Invoke-Expression|cmd\s+/c|call\s+.*%QING_RELEASE') {
    throw 'Release BAT contains unsafe command-string evaluation.'
}

# Input, metadata, and release-note gates are checked before any external command.
foreach ($contract in @(
        'The two target version entries must match exactly',
        'ReadVersionFromEnvironment',
        'QING_RELEASE_VERSION_FIRST',
        'QING_RELEASE_VERSION_SECOND',
        'Test-StrictSemVer',
        'Get-PreviewReleaseMetadata',
        'docs\releases',
        'Target version',
        'Version -cne $ConfirmVersion')) {
    Assert-Contains $orchestrator $contract
}

# PowerShell 5.1 strict mode must still represent a clean diff as an empty
# array. Piping outside @() unwraps zero results to $null and makes .Count fail
# before the release guards can run.
if ($orchestrator -notmatch '(?s)\$diffIssues\s*=\s*@\(\s*@\(.*?diff.*?--check.*?diff.*?--cached.*?--check.*?\)\s*\|\s*Where-Object.*?\s*\)') {
    throw 'Clean diff output is not protected by an outer array expression.'
}

# Source, authentication, and existing tag/Release guards.
foreach ($contract in @(
        'branch", "--show-current',
        'status", "--porcelain=v1',
        'fetch", "--no-tags", "origin", "toolbox',
        'refs/remotes/origin/toolbox',
        'auth", "status',
        'refs/tags/$tag',
        'ls-remote',
        'release", "view", $tag')) {
    Assert-Contains $orchestrator $contract
}

# Candidate validation must happen before tag mutation; publication must carry the
# successful candidate's run id on a tag ref.
Assert-Contains $orchestrator 'publish_release = "false"' 'Candidate dispatch does not disable release publication.'
Assert-Contains $orchestrator 'publish_release = "true"' 'Publication dispatch does not enable release publication.'
Assert-Contains $orchestrator 'candidate_run_id = [string]$candidateRun.databaseId' 'Publication dispatch does not carry candidate_run_id.'
Assert-Contains $orchestrator 'Start-Sleep -Seconds $PollSeconds' 'Normal mode does not poll for the exact newly dispatched run.'
Assert-Contains $orchestrator 'gh run watch' 'Normal mode does not watch each workflow run.'
Assert-Ordered $orchestrator 'Wait-ForSuccessfulWorkflowRun -Run $candidateRun' 'Publish-Tag -Tag $tag' 'The candidate must be proven successful before tag creation.'
Assert-Ordered $orchestrator 'Publish-Tag -Tag $tag' 'publish_release = "true"' 'The tag must be created/pushed before publication dispatch.'
Assert-Contains $orchestrator '--ref $tag' 'Publication dispatch is not pinned to the newly created tag.'

# WhatIf/TestMode is a hard no-side-effect path.  The branch is before the first
# source/auth/dispatch call, so it cannot mutate or wait on git or gh.
Assert-Contains $orchestrator '$dryRun = $WhatIf -or $TestMode' 'WhatIf/TestMode switch handling is missing.'
Assert-Ordered $orchestrator 'if ($dryRun)' '$head = Assert-SourceGuards' 'WhatIf/TestMode must return before git/gh guards.'
$dryRunIndex = $orchestrator.IndexOf('$dryRun = $WhatIf -or $TestMode', [StringComparison]::OrdinalIgnoreCase)
$sourceGuardIndex = $orchestrator.IndexOf('$head = Assert-SourceGuards', [StringComparison]::OrdinalIgnoreCase)
if ($dryRunIndex -lt 0 -or $sourceGuardIndex -le $dryRunIndex) {
    throw 'Unable to locate the guarded normal-mode block.'
}
$dryRunBlock = $orchestrator.Substring($dryRunIndex, $sourceGuardIndex - $dryRunIndex)
if ($dryRunBlock -match 'Start-Sleep|gh\s+run\s+watch|Invoke-Git|Invoke-Gh') {
    throw 'WhatIf/TestMode block contains a git/gh call or wait.'
}

Write-Host 'Publish preview release contracts passed.'
