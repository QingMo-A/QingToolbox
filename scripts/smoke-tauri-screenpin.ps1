[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$ExecutablePath)
Set-StrictMode -Version Latest; $ErrorActionPreference = 'Stop'
$resolved = [IO.Path]::GetFullPath($ExecutablePath); if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Screen Pin executable was not found: $resolved" }
$nonce = 'screenpin-smoke-' + [Guid]::NewGuid().ToString('N'); $psi = [Diagnostics.ProcessStartInfo]::new(); $psi.FileName = $resolved; $psi.WorkingDirectory = Split-Path -Parent $resolved; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.Environment['QINGTOOLBOX_MODULE_ID'] = 'qing.screenpin'; $psi.Environment['QINGTOOLBOX_MODULE_NONCE'] = $nonce
$process = [Diagnostics.Process]::new(); $process.StartInfo = $psi
function Read-Frame([Diagnostics.Process]$Child, [string]$Label) { $task = $Child.StandardOutput.ReadLineAsync(); if (-not $task.Wait(10000)) { throw "Timed out waiting for Screen Pin $Label response." }; if ($task.IsFaulted -or $null -eq $task.Result) { throw "Screen Pin closed stdout before the $Label response." }; try { return $task.Result | ConvertFrom-Json } catch { throw "Screen Pin returned invalid JSON for $Label response." } }
function Send-Frame([Diagnostics.Process]$Child, [hashtable]$Frame, [string]$Label) { $Child.StandardInput.WriteLine(($Frame | ConvertTo-Json -Compress -Depth 20)); $Child.StandardInput.Flush(); return Read-Frame $Child $Label }
try {
    if (-not $process.Start()) { throw 'Unable to start Screen Pin module.' }
    $hello = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.hello.request'; requestId = 'hello-smoke'; payload = @{ moduleId = 'qing.screenpin'; nonce = $nonce } } 'hello'; if ($hello.messageType -ne 'module.hello.response' -or $hello.payload.moduleId -ne 'qing.screenpin' -or $hello.payload.nonce -ne $nonce) { throw 'Screen Pin hello response did not satisfy the nonce-bound protocol contract.' }
    $bounds = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'bounds-smoke'; payload = @{ method = 'getDisplayBounds'; payload = @{} } } 'display bounds'; if ($null -eq $bounds.payload.displayBounds) { throw 'Screen Pin display bounds response was invalid.' }
    if ($bounds.payload.displayBounds.width -gt 0 -and $bounds.payload.displayBounds.height -gt 0) {
        $capture = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'capture-smoke'; payload = @{ method = 'captureRegion'; payload = @{ x = [int]$bounds.payload.displayBounds.x; y = [int]$bounds.payload.displayBounds.y; width = 1; height = 1 } } } 'one-pixel capture'
        if ($null -eq $capture.payload.pins -or $capture.payload.pins.Count -ne 1 -or $capture.payload.pins[0].dataUrl -notlike 'data:image/png;base64,*') { throw 'Screen Pin did not return a bounded PNG capture.' }
        $removed = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'remove-smoke'; payload = @{ method = 'removePin'; payload = @{ pinId = [string]$capture.payload.pins[0].id } } } 'remove pin'
        if ($removed.payload.pins.Count -ne 0) { throw 'Screen Pin did not remove the opaque pin id.' }
    }
    $bad = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'bad-region'; payload = @{ method = 'captureRegion'; payload = @{ x = 0; y = 0; width = 0; height = 20 } } } 'invalid region'; if ($null -eq $bad.error -or $bad.error.code -ne 'invalid_region') { throw 'Screen Pin accepted an invalid region.' }
    $shutdown = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.shutdown.request'; requestId = 'shutdown-smoke'; payload = @{} } 'shutdown'; if ($shutdown.messageType -ne 'module.shutdown.response') { throw 'Screen Pin shutdown response was invalid.' }; $process.StandardInput.Close(); if (-not $process.WaitForExit(3000)) { throw 'Screen Pin did not exit after shutdown.' }; if ($process.ExitCode -ne 0) { throw "Screen Pin exited with code $($process.ExitCode)." }
    Write-Host 'Screen Pin module smoke test passed.'
} finally { if ($process -and -not $process.HasExited) { try { $process.Kill() } catch {}; try { $process.WaitForExit(1000) } catch {} }; if ($process) { $process.Dispose() } }
