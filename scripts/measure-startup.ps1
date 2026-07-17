[CmdletBinding()]
param(
    [ValidateSet("Development","ModuleTest")][string]$Environment = "Development",
    [string]$Profile = "StartupMeasure",
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$Apply
)
$ErrorActionPreference = "Stop"
if (-not $Apply) {
    Write-Host "WhatIf: would launch the isolated $Environment profile and read its Startup Health Journal."
    Write-Host "Pass -Apply to run. No Task Scheduler or HKCU Run registration is performed."
    return
}
$repo = [IO.Path]::GetFullPath($RepositoryRoot)
if ($Environment -eq "Development") {
    & (Join-Path $PSScriptRoot "start-dev-host.ps1") -Profile $Profile -RepositoryRoot $repo
} else {
    & (Join-Path $PSScriptRoot "start-module-test-host.ps1") -Profile $Profile -RepositoryRoot $repo
}
$journal = Join-Path $repo ".qingtoolbox\$($Environment.ToLowerInvariant())\$Profile\local\Startup\startup-health.json"
if (-not (Test-Path -LiteralPath $journal)) { Write-Warning "No startup journal was produced yet: $journal"; return }
$records = Get-Content -LiteralPath $journal -Raw -Encoding UTF8 | ConvertFrom-Json
$last = @($records)[-1]
$summary = [ordered]@{
    processEntryToInstanceReady = $last.elapsedMilliseconds.InstanceReady
    instanceReadyToNotificationAreaReady = $last.elapsedMilliseconds.NotificationAreaReady - $last.elapsedMilliseconds.InstanceReady
    notificationAreaReadyToPresentationReady = $last.elapsedMilliseconds.PresentationReady - $last.elapsedMilliseconds.NotificationAreaReady
    presentationReadyToDiscoveryComplete = $last.elapsedMilliseconds.ModuleDiscoveryComplete - $last.elapsedMilliseconds.PresentationReady
    discoveryCompleteToReady = $last.elapsedMilliseconds.Ready - $last.elapsedMilliseconds.ModuleDiscoveryComplete
}
$summary | ConvertTo-Json | Write-Host
$summary.GetEnumerator() | ForEach-Object { Write-Host ("{0}: {1} ms" -f $_.Key,$_.Value) }
