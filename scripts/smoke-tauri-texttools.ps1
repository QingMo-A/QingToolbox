[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$resolved = [IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Text Tools executable was not found: $resolved" }
$moduleRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\QingToolbox.Tauri\native-texttools'))
$nonce = 'texttools-smoke-' + [Guid]::NewGuid().ToString('N')
$dataRoot = Join-Path ([IO.Path]::GetTempPath()) ('qing-texttools-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
$psi = [Diagnostics.ProcessStartInfo]::new(); $psi.FileName = $resolved; $psi.WorkingDirectory = Split-Path -Parent $resolved; $psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$psi.Environment['QINGTOOLBOX_MODULE_ID'] = 'qing.texttools'; $psi.Environment['QINGTOOLBOX_MODULE_NONCE'] = $nonce; $psi.Environment['QINGTOOLBOX_MODULE_DATA_DIR'] = $dataRoot
$process = [Diagnostics.Process]::new(); $process.StartInfo = $psi
function Read-Frame([Diagnostics.Process]$Child, [string]$Label) { $task = $Child.StandardOutput.ReadLineAsync(); if (-not $task.Wait(5000)) { throw "Timed out waiting for Text Tools $Label response." }; if ($task.IsFaulted -or $null -eq $task.Result) { throw "Text Tools closed stdout before $Label." }; try { return $task.Result | ConvertFrom-Json } catch { throw "Text Tools returned invalid JSON for $Label." } }
function Send-Frame([Diagnostics.Process]$Child, [hashtable]$Frame, [string]$Label) { $Child.StandardInput.WriteLine(($Frame | ConvertTo-Json -Compress -Depth 20)); $Child.StandardInput.Flush(); Read-Frame $Child $Label }
try {
    if (-not $process.Start()) { throw 'Unable to start Text Tools module.' }
    $hello = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.hello.request'; requestId = 'hello-smoke'; payload = @{ moduleId = 'qing.texttools'; nonce = $nonce } } 'hello'
    if ($hello.messageType -ne 'module.hello.response' -or $hello.payload.moduleId -ne 'qing.texttools') { throw 'Text Tools hello response was invalid.' }
    $set = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'set-smoke'; payload = @{ method = 'setInput'; payload = @{ text = '{"b":2,"a":1}' } } } 'setInput'
    if ($set.payload.input -ne '{"b":2,"a":1}') { throw 'Text Tools did not retain input text.' }
    $formatted = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'format-smoke'; payload = @{ method = 'formatJson'; payload = @{} } } 'formatJson'
    if ($formatted.payload.status -ne 'done' -or $formatted.payload.output -notmatch '\n') { throw 'Text Tools JSON formatting failed.' }
    $rejected = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'payload-smoke'; payload = @{ method = 'setInput'; payload = @{} } } 'payload validation'
    if ($rejected.error.code -ne 'invalid_payload') { throw 'Text Tools accepted a malformed setInput payload.' }
    $shutdown = Send-Frame $process @{ protocolVersion = 1; messageType = 'module.shutdown.request'; requestId = 'shutdown-smoke'; payload = @{} } 'shutdown'
    if ($shutdown.messageType -ne 'module.shutdown.response') { throw 'Text Tools shutdown response was invalid.' }
    $process.StandardInput.Close(); if (-not $process.WaitForExit(3000)) { throw 'Text Tools did not exit after shutdown.' }; if ($process.ExitCode -ne 0) { throw "Text Tools exited with code $($process.ExitCode)." }
    Write-Host 'Text Tools module smoke test passed.'
} finally { if ($process -and -not $process.HasExited) { try { $process.Kill() } catch {}; try { $process.WaitForExit(1000) } catch {} }; if ($process) { $process.Dispose() }; if (Test-Path -LiteralPath $dataRoot) { Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue } }
