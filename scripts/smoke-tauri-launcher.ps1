[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
$resolved = [IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $resolved)) { throw "Launcher executable was not found: $resolved" }

$nonce = 'launcher-smoke-' + [Guid]::NewGuid().ToString('N')
$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $resolved
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.Environment['QINGTOOLBOX_MODULE_ID'] = 'qing.launcher'
$psi.Environment['QINGTOOLBOX_MODULE_NONCE'] = $nonce
$dataRoot = Join-Path ([IO.Path]::GetTempPath()) ('qing-launcher-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
$psi.Environment['QINGTOOLBOX_MODULE_DATA_DIR'] = $dataRoot
$process = [Diagnostics.Process]::new()
$process.StartInfo = $psi
try {
    if (-not $process.Start()) { throw 'Failed to start Qing Launcher module.' }
    $writer = $process.StandardInput
    $reader = $process.StandardOutput
    $writer.WriteLine((ConvertTo-Json @{ protocolVersion = 1; messageType = 'module.hello.request'; requestId = 'hello-1'; payload = @{ moduleId = 'qing.launcher'; nonce = $nonce } } -Compress))
    $writer.Flush()
    $hello = $reader.ReadLine() | ConvertFrom-Json
    if ($hello.messageType -ne 'module.hello.response' -or $hello.payload.moduleId -ne 'qing.launcher' -or $hello.payload.nonce -ne $nonce) { throw 'Launcher hello response was invalid.' }

    $writer.WriteLine((ConvertTo-Json @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'invoke-1'; payload = @{ method = 'getState'; payload = @{} } } -Compress))
    $writer.Flush()
    $state = $reader.ReadLine() | ConvertFrom-Json
    if ($state.messageType -ne 'module.invoke.response' -or $null -eq $state.payload.items -or $null -eq $state.payload.sortMode) { throw 'Launcher getState response was invalid.' }
    if ($state.payload.items | Get-Member -Name target -ErrorAction SilentlyContinue) { throw 'Launcher state leaked a target path.' }

    $writer.WriteLine((ConvertTo-Json @{ protocolVersion = 1; messageType = 'module.shutdown.request'; requestId = 'shutdown-1'; payload = @{} } -Compress))
    $writer.Flush()
    $shutdown = $reader.ReadLine() | ConvertFrom-Json
    if ($shutdown.messageType -ne 'module.shutdown.response') { throw 'Launcher shutdown response was invalid.' }
    if (-not $process.WaitForExit(3000)) { throw 'Launcher did not exit after shutdown.' }
    Write-Host 'Qing Launcher module smoke passed.'
} finally {
    if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $process.Dispose()
    if (Test-Path -LiteralPath $dataRoot) { Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
