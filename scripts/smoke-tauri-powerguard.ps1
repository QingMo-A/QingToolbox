[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$resolved = [IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "PowerGuard executable was not found: $resolved" }
$nonce = 'powerguard-smoke-' + [Guid]::NewGuid().ToString('N')
$dataRoot = Join-Path ([IO.Path]::GetTempPath()) ('qing-powerguard-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $resolved; $psi.WorkingDirectory = Split-Path -Parent $resolved
$psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$psi.Environment['QINGTOOLBOX_MODULE_ID'] = 'qing.powerguard'; $psi.Environment['QINGTOOLBOX_MODULE_NONCE'] = $nonce; $psi.Environment['QINGTOOLBOX_MODULE_DATA_DIR'] = $dataRoot
$process = [Diagnostics.Process]::new(); $process.StartInfo = $psi
function Read-Frame([Diagnostics.Process]$Child, [string]$Label) {
    $task = $Child.StandardOutput.ReadLineAsync()
    if (-not $task.Wait(10000)) { throw "Timed out waiting for PowerGuard $Label response." }
    if ($task.IsFaulted -or $null -eq $task.Result) { throw "PowerGuard closed stdout before the $Label response." }
    try { return $task.Result | ConvertFrom-Json } catch { throw "PowerGuard returned invalid JSON for $Label response." }
}
function Send-Frame([Diagnostics.Process]$Child, [hashtable]$Frame, [string]$Label) {
    $Child.StandardInput.WriteLine(($Frame | ConvertTo-Json -Compress -Depth 20)); $Child.StandardInput.Flush(); return Read-Frame $Child $Label
}
try {
    if (-not $process.Start()) { throw 'Unable to start PowerGuard module.' }
    $hello = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.hello.request'; requestId = 'hello-smoke'; payload = @{ moduleId = 'qing.powerguard'; nonce = $nonce } } 'hello'
    if ($hello.messageType -ne 'module.hello.response' -or $hello.payload.moduleId -ne 'qing.powerguard' -or $hello.payload.nonce -ne $nonce) { throw 'PowerGuard hello response did not satisfy the nonce-bound protocol contract.' }
    $state = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'state-smoke'; payload = @{ method = 'getState'; payload = @{} } } 'getState'
    if ($null -eq $state.payload.settings -or $state.payload.guardEnabled -ne $false -or $null -eq $state.payload.events) { throw 'PowerGuard initial state was invalid or unsafe.' }
    $bad = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'bad-settings'; payload = @{ method = 'setSettings'; payload = @{ guardEnabled = $false; startupGraceSeconds = 0; offlineConfirmationSeconds = 1; shutdownCountdownSeconds = 60; recoveryConfirmationSeconds = 5; showRecoveryNotification = $true } } } 'invalid settings'
    if ($null -eq $bad.error -or $bad.error.code -ne 'settings_invalid') { throw 'PowerGuard accepted an unsafe settings range.' }
    $probe = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'probe-smoke'; payload = @{ method = 'probeNow'; payload = @{} } } 'probe'
    if ($null -eq $probe.payload.lastProbe -or $null -eq $probe.payload.lastProbeEpochMillis) { throw 'PowerGuard probe response was invalid.' }
    $test = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'test-smoke'; payload = @{ method = 'testWarning'; payload = @{} } } 'test warning'
    if ($null -eq $test.payload.testRemainingSeconds -or $test.payload.testRemainingSeconds -le 0) { throw 'PowerGuard test countdown did not start.' }
    $cancel = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'cancel-test'; payload = @{ method = 'clearEvents'; payload = @{} } } 'clear events'
    if ($null -eq $cancel.payload.events) { throw 'PowerGuard clear events response was invalid.' }
    $shutdown = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'shutdown-smoke'; payload = @{ method = 'shutdownNow'; payload = @{ confirm = 'yes' } } } 'confirmation'
    if ($null -eq $shutdown.error -or $shutdown.error.code -ne 'confirmation_required') { throw 'PowerGuard accepted an unconfirmed shutdown request.' }
    $response = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.shutdown.request'; requestId = 'final-shutdown'; payload = @{} } 'shutdown'
    if ($response.messageType -ne 'module.shutdown.response') { throw 'PowerGuard shutdown response was invalid.' }
    $process.StandardInput.Close(); if (-not $process.WaitForExit(3000)) { throw 'PowerGuard did not exit after shutdown.' }
    if ($process.ExitCode -ne 0) { throw "PowerGuard exited with code $($process.ExitCode)." }
    Write-Host 'PowerGuard module smoke test passed.'
}
finally {
    if ($process -and -not $process.HasExited) { try { $process.Kill() } catch {}; try { $process.WaitForExit(1000) } catch {} }
    if ($process) { $process.Dispose() }
    if (Test-Path -LiteralPath $dataRoot) { Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
