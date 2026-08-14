[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-AbsoluteLocalPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathRooted($Path)) {
        throw 'InstallRoot must be an absolute local path.'
    }
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.StartsWith('\\')) { throw 'UNC InstallRoot paths are not supported.' }
    return $full.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

function Protect-UserPath {
    param([AllowNull()][string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $Path }
    $profile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    if (-not [string]::IsNullOrWhiteSpace($profile) -and
        $Path.StartsWith($profile, [StringComparison]::OrdinalIgnoreCase)) {
        return '%USERPROFILE%' + $Path.Substring($profile.Length)
    }
    return $Path
}

function Protect-Message {
    param([AllowNull()][string]$Message)
    if ([string]::IsNullOrWhiteSpace($Message)) { return $Message }
    $line = ($Message -split "`r?`n")[0].Trim()
    return (Protect-UserPath $line).Replace("`t", ' ')
}

function Get-WebUiFileSet {
    param([Parameter(Mandatory = $true)][string]$WebUiRoot)
    $missing = [Collections.Generic.List[string]]::new()
    $extra = [Collections.Generic.List[string]]::new()
    $manifestPath = Join-Path $WebUiRoot 'qing-web-assets.json'
    if (-not (Test-Path -LiteralPath $WebUiRoot -PathType Container)) {
        return [pscustomobject]@{ Passed = $false; Missing = @(); Extra = @(); Failure = 'WebUI directory is missing.' }
    }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        return [pscustomobject]@{ Passed = $false; Missing = @(); Extra = @(); Failure = 'WebUI asset manifest is missing.' }
    }
    try { $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { return [pscustomobject]@{ Passed = $false; Missing = @(); Extra = @(); Failure = 'WebUI asset manifest is invalid.' } }

    $expected = @{}
    foreach ($entry in @($manifest.outputFiles)) {
        $relative = ([string]$entry.path).Replace('/', '\')
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or
            $relative.Split('\') -contains '..') { continue }
        $expected[$relative] = $true
    }
    $prefix = $WebUiRoot.TrimEnd('\') + '\'
    foreach ($file in @(Get-ChildItem -LiteralPath $WebUiRoot -Recurse -File -ErrorAction SilentlyContinue)) {
        if ($file.Name -eq 'qing-web-assets.json') { continue }
        $relative = $file.FullName.Substring($prefix.Length).Replace('\', '/')
        if (-not $expected.ContainsKey($relative.Replace('/', '\'))) { $extra.Add($relative) }
    }
    foreach ($path in $expected.Keys) {
        if (-not (Test-Path -LiteralPath (Join-Path $WebUiRoot $path) -PathType Leaf)) {
            $missing.Add($path.Replace('\', '/'))
        }
    }
    return [pscustomobject]@{
        Passed = ($missing.Count -eq 0 -and $extra.Count -eq 0)
        Missing = @($missing | Sort-Object)
        Extra = @($extra | Sort-Object)
        Failure = if ($missing.Count -or $extra.Count) { 'WebUI file set differs from manifest.' } else { $null }
    }
}

function Get-LogTail {
    param([Parameter(Mandatory = $true)][string]$Path)
    $stream = $null
    $reader = $null
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
            [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        $maxBytes = 262144L
        if ($stream.Length -gt $maxBytes) { $stream.Seek(-$maxBytes, [IO.SeekOrigin]::End) | Out-Null }
        $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false), $true)
        return $reader.ReadToEnd() -split "`r?`n"
    }
    catch { return @() }
    finally {
        if ($reader) { $reader.Dispose() }
        elseif ($stream) { $stream.Dispose() }
    }
}

function Get-ProductionLogSummary {
    param([Parameter(Mandatory = $true)][string]$LogsDirectory)
    $latestFailure = $null
    $latestFailureAt = [DateTimeOffset]::MinValue
    $latestReadyAt = [DateTimeOffset]::MinValue
    $checked = 0
    if (Test-Path -LiteralPath $LogsDirectory -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $LogsDirectory -Filter 'qingtoolbox-*.log' -File -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 5)) {
            $checked++
            foreach ($line in @(Get-LogTail $file.FullName)) {
                $parts = $line -split "`t", 4
                if ($parts.Count -lt 4 -or $parts[2] -ne 'WebShell') { continue }
                $when = [DateTimeOffset]::MinValue
                [DateTimeOffset]::TryParse($parts[0], [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::RoundtripKind, [ref]$when) | Out-Null
                $message = [string]$parts[3]
                if ($message -match '^Web Shell initialization failed; failure=(?<code>[^.\r\n]+(?:\.[^.\r\n]+)*)\.$' -and $when -ge $latestFailureAt) {
                    $latestFailureAt = $when
                    $latestFailure = Protect-Message $Matches['code']
                }
                if ($message -match '^Web Shell ready;' -and $when -ge $latestReadyAt) { $latestReadyAt = $when }
            }
        }
    }
    return [pscustomobject]@{
        FilesChecked = $checked
        LatestFailureCode = if ($latestFailure) { $latestFailure } else { '<none>' }
        HasReady = ($latestReadyAt -gt [DateTimeOffset]::MinValue)
    }
}

function Get-ProductionProfileSummary {
    param([Parameter(Mandatory = $true)][string]$ProfileRoot)
    if (-not (Test-Path -LiteralPath $ProfileRoot -PathType Container)) {
        return [pscustomobject]@{ Present = $false; Accessible = $false; FileCount = 0; FileCountBounded = $true }
    }
    try {
        $files = @(Get-ChildItem -LiteralPath $ProfileRoot -File -Recurse -ErrorAction Stop | Select-Object -First 10001)
        return [pscustomobject]@{ Present = $true; Accessible = $true; FileCount = [Math]::Min($files.Count, 10000); FileCountBounded = ($files.Count -le 10000) }
    }
    catch { return [pscustomobject]@{ Present = $true; Accessible = $false; FileCount = 0; FileCountBounded = $true } }
}

function Copy-InstalledHostPayload {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$DestinationRoot
    )
    $manifestPath = Join-Path $SourceRoot 'host-payload.manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'Host payload manifest is missing; clean probe is unavailable.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($entry in @($manifest.entries)) {
        $relative = ([string]$entry.relativePath).Replace('/', '\')
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or
            $relative.Split('\') -contains '..' -or $relative.Contains(':')) {
            throw 'Host payload manifest contains an unsafe path; clean probe was refused.'
        }
        $source = Join-Path $SourceRoot $relative
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw 'Host payload manifest references a missing file; clean probe was refused.'
        }
        $sourceItem = Get-Item -LiteralPath $source -Force
        if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Installed payload contains a reparse point; diagnostic copy was refused.'
        }
        $destination = Join-Path $DestinationRoot $relative
        $parent = Split-Path -Parent $destination
        if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Copy-Item -LiteralPath $source -Destination $destination -Force -ErrorAction Stop
    }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $DestinationRoot 'host-payload.manifest.json') -Force -ErrorAction Stop
}

function Stop-ProbeProcess {
    param([AllowNull()][Diagnostics.Process]$Process)
    if ($null -eq $Process) { return }
    try { $Process.Refresh() } catch { return }
    if (-not $Process.HasExited) {
        try { $Process.Kill($true) }
        catch {
            $old = $ErrorActionPreference; $ErrorActionPreference = 'SilentlyContinue'
            & taskkill.exe /PID $Process.Id /T /F 2>$null | Out-Null
            $global:LASTEXITCODE = 0
            $ErrorActionPreference = $old
        }
    }
    try { $Process.WaitForExit(5000) | Out-Null } catch { }
}

function Invoke-CleanProbe {
    param([Parameter(Mandatory = $true)][string]$PayloadRoot)
    $id = [Guid]::NewGuid()
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('QingToolbox-WebShellDiagnostic-' + $id.ToString('N'))
    $probeProcess = $null
    $result = $null
    $failure = $null
    try {
        New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
        $probePayload = Join-Path $temporaryRoot 'payload'
        New-Item -ItemType Directory -Path $probePayload -Force | Out-Null
        Copy-InstalledHostPayload -SourceRoot $PayloadRoot -DestinationRoot $probePayload
        $sourceRoot = Join-Path $temporaryRoot 'source'
        New-Item -ItemType Directory -Path (Join-Path $sourceRoot 'QingToolbox.Shell') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $sourceRoot 'scripts') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $sourceRoot 'QingToolbox.Shell\QingToolbox.Shell.csproj') -Value '<Project />' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $sourceRoot 'scripts\start-dev-host.ps1') -Value '# diagnostic marker' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $sourceRoot 'Directory.Build.props') -Value '<Project />' -Encoding UTF8
        $profile = 'InstalledWebDiag-' + $id.ToString('N').Substring(0, 12)
        $probeResult = Join-Path $sourceRoot ('.qingtoolbox\development\' + $profile + '\temp\web-shell-probe-' + $id.ToString('D') + '.json')
        $exe = Join-Path $probePayload 'QingToolbox.Shell.exe'
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Installed payload copy is missing QingToolbox.Shell.exe.' }
        $arguments = @('--environment', 'Development', '--profile', $profile, '--repo-root', $sourceRoot,
            '--web-shell-probe', $id.ToString('D'))
        $probeProcess = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $probePayload -PassThru
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
        while (-not (Test-Path -LiteralPath $probeResult -PathType Leaf) -and [DateTimeOffset]::UtcNow -lt $deadline) {
            try { $probeProcess.Refresh() } catch { }
            if ($probeProcess.HasExited) { break }
            Start-Sleep -Milliseconds 250
        }
        if (-not (Test-Path -LiteralPath $probeResult -PathType Leaf)) {
            if ($probeProcess.HasExited) { $failure = 'ProbeProcessExited' } else { $failure = 'ProbeTimeout' }
        }
        else {
            $result = Get-Content -LiteralPath $probeResult -Raw -Encoding UTF8 | ConvertFrom-Json
        }
    }
    catch { $failure = Protect-Message $_.Exception.Message }
    finally {
        Stop-ProbeProcess $probeProcess
        if (Test-Path -LiteralPath $temporaryRoot) {
            for ($attempt = 1; $attempt -le 8 -and (Test-Path -LiteralPath $temporaryRoot); $attempt++) {
                Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $temporaryRoot) { Start-Sleep -Milliseconds (100 * $attempt) }
            }
        }
    }
    if ($null -eq $result) {
        return [pscustomobject]@{
            Ready = $false
            FailureCode = if ($failure) { $failure } else { 'ProbeNoResult' }
            AssetBuildId = '<none>'
            NavigationSucceeded = $false
            ReadyChallengeIssued = $false
            SnapshotValidated = $false
            ActivationPingSucceeded = $false
            SessionTokenIssued = $false
            RepeatedPingSucceeded = $false
            WorkspaceActivated = $false
        }
    }
    $flags = @('navigationSucceeded', 'readyChallengeIssued', 'snapshotValidated', 'activationPingSucceeded',
        'sessionTokenIssued', 'repeatedPingSucceeded', 'workspaceActivated')
    $ready = $true
    foreach ($flag in $flags) { if (-not [bool]$result.$flag) { $ready = $false } }
    if ($result.failureCode) { $ready = $false }
    return [pscustomobject]@{
        Ready = $ready
        FailureCode = if ($result.failureCode) { Protect-Message ([string]$result.failureCode) } else { '<none>' }
        AssetBuildId = if ($result.assetBuildId) { [string]$result.assetBuildId } else { '<none>' }
        NavigationSucceeded = [bool]$result.navigationSucceeded
        ReadyChallengeIssued = [bool]$result.readyChallengeIssued
        SnapshotValidated = [bool]$result.snapshotValidated
        ActivationPingSucceeded = [bool]$result.activationPingSucceeded
        SessionTokenIssued = [bool]$result.sessionTokenIssued
        RepeatedPingSucceeded = [bool]$result.repeatedPingSucceeded
        WorkspaceActivated = [bool]$result.workspaceActivated
    }
}

