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

    function Invoke-Case {
        param([Parameter(Mandatory = $true)][string]$Name, [Parameter(Mandatory = $true)][string]$InstallPath)
        $json = Join-Path $root ($Name + '.json')
        & $diagnostic -InstallRoot $InstallPath -OutputPath $json 6>$null | Out-Null
        if (-not (Test-Path -LiteralPath $json -PathType Leaf)) { throw "Diagnostic did not write a report for $Name." }
        return Get-Content -LiteralPath $json -Raw -Encoding UTF8 | ConvertFrom-Json
    }

    $cleanReport = Invoke-Case -Name 'clean' -InstallPath $clean
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
    $staleReport = Invoke-Case -Name 'stale' -InstallPath $stale
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
    $corruptReport = Invoke-Case -Name 'corrupt' -InstallPath $corrupt
    if ($corruptReport.AssetBinding -ne 'FAIL') { throw 'Modified current WebUI asset did not fail AssetBinding.' }
    Write-Host 'Scenario modified current asset: AssetBinding FAIL.'

    $source = [IO.File]::ReadAllText($diagnostic)
    [scriptblock]::Create($source) | Out-Null
    foreach ($forbidden in @('WebShellInitializer', 'WebAssetIdentity', 'WebBridge', 'Remove-Item WebUI', 'Remove-Item webview2', 'Clear-Cache')) {
        if ($source -match [regex]::Escape($forbidden)) { throw "Diagnostic contains forbidden production mutation/reference: $forbidden" }
    }
    foreach ($outputGuard in @('OutputPath must not write into the installed payload', '$resolvedOutputPath.StartsWith($protectedPrefix')) {
        if ($source -notmatch [regex]::Escape($outputGuard)) { throw "Diagnostic OutputPath guard is missing: $outputGuard" }
    }
    Write-Host 'Installed Web Shell diagnostic PowerShell contract passed.'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
