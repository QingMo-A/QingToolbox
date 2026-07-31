[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateRange(1, 600)][int]$TimeoutSeconds = 120,
    [string]$DiagnosticsDirectory,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'local-environment-common.ps1')

$probeId = [Guid]::NewGuid()
$profile = "WebShellProbe-$($probeId.ToString('N').Substring(0, 12))"
$info = Resolve-LocalEnvironmentProfile -Environment Development -Profile $profile
$result = Join-Path $info.ProfileRoot "temp\web-shell-probe-$($probeId.ToString('D')).json"
$webViewUserData = Join-Path $info.ProfileRoot "local\webview2\$profile"
$settingsPath = Join-Path $info.ProfileRoot 'roaming\settings.json'
$logsDirectory = Join-Path $info.ProfileRoot 'local\logs'
$startupDirectory = Join-Path $info.ProfileRoot 'local\Startup'
$startupHealthPath = Join-Path $startupDirectory 'startup-health.json'
$exe = Join-Path $info.RepoRoot "QingToolbox.Shell\bin\$Configuration\net10.0-windows\QingToolbox.Shell.exe"
$process = $null
$startedAt = [DateTimeOffset]::UtcNow
$endedAt = $null
$failure = $null
$failureType = $null
$diagnosticsCopyFailures = [Collections.Generic.List[string]]::new()
$cleanupFailure = $null

function Get-ProcessState {
    if ($null -eq $process) { return [pscustomobject]@{ Alive = $false; ExitCode = $null } }
    try { $process.Refresh() } catch { }
    if ($process.HasExited) { return [pscustomobject]@{ Alive = $false; ExitCode = $process.ExitCode } }
    return [pscustomobject]@{ Alive = $true; ExitCode = $null }
}

function Copy-DiagnosticFile {
    param([Parameter(Mandatory = $true)][string]$Source, [Parameter(Mandatory = $true)][string]$Destination)
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { return }
    try {
        $parent = Split-Path -Parent $Destination
        if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Copy-Item -LiteralPath $Source -Destination $Destination -Force -ErrorAction Stop
    }
    catch { $diagnosticsCopyFailures.Add("$Source -> $Destination ($($_.Exception.GetType().Name))") }
}

function Write-CanaryDiagnostics {
    param([Parameter(Mandatory = $true)][string]$Type)
    if ([string]::IsNullOrWhiteSpace($DiagnosticsDirectory)) { return }
    try { New-Item -ItemType Directory -Path $DiagnosticsDirectory -Force | Out-Null }
    catch { $diagnosticsCopyFailures.Add("DiagnosticsDirectory ($($_.Exception.GetType().Name))"); return }

    Copy-DiagnosticFile -Source $result -Destination (Join-Path $DiagnosticsDirectory 'probe-result.json')
    if (Test-Path -LiteralPath $logsDirectory -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $logsDirectory -File -Filter '*.log' -ErrorAction SilentlyContinue)) {
            Copy-DiagnosticFile -Source $file.FullName -Destination (Join-Path $DiagnosticsDirectory "logs\$($file.Name)")
        }
    }
    if (Test-Path -LiteralPath $startupDirectory -PathType Container) {
        foreach ($file in @(Get-ChildItem -LiteralPath $startupDirectory -File -Filter '*.json' -ErrorAction SilentlyContinue)) {
            Copy-DiagnosticFile -Source $file.FullName -Destination (Join-Path $DiagnosticsDirectory "startup\$($file.Name)")
        }
    }

    try {
        $bounded = [Collections.Generic.List[string]]::new()
        foreach ($root in @($logsDirectory, $startupDirectory)) {
            if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
            foreach ($file in @(Get-ChildItem -LiteralPath $root -File -Recurse -ErrorAction SilentlyContinue)) {
                $relative = $file.FullName.Substring($info.ProfileRoot.Length).TrimStart('\')
                $bounded.Add("$relative`t$($file.Length)")
            }
        }
        if (Test-Path -LiteralPath $result -PathType Leaf) {
            $file = Get-Item -LiteralPath $result
            $bounded.Add("$($file.FullName.Substring($info.ProfileRoot.Length).TrimStart('\'))`t$($file.Length)")
        }
        $bounded | Set-Content -LiteralPath (Join-Path $DiagnosticsDirectory 'bounded-files.txt') -Encoding UTF8
    }
    catch { $diagnosticsCopyFailures.Add("bounded-files.txt ($($_.Exception.GetType().Name))") }

    $state = Get-ProcessState
    $finished = if ($endedAt) { $endedAt } else { [DateTimeOffset]::UtcNow }
    $summary = @(
        "probeId=$probeId"
        'Environment=Development'
        "Profile=$profile"
        "Configuration=$Configuration"
        "TimeoutSeconds=$TimeoutSeconds"
        "StartedAtUtc=$($startedAt.ToString('O'))"
        "EndedAtUtc=$($finished.ToString('O'))"
        "ElapsedSeconds=$([Math]::Round(($finished - $startedAt).TotalSeconds, 3))"
        "ShellExe=$exe"
        "ProcessId=$(if ($process) { $process.Id } else { 'NotStarted' })"
        "ProcessAlive=$($state.Alive)"
        "ExitCode=$(if ($null -ne $state.ExitCode) { $state.ExitCode } else { 'Unavailable' })"
        "ResultPath=$result"
        "ResultExists=$(Test-Path -LiteralPath $result -PathType Leaf)"
        "ProfileRoot=$($info.ProfileRoot)"
        "WebView2UserDataExists=$(Test-Path -LiteralPath $webViewUserData -PathType Container)"
        "SettingsExists=$(Test-Path -LiteralPath $settingsPath -PathType Leaf)"
        "SessionLogsExist=$(Test-Path -LiteralPath $logsDirectory -PathType Container)"
        "StartupHealthExists=$(Test-Path -LiteralPath $startupHealthPath -PathType Leaf)"
        "FailureType=$Type"
        "DiagnosticsCopyFailures=$(if ($diagnosticsCopyFailures.Count) { $diagnosticsCopyFailures -join '; ' } else { 'None' })"
    )
    try { $summary | Set-Content -LiteralPath (Join-Path $DiagnosticsDirectory 'summary.txt') -Encoding UTF8 }
    catch { Write-Warning "Unable to write Web Shell canary summary: $($_.Exception.GetType().Name)" }
}