$root = Resolve-AbsoluteLocalPath $InstallRoot
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'InstallRoot directory does not exist.' }
$shellExe = Join-Path $root 'QingToolbox.Shell.exe'
if (-not (Test-Path -LiteralPath $shellExe -PathType Leaf)) { throw 'InstallRoot must contain QingToolbox.Shell.exe.' }
$webUiRoot = Join-Path $root 'WebUI'
$hostPayloadPath = Join-Path $root 'host-payload.manifest.json'
$localRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'QingToolbox'
$resolvedOutputPath = $null
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
    foreach ($protectedRoot in @($root, $localRoot)) {
        $protectedPrefix = $protectedRoot.TrimEnd('\') + '\'
        if ($resolvedOutputPath.Equals($protectedRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $resolvedOutputPath.StartsWith($protectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'OutputPath must not write into the installed payload or QingToolbox production data.'
        }
    }
}
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($shellExe)
$assetBindingPassed = $false
$assetBindingFailure = $null
try {
    $verifyScript = Join-Path $PSScriptRoot 'verify-host-web-asset-binding.ps1'
    $verifyOutput = & $verifyScript -PayloadRoot $root 2>&1 6>$null | Out-String
    $assetBindingPassed = $true
}
catch { $assetBindingFailure = Protect-Message $_.Exception.Message }
$fileSet = Get-WebUiFileSet $webUiRoot
$productionLogs = Get-ProductionLogSummary (Join-Path $localRoot 'logs')
$productionProfile = Get-ProductionProfileSummary (Join-Path $localRoot 'webview2\Default')
$cleanProbe = Invoke-CleanProbe $root
$hostAssetsValid = $assetBindingPassed -and $fileSet.Passed -and (Test-Path -LiteralPath $hostPayloadPath -PathType Leaf)
if (-not $hostAssetsValid) { $category = 'HostAssetsInvalid' }
elseif ($cleanProbe.Ready -and $productionLogs.LatestFailureCode -ne '<none>') { $category = 'ProductionOnlyFailure' }
elseif (-not $cleanProbe.Ready) { $category = 'CleanProbeFailure' }
elseif ($productionLogs.HasReady) { $category = 'Ready' }
else { $category = 'NoRecordedFailure' }

$report = [ordered]@{
    InstallRoot = Protect-UserPath $root
    ProductVersion = [string]$versionInfo.ProductVersion
    FileVersion = [string]$versionInfo.FileVersion
    HostPayload = if (Test-Path -LiteralPath $hostPayloadPath -PathType Leaf) { 'PASS' } else { 'MISSING' }
    WebUI = if (Test-Path -LiteralPath $webUiRoot -PathType Container) { 'Present' } else { 'Missing' }
    AssetBinding = if ($assetBindingPassed) { 'PASS' } else { 'FAIL' }
    AssetBindingFailure = if ($assetBindingFailure) { $assetBindingFailure } else { '<none>' }
    WebUiFileSet = if ($fileSet.Passed) { 'PASS' } else { 'FAIL' }
    MissingWebAssets = @($fileSet.Missing)
    ExtraWebAssets = @($fileSet.Extra)
    ProductionWebViewProfile = if ($productionProfile.Present) { 'Present' } else { 'Missing' }
    ProductionWebViewProfileAccessible = $productionProfile.Accessible
    ProductionWebViewProfileFileCount = $productionProfile.FileCount
    ProductionWebViewProfileFileCountBounded = $productionProfile.FileCountBounded
    ProductionLogFilesChecked = $productionLogs.FilesChecked
    LatestProductionFailureCode = $productionLogs.LatestFailureCode
    LatestProductionReady = $productionLogs.HasReady
    CleanProbe = [ordered]@{
        navigationSucceeded = $cleanProbe.NavigationSucceeded
        readyChallengeIssued = $cleanProbe.ReadyChallengeIssued
        snapshotValidated = $cleanProbe.SnapshotValidated
        activationPingSucceeded = $cleanProbe.ActivationPingSucceeded
        sessionTokenIssued = $cleanProbe.SessionTokenIssued
        repeatedPingSucceeded = $cleanProbe.RepeatedPingSucceeded
        workspaceActivated = $cleanProbe.WorkspaceActivated
        failureCode = $cleanProbe.FailureCode
        assetBuildId = $cleanProbe.AssetBuildId
    }
    DiagnosisCategory = $category
}

Write-Host 'QingToolbox Installed Web Shell Diagnostic'
Write-Host '------------------------------------------'
Write-Host "InstallRoot: $($report.InstallRoot)"
Write-Host "HostVersion: $($report.ProductVersion)"
Write-Host "FileVersion: $($report.FileVersion)"
Write-Host "HostPayload: $($report.HostPayload)"
Write-Host "AssetBinding: $($report.AssetBinding)"
if ($report.AssetBindingFailure -ne '<none>') { Write-Host "Failure: $($report.AssetBindingFailure)" }
Write-Host "WebUiFileSet: $($report.WebUiFileSet)"
Write-Host ('MissingWebAssets: ' + $(if ($report.MissingWebAssets.Count) { $report.MissingWebAssets -join ', ' } else { '<none>' }))
Write-Host ('ExtraWebAssets: ' + $(if ($report.ExtraWebAssets.Count) { $report.ExtraWebAssets -join ', ' } else { '<none>' }))
Write-Host "ProductionWebViewProfile: $($report.ProductionWebViewProfile)"
Write-Host "ProductionWebViewProfileAccessible: $($report.ProductionWebViewProfileAccessible)"
Write-Host "ProductionLogFilesChecked: $($report.ProductionLogFilesChecked)"
Write-Host "LatestProductionFailureCode: $($report.LatestProductionFailureCode)"
Write-Host "LatestProductionReady: $($report.LatestProductionReady)"
Write-Host 'CleanProbe:'
foreach ($property in $report.CleanProbe.Keys) { Write-Host "  $property`: $($report.CleanProbe[$property])" }
Write-Host "DiagnosisCategory: $($report.DiagnosisCategory)"

if ($resolvedOutputPath) {
    $parent = Split-Path -Parent $resolvedOutputPath
    if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resolvedOutputPath -Encoding UTF8
}
