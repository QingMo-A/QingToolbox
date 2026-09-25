[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$resolved = [IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Window Topmost executable was not found: $resolved" }

$nonce = 'windowtopmost-smoke-' + [Guid]::NewGuid().ToString('N')
$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $resolved
$psi.WorkingDirectory = Split-Path -Parent $resolved
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.Environment['QINGTOOLBOX_MODULE_ID'] = 'qing.windowtopmost'
$psi.Environment['QINGTOOLBOX_MODULE_NONCE'] = $nonce

$process = [Diagnostics.Process]::new()
$process.StartInfo = $psi

function Read-Frame([Diagnostics.Process]$Child, [string]$Label) {
    $task = $Child.StandardOutput.ReadLineAsync()
    if (-not $task.Wait(5000)) { throw "Timed out waiting for Window Topmost $Label response." }
    if ($task.IsFaulted -or $null -eq $task.Result) { throw "Window Topmost closed stdout before the $Label response." }
    try { return $task.Result | ConvertFrom-Json } catch { throw "Window Topmost returned invalid JSON for $Label response." }
}

function Send-Frame([Diagnostics.Process]$Child, [hashtable]$Frame, [string]$Label) {
    $Child.StandardInput.WriteLine(($Frame | ConvertTo-Json -Compress -Depth 20))
    $Child.StandardInput.Flush()
    return Read-Frame $Child $Label
}

try {
    if (-not $process.Start()) { throw 'Unable to start Window Topmost module.' }
    $hello = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.hello.request'; requestId = 'hello-smoke'; payload = @{ moduleId = 'qing.windowtopmost'; nonce = $nonce } } 'hello'
    if ($hello.messageType -ne 'module.hello.response' -or $hello.payload.moduleId -ne 'qing.windowtopmost' -or $hello.payload.nonce -ne $nonce) { throw 'Window Topmost hello response did not satisfy the nonce-bound protocol contract.' }
    $state = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'state-smoke'; payload = @{ method = 'getState'; payload = @{} } } 'getState'
    if ($state.messageType -ne 'module.invoke.response' -or $null -eq $state.payload.windows -or $null -eq $state.payload.status) { throw 'Window Topmost getState response was invalid.' }
    $bad = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'bad-window'; payload = @{ method = 'setTopmost'; payload = @{ windowId = 'not-a-window-id' } } } 'invalid window id'
    if ($null -eq $bad.error -or $bad.error.code -ne 'invalid_payload') { throw 'Window Topmost accepted an invalid opaque window id.' }
    $badSelection = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'bad-selection'; payload = @{ method = 'selectWindow'; payload = @{ windowId = 'not-a-window-id' } } } 'invalid selection id'
    if ($null -eq $badSelection.error -or $badSelection.error.code -ne 'invalid_payload') { throw 'Window Topmost accepted an invalid list selection id.' }
    $shutdown = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.shutdown.request'; requestId = 'shutdown-smoke'; payload = @{} } 'shutdown'
    if ($shutdown.messageType -ne 'module.shutdown.response') { throw 'Window Topmost shutdown response was invalid.' }
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(3000)) { throw 'Window Topmost did not exit after shutdown.' }
    if ($process.ExitCode -ne 0) { throw "Window Topmost exited with code $($process.ExitCode)." }
    Write-Host 'Window Topmost module smoke test passed.'
}
finally {
    if ($process -and -not $process.HasExited) { try { $process.Kill() } catch {}; try { $process.WaitForExit(1000) } catch {} }
    if ($process) { $process.Dispose() }
}