try {
    try {
        Assert-NoLocalProfileReparsePoints -ProfileInfo $info
        if (-not $NoBuild) {
            & (Join-Path $PSScriptRoot 'build-web-ui.ps1')
            if ($LASTEXITCODE) { throw 'WebUI build failed.' }
            dotnet build (Join-Path $info.RepoRoot 'QingToolbox.Shell\QingToolbox.Shell.csproj') -c $Configuration
            if ($LASTEXITCODE) { throw 'Shell build failed.' }
        }
        $arguments = @('--environment', 'Development', '--profile', $profile, '--repo-root', $info.RepoRoot,
            '--web-shell-probe', $probeId.ToString('D'))
        $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
        $deadline = $startedAt.AddSeconds($TimeoutSeconds)
        $nextStatus = $startedAt.AddSeconds(10)
        while (-not (Test-Path -LiteralPath $result) -and -not $process.HasExited -and
            [DateTimeOffset]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 200
            $process.Refresh()
            $now = [DateTimeOffset]::UtcNow
            if ($now -ge $nextStatus) {
                Write-Host "Development Web Shell probe pending; elapsed=$([Math]::Floor(($now - $startedAt).TotalSeconds))s; processAlive=$(-not $process.HasExited); resultExists=$(Test-Path -LiteralPath $result)."
                $nextStatus = $nextStatus.AddSeconds(10)
            }
        }
        if (-not (Test-Path -LiteralPath $result)) {
            if ($process.HasExited) {
                $failureType = 'ProcessExited'
                throw "Development Web Shell exited before its probe completed (exit $($process.ExitCode))."
            }
            $failureType = 'OuterTimeout'
            throw "Development Web Shell probe timed out after $TimeoutSeconds seconds."
        }
        $probe = Get-Content -LiteralPath $result -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($probe.probeId -ne $probeId -or $probe.protocolVersion -ne 4 -or -not $probe.navigationSucceeded -or
            -not $probe.readyChallengeIssued -or -not $probe.snapshotValidated -or -not $probe.activationPingSucceeded -or
            -not $probe.sessionTokenIssued -or -not $probe.repeatedPingSucceeded -or -not $probe.workspaceActivated -or
            $probe.usedMockTransport) {
            $failureType = 'ProbeResultRejected'
            throw "Development Web Shell probe failed; failureCode=$($probe.failureCode); navigation=$($probe.navigationSucceeded); readyChallenge=$($probe.readyChallengeIssued); snapshotValidated=$($probe.snapshotValidated); activationPing=$($probe.activationPingSucceeded); sessionToken=$($probe.sessionTokenIssued); repeatedPing=$($probe.repeatedPingSucceeded); workspaceActivated=$($probe.workspaceActivated); mock=$($probe.usedMockTransport)."
        }
        Write-Host 'Development Web Shell canary passed.'
        Write-Host "Probe ID: $probeId"
        Write-Host "Asset Build ID: $($probe.assetBuildId)"
        Write-Host 'Mock Transport: false'
    }
    catch {
        $failure = $_
        if (-not $failureType) { $failureType = 'ProbeResultRejected' }
    }
    $endedAt = [DateTimeOffset]::UtcNow
    if ($failure) { Write-CanaryDiagnostics -Type $failureType }
}
finally {
    if ($process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            $previous = $ErrorActionPreference
            $ErrorActionPreference = 'SilentlyContinue'
            & taskkill.exe /PID $process.Id /T /F 2>$null | Out-Null
            $ErrorActionPreference = $previous
            $global:LASTEXITCODE = 0
        }
    }
    if (Test-Path -LiteralPath $info.ProfileRoot) {
        try {
            Assert-NoLocalProfileReparsePoints -ProfileInfo $info -IncludeDescendants
            for ($attempt = 1; $attempt -le 10 -and (Test-Path -LiteralPath $info.ProfileRoot); $attempt++) {
                Start-Sleep -Milliseconds (100 * $attempt)
                Remove-Item -LiteralPath $info.ProfileRoot -Recurse -Force -ErrorAction SilentlyContinue
            }
            if (Test-Path -LiteralPath $info.ProfileRoot) { throw 'Development Web Shell probe profile cleanup did not complete.' }
        }
        catch { $cleanupFailure = $_ }
    }
}

if ($cleanupFailure) {
    if (-not $failure) { $failure = $cleanupFailure; $failureType = 'CleanupFailure' }
    if ($DiagnosticsDirectory) { $endedAt = [DateTimeOffset]::UtcNow; Write-CanaryDiagnostics -Type $failureType }
}
if ($failure) { throw $failure }
