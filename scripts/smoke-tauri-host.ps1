[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$workingDirectory = Split-Path -Parent $resolvedExecutable
$first = $null
$second = $null

function New-HostStartInfo([string]$Path, [string]$WorkingDirectory) {
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Path
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    return $info
}

try {
    $first = [System.Diagnostics.Process]::new()
    $first.StartInfo = New-HostStartInfo $resolvedExecutable $workingDirectory
    if (-not $first.Start()) {
        throw 'Unable to start the Tauri host.'
    }
    # WebView2-backed windows are not guaranteed to report input idle, so use
    # the process-alive check below as the startup assertion.
    Start-Sleep -Seconds 3
    if ($first.HasExited) {
        throw "Tauri host exited during startup with code $($first.ExitCode)."
    }

    $second = [System.Diagnostics.Process]::new()
    $second.StartInfo = New-HostStartInfo $resolvedExecutable $workingDirectory
    if (-not $second.Start()) {
        throw 'Unable to start the second Tauri host instance.'
    }
    if (-not $second.WaitForExit(5000)) {
        throw 'The second Tauri host instance did not hand off to the first instance.'
    }
    if ($second.ExitCode -ne 0) {
        throw "The second Tauri host instance exited with code $($second.ExitCode)."
    }
    if ($first.HasExited) {
        throw 'The first Tauri host instance exited after the second launch.'
    }
    Write-Host 'Tauri host startup and single-instance smoke test passed.'
} finally {
    if ($second) {
        if (-not $second.HasExited) {
            try { [void]$second.Kill() } catch { }
            try { [void]$second.WaitForExit(1000) } catch { }
        }
        $second.Dispose()
    }
    if ($first) {
        if (-not $first.HasExited) {
            try { [void]$first.Kill() } catch { }
            try { [void]$first.WaitForExit(2000) } catch { }
        }
        $first.Dispose()
    }
}
