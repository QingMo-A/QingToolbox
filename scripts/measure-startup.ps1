[CmdletBinding()]
param(
    [ValidateSet("Development","ModuleTest")][string]$Environment = "Development",
    [string]$Profile = "StartupMeasure",
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$Apply
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "local-environment-common.ps1")
if (-not $Apply) {
    Write-Host "WhatIf: would launch the isolated $Environment profile and read its Startup Health Journal."
    Write-Host "Pass -Apply to run. No Task Scheduler or HKCU Run registration is performed."
    return
}
$repo = Assert-LocalRepositoryRoot -RepositoryRoot ([IO.Path]::GetFullPath($RepositoryRoot))
$profileInfo = Resolve-LocalEnvironmentProfile -Environment $Environment -Profile $Profile
if (-not $profileInfo.RepoRoot.Equals($repo, [StringComparison]::OrdinalIgnoreCase)) {
    throw "RepositoryRoot must identify the repository that contains this script."
}
if ($Environment -eq "Development") {
    & (Join-Path $PSScriptRoot "start-dev-host.ps1") -Profile $Profile -RepositoryRoot $repo
} else {
    & (Join-Path $PSScriptRoot "start-module-test-host.ps1") -Profile $Profile -RepositoryRoot $repo
}
$journal = Join-Path $profileInfo.ProfileRoot "local\Startup\startup-health.json"
if (-not (Test-Path -LiteralPath $journal)) { Write-Warning "No startup journal was produced yet: $journal"; return }
$records = Get-Content -LiteralPath $journal -Raw -Encoding UTF8 | ConvertFrom-Json
$last = @($records)[-1]
function Get-ElapsedDelta([string]$From, [string]$To) {
    $fromValue = $last.elapsedMilliseconds.$From
    $toValue = $last.elapsedMilliseconds.$To
    if ($null -eq $fromValue -or $null -eq $toValue) { return $null }
    return [long]$toValue - [long]$fromValue
}
$summary = [ordered]@{
    attemptId = $last.attemptId
    source = $last.source
    phaseOutcomes = $last.phaseOutcomes
    processEntryToInstanceReady = $last.elapsedMilliseconds.InstanceReady
    instanceReadyToNotificationAreaReady = Get-ElapsedDelta "InstanceReady" "NotificationAreaReady"
    notificationAreaReadyToPresentationReady = Get-ElapsedDelta "NotificationAreaReady" "PresentationReady"
    presentationReadyToDiscoveryComplete = Get-ElapsedDelta "PresentationReady" "ModuleDiscoveryComplete"
    discoveryCompleteToReady = Get-ElapsedDelta "ModuleDiscoveryComplete" "Ready"
}
$summary | ConvertTo-Json | Write-Host
$summary.GetEnumerator() | Where-Object { $_.Key -notin @("phaseOutcomes") } |
    ForEach-Object { Write-Host ("{0}: {1}" -f $_.Key,$_.Value) }
