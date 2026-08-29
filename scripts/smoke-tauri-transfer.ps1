[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$resolved = [IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
    throw "QingTransfer executable was not found: $resolved"
}

$moduleRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\QingToolbox.Tauri\native-transfer'))
$nonce = 'transfer-smoke-' + [Guid]::NewGuid().ToString('N')
$dataRoot = Join-Path ([IO.Path]::GetTempPath()) ('qing-transfer-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null

$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $resolved
$psi.WorkingDirectory = Split-Path -Parent $resolved
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.Environment['QINGTOOLBOX_MODULE_ID'] = 'qing.qingtransfer'
$psi.Environment['QINGTOOLBOX_MODULE_NONCE'] = $nonce
$psi.Environment['QINGTOOLBOX_MODULE_DIRECTORY'] = $moduleRoot
$psi.Environment['QINGTOOLBOX_MODULE_DATA_DIR'] = $dataRoot
$psi.Environment['QINGTOOLBOX_TRANSFER_NAME'] = 'QingTransfer Smoke'

$process = [Diagnostics.Process]::new()
$process.StartInfo = $psi

function Read-Frame([Diagnostics.Process]$Child, [string]$Label) {
    $task = $Child.StandardOutput.ReadLineAsync()
    if (-not $task.Wait(5000)) { throw "Timed out waiting for QingTransfer $Label response." }
    if ($task.IsFaulted -or $null -eq $task.Result) { throw "QingTransfer closed stdout before the $Label response." }
    try { return $task.Result | ConvertFrom-Json } catch { throw "QingTransfer returned invalid JSON for $Label response." }
}

function Send-Frame([Diagnostics.Process]$Child, [hashtable]$Frame, [string]$Label) {
    $Child.StandardInput.WriteLine(($Frame | ConvertTo-Json -Compress -Depth 20))
    $Child.StandardInput.Flush()
    return Read-Frame $Child $Label
}

try {
    if (-not $process.Start()) { throw 'Unable to start QingTransfer module.' }
    $hello = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.hello.request'; requestId = 'hello-smoke'; payload = @{ moduleId = 'qing.qingtransfer'; nonce = $nonce } } 'hello'
    if ($hello.messageType -ne 'module.hello.response' -or $hello.payload.moduleId -ne 'qing.qingtransfer' -or $hello.payload.nonce -ne $nonce) {
        throw 'QingTransfer hello response did not satisfy the nonce-bound protocol contract.'
    }

    $state = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'state-smoke'; payload = @{ method = 'getState'; payload = @{} } } 'getState'
    if ($state.messageType -ne 'module.invoke.response' -or $null -eq $state.payload.discovery -or $null -eq $state.payload.session -or $null -eq $state.payload.receive) {
        throw 'QingTransfer getState response was invalid.'
    }
    if ($null -eq $state.payload.discovery.peers) { throw 'QingTransfer discovery snapshot omitted peers.' }

    # Paths submitted by Vue are still validated inside the module. A relative
    # path must never reach the network worker or the filesystem.
    $rejected = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'security-smoke'; payload = @{ method = 'sendFile'; payload = @{ peerId = 'peer'; path = 'relative.txt' } } } 'path validation'
    if ($rejected.messageType -ne 'module.invoke.response' -or $null -eq $rejected.error -or $rejected.error.code -ne 'path_invalid') {
        throw 'QingTransfer accepted an unsafe relative send path.'
    }

    $shutdown = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.shutdown.request'; requestId = 'shutdown-smoke'; payload = @{} } 'shutdown'
    if ($shutdown.messageType -ne 'module.shutdown.response') { throw 'QingTransfer shutdown response was invalid.' }
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(3000)) { throw 'QingTransfer did not exit after shutdown.' }
    if ($process.ExitCode -ne 0) { throw "QingTransfer exited with code $($process.ExitCode)." }
    Write-Host 'QingTransfer module smoke test passed.'
}
finally {
    if ($process -and -not $process.HasExited) {
        try { $process.Kill() } catch { }
        try { $process.WaitForExit(1000) } catch { }
    }
    if ($process) { $process.Dispose() }
    if (Test-Path -LiteralPath $dataRoot) { Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
