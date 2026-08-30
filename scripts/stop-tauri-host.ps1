[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)

# Only target executables produced inside this checkout.  In particular, do
# not match by process name alone: an installed QingToolbox or another user's
# Tauri process must never be terminated by the development stop button.
$candidatePaths = @(
    (Join-Path $root 'QingToolbox.Tauri\src-tauri\target\debug\qingtoolbox-tauri.exe'),
    (Join-Path $root 'QingToolbox.Tauri\src-tauri\target\release\qingtoolbox-tauri.exe'),
    (Join-Path $root 'artifacts\tauri-production\QingToolbox\QingToolbox.exe'),
    (Join-Path $root 'artifacts\tauri-portable\QingToolbox\QingToolbox.exe')
) | ForEach-Object { [IO.Path]::GetFullPath($_) }

$candidateSet = @{}
foreach ($path in $candidatePaths) { $candidateSet[$path] = $true }

$processes = @(
    Get-CimInstance Win32_Process |
        Where-Object {
            $path = $_.ExecutablePath
            if ([string]::IsNullOrWhiteSpace($path)) { return $false }
            try {
                $candidateSet.ContainsKey([IO.Path]::GetFullPath($path))
            }
            catch {
                $false
            }
        }
)

if ($processes.Count -eq 0) {
    Write-Host 'The Tauri host from this workspace is not running.'
    exit 0
}

foreach ($process in $processes) {
    Write-Host "Stopping Tauri host PID $($process.ProcessId)..."
    & taskkill.exe /PID $process.ProcessId /T /F | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to stop Tauri host PID $($process.ProcessId)."
    }
}

Write-Host 'The Tauri host and its child module processes were stopped.'
