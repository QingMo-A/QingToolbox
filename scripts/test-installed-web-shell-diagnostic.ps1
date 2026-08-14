[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$payloadSource = Join-Path $repoRoot 'QingToolbox.Shell\bin\Release\net10.0-windows10.0.17763.0'
$diagnostic = Join-Path $PSScriptRoot 'diagnose-installed-web-shell.ps1'
if (-not (Test-Path -LiteralPath $payloadSource -PathType Container)) {
    throw 'Release Shell payload is missing; build QingToolbox.Shell before running this test.'
}
if (-not (Test-Path -LiteralPath $diagnostic -PathType Leaf)) { throw 'Installed Web Shell diagnostic script is missing.' }

$root = Join-Path ([IO.Path]::GetTempPath()) ('QingToolbox-InstalledWebShellDiagnosticTest-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $clean = Join-Path $root 'clean'
    Copy-Item -LiteralPath $payloadSource -Destination $clean -Recurse -Force
    foreach ($nonPayload in @('Modules', 'UserData')) {
        $path = Join-Path $clean $nonPayload
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    foreach ($directory in @(Get-ChildItem -LiteralPath $clean -Directory -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -in @('Modules', 'modules', 'UserData', 'userdata') } |
            Sort-Object FullName -Descending)) {
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force
    }
    & (Join-Path $PSScriptRoot 'write-host-payload-manifest.ps1') -PayloadDirectory $clean 6>$null | Out-Null

    function New-ProductionRoot {
        param([Parameter(Mandatory = $true)][string]$Name, [string[]]$Lines = @())
        $productionRoot = Join-Path $root ('production-' + $Name)
        $logs = Join-Path $productionRoot 'logs'
        New-Item -ItemType Directory -Path $logs -Force | Out-Null
        if ($Lines.Count) {
            Set-Content -LiteralPath (Join-Path $logs 'qingtoolbox-diagnostic.log') -Value $Lines -Encoding UTF8
        }
        return $productionRoot
    }

    function Invoke-Case {
        param(
            [Parameter(Mandatory = $true)][string]$Name,
            [Parameter(Mandatory = $true)][string]$InstallPath,
            [Parameter(Mandatory = $true)][string]$ProductionRoot
        )
        $json = Join-Path $root ($Name + '.json')
        & $diagnostic -InstallRoot $InstallPath -OutputPath $json -ProductionLocalRoot $ProductionRoot 6>$null | Out-Null
        if (-not (Test-Path -LiteralPath $json -PathType Leaf)) { throw "Diagnostic did not write a report for $Name." }
        return Get-Content -LiteralPath $json -Raw -Encoding UTF8 | ConvertFrom-Json
    }

    $cleanReport = Invoke-Case -Name 'clean' -InstallPath $clean -ProductionRoot (New-ProductionRoot 'clean')
    if ($cleanReport.AssetBinding -ne 'PASS' -or $cleanReport.WebUiFileSet -ne 'PASS' -or
        -not [bool]$cleanReport.CleanProbe.navigationSucceeded -or
        -not [bool]$cleanReport.CleanProbe.workspaceActivated) {
        throw 'Clean installed payload did not pass binding and Web Shell Ready probe.'
    }
    Write-Host 'Scenario clean: AssetBinding PASS; CleanProbe Ready.'

    $stale = Join-Path $root 'stale'
    Copy-Item -LiteralPath $clean -Destination $stale -Recurse -Force
    $staleFile = Join-Path $stale 'WebUI\assets\stale-test.js'
    Set-Content -LiteralPath $staleFile -Value 'stale diagnostic fixture' -Encoding ASCII
    $staleReport = Invoke-Case -Name 'stale' -InstallPath $stale -ProductionRoot (New-ProductionRoot 'stale')
    if ($staleReport.DiagnosisCategory -ne 'HostAssetsInvalid' -and $staleReport.WebUiFileSet -ne 'FAIL') {
        throw 'Extra WebUI asset was not classified as invalid.'
    }
    Write-Host 'Scenario extra asset: HostAssetsInvalid/WebUiFileSet FAIL.'

    $corrupt = Join-Path $root 'corrupt'
    Copy-Item -LiteralPath $clean -Destination $corrupt -Recurse -Force
    $currentJs = @(Get-ChildItem -LiteralPath (Join-Path $corrupt 'WebUI\assets') -Filter '*.js' -File)[0]
    $bytes = [IO.File]::ReadAllBytes($currentJs.FullName)
    if ($bytes.Length -lt 1) { throw 'Current JavaScript fixture is empty.' }
    $bytes[0] = $bytes[0] -bxor 1
    [IO.File]::WriteAllBytes($currentJs.FullName, $bytes)
    $corruptReport = Invoke-Case -Name 'corrupt' -InstallPath $corrupt -ProductionRoot (New-ProductionRoot 'corrupt')
    if ($corruptReport.AssetBinding -ne 'FAIL') { throw 'Modified current WebUI asset did not fail AssetBinding.' }
    Write-Host 'Scenario modified current asset: AssetBinding FAIL.'

    $failureReadyLines = @(
        "2026-08-14T12:00:00.0000000+00:00`tInformation`tWebShell`tWeb Shell initialization failed; failure=ReadyTimeout."
        "2026-08-14T12:01:00.0000000+00:00`tInformation`tWebShell`tWeb Shell ready; environment=Production; protocol=4; generation=1."
    )
    $failureReady = Invoke-Case -Name 'failure-ready' -InstallPath $clean -ProductionRoot (New-ProductionRoot 'failure-ready' $failureReadyLines)
    if ($failureReady.LatestProductionFailureCode -ne 'ReadyTimeout' -or
        $failureReady.LatestProductionOutcome -ne 'Ready' -or
        $failureReady.DiagnosisCategory -ne 'Ready') {
        throw 'Failure followed by Ready was not classified as current Ready.'
    }
    Write-Host 'Scenario failure -> ready: history retained; outcome Ready; category Ready.'

    $readyFailureLines = @(
        "2026-08-14T12:00:00.0000000+00:00`tInformation`tWebShell`tWeb Shell ready; environment=Production; protocol=4; generation=1."
        "2026-08-14T12:01:00.0000000+00:00`tInformation`tWebShell`tWeb Shell initialization failed; failure=NavigationTimeout."
    )
    $readyFailure = Invoke-Case -Name 'ready-failure' -InstallPath $clean -ProductionRoot (New-ProductionRoot 'ready-failure' $readyFailureLines)
    if ($readyFailure.LatestProductionFailureCode -ne 'NavigationTimeout' -or
        $readyFailure.LatestProductionOutcome -ne 'Failed' -or
        $readyFailure.DiagnosisCategory -ne 'ProductionOnlyFailure') {
        throw 'Ready followed by failure was not classified as current Failed.'
    }
    Write-Host 'Scenario ready -> failure: outcome Failed; category ProductionOnlyFailure.'

    $unknown = Invoke-Case -Name 'unknown' -InstallPath $clean -ProductionRoot (New-ProductionRoot 'unknown')
    if ($unknown.LatestProductionOutcome -ne 'Unknown' -or $unknown.DiagnosisCategory -ne 'NoRecordedFailure') {
        throw 'Missing WebShell events were not classified as Unknown/NoRecordedFailure.'
    }
    Write-Host 'Scenario no WebShell events: outcome Unknown; category NoRecordedFailure.'

    $source = [IO.File]::ReadAllText($diagnostic)
    [scriptblock]::Create($source) | Out-Null
    foreach ($forbidden in @('WebShellInitializer', 'WebAssetIdentity', 'WebBridge', 'Remove-Item WebUI', 'Remove-Item webview2', 'Clear-Cache')) {
        if ($source -match [regex]::Escape($forbidden)) { throw "Diagnostic contains forbidden production mutation/reference: $forbidden" }
    }
    foreach ($outputGuard in @('OutputPath must not write into the installed payload', '$resolvedOutputPath.StartsWith($protectedPrefix')) {
        if ($source -notmatch [regex]::Escape($outputGuard)) { throw "Diagnostic OutputPath guard is missing: $outputGuard" }
    }
    foreach ($orderingContract in @('ProductionLocalRoot', 'LatestProductionOutcome', 'CurrentOutcome')) {
        if ($source -notmatch [regex]::Escape($orderingContract)) { throw "Diagnostic log-ordering contract is missing: $orderingContract" }
    }
    Write-Host 'Installed Web Shell diagnostic PowerShell contract passed.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
