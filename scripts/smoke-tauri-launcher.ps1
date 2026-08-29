[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,
    [switch]$Everything,
    [switch]$EverythingService
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
$everythingIndexRoot = $null
if ($Everything -and -not $EverythingService) {
    $everythingIndexRoot = Join-Path $dataRoot 'everything-index'
    New-Item -ItemType Directory -Force -Path $everythingIndexRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $everythingIndexRoot 'qing-everything-smoke.txt') -Value 'Everything smoke' -Encoding utf8
}
$psi.Environment['QINGTOOLBOX_MODULE_DATA_DIR'] = $dataRoot
if ($Everything -or $EverythingService) {
    # The controlled root keeps this optional integration smoke bounded and
    # avoids changing a developer's global Everything index configuration.
    $moduleDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\QingToolbox.Tauri\native-launcher'))
    $psi.Environment['QINGTOOLBOX_MODULE_DIRECTORY'] = $moduleDirectory
    if ($EverythingService) {
        # Exercise the production service path against a file on a fixed
        # volume.  The dedicated service is already installed by the user (or
        # the smoke fails with a clear unavailable status); no global index
        # configuration is modified by this script.
        $psi.Environment['QING_LAUNCHER_EVERYTHING_SERVICE'] = '1'
    } else {
        $psi.Environment['QING_LAUNCHER_EVERYTHING_SERVICE'] = '0'
        $psi.Environment['QING_LAUNCHER_EVERYTHING_INDEX_ROOTS'] = $everythingIndexRoot
    }
}
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

    if ($Everything) {
        $everythingQuery = if ($EverythingService) { 'Everything.exe' } else { 'qing-everything-smoke.txt' }
        $writer.WriteLine((ConvertTo-Json @{ protocolVersion = 1; messageType = 'module.invoke.request'; requestId = 'everything-1'; payload = @{ method = 'searchEverything'; payload = @{ mode = 'everything-file'; query = $everythingQuery; requestId = 'smoke-1' } } } -Compress))
        $writer.Flush()
        $everythingResponse = $reader.ReadLine() | ConvertFrom-Json
        if ($everythingResponse.messageType -ne 'module.invoke.response' -or $everythingResponse.payload.status -ne 'ready') { throw "Everything search was not ready: $($everythingResponse | ConvertTo-Json -Compress)" }
        if (-not ($everythingResponse.payload.results | Where-Object { $_.name -eq $everythingQuery })) { throw "Everything search did not return $everythingQuery." }
        $resultId = ($everythingResponse.payload.results | Where-Object { $_.name -eq $everythingQuery } | Select-Object -First 1).id
        if ([string]::IsNullOrWhiteSpace($resultId) -or $resultId -match '[\\/]') { throw 'Everything response exposed an unsafe result id.' }
    }

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
