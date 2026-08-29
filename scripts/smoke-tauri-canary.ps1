[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $resolvedExecutable
$psi.WorkingDirectory = Split-Path -Parent $resolvedExecutable
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $psi

function Read-Response([System.Diagnostics.Process]$Child, [string]$Label) {
    $task = $Child.StandardOutput.ReadLineAsync()
    if (-not $task.Wait(3000)) {
        throw "Timed out waiting for canary $Label response."
    }
    if ($task.IsFaulted -or $null -eq $task.Result) {
        throw "Canary closed stdout before the $Label response."
    }
    try {
        return $task.Result | ConvertFrom-Json
    } catch {
        throw "Canary returned invalid JSON for $Label response: $($task.Result)"
    }
}

try {
    if (-not $process.Start()) {
        throw 'Unable to start the Tauri module canary.'
    }
    $nonce = "smoke-$([Guid]::NewGuid().ToString('N'))"
    $hello = @{
        protocolVersion = 1
        messageType = 'module.hello.request'
        requestId = 'hello-smoke'
        payload = @{ moduleId = 'qing.canary'; nonce = $nonce }
    } | ConvertTo-Json -Compress -Depth 5
    $process.StandardInput.WriteLine($hello)
    $process.StandardInput.Flush()

    $helloResponse = Read-Response $process 'hello'
    if ($helloResponse.protocolVersion -ne 1 -or
        $helloResponse.messageType -ne 'module.hello.response' -or
        $helloResponse.requestId -ne 'hello-smoke' -or
        $helloResponse.payload.moduleId -ne 'qing.canary' -or
        $helloResponse.payload.nonce -ne $nonce) {
        throw 'Canary hello response did not satisfy the nonce-bound protocol contract.'
    }

    $invoke = @{
        protocolVersion = 1
        messageType = 'module.invoke.request'
        requestId = 'invoke-smoke'
        payload = @{ method = 'ping'; payload = @{ source = 'smoke' } }
    } | ConvertTo-Json -Compress -Depth 8
    $process.StandardInput.WriteLine($invoke)
    $process.StandardInput.Flush()

    $invokeResponse = Read-Response $process 'invoke'
    if ($invokeResponse.protocolVersion -ne 1 -or
        $invokeResponse.messageType -ne 'module.invoke.response' -or
        $invokeResponse.requestId -ne 'invoke-smoke' -or
        $invokeResponse.payload.pong -ne $true -or
        $invokeResponse.payload.echo.source -ne 'smoke') {
        throw 'Canary invoke response did not satisfy the request correlation contract.'
    }

    $shutdown = @{
        protocolVersion = 1
        messageType = 'module.shutdown.request'
        requestId = 'shutdown-smoke'
        payload = @{}
    } | ConvertTo-Json -Compress -Depth 5
    $process.StandardInput.WriteLine($shutdown)
    $process.StandardInput.Flush()

    $shutdownResponse = Read-Response $process 'shutdown'
    if ($shutdownResponse.protocolVersion -ne 1 -or
        $shutdownResponse.messageType -ne 'module.shutdown.response' -or
        $shutdownResponse.requestId -ne 'shutdown-smoke') {
        throw 'Canary shutdown response did not satisfy the protocol contract.'
    }
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(3000)) {
        throw 'Canary did not exit after shutdown.'
    }
    if ($process.ExitCode -ne 0) {
        throw "Canary exited with code $($process.ExitCode)."
    }
    Write-Host 'Tauri module canary smoke test passed.'
} finally {
    if ($process -and -not $process.HasExited) {
        try { $process.Kill() } catch { }
        try { $process.WaitForExit(1000) } catch { }
    }
    if ($process) { $process.Dispose() }
}
