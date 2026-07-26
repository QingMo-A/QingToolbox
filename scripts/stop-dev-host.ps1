[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)
$shellOutputRoot = [System.IO.Path]::Combine($root, 'QingToolbox.Shell', 'bin') +
    [System.IO.Path]::DirectorySeparatorChar

$developmentProcesses = @(
    Get-CimInstance Win32_Process -Filter "Name='QingToolbox.Shell.exe'" |
        Where-Object {
            $executablePath = $_.ExecutablePath
            $commandLine = $_.CommandLine

            -not [string]::IsNullOrWhiteSpace($executablePath) -and
            -not [string]::IsNullOrWhiteSpace($commandLine) -and
            [System.IO.Path]::GetFullPath($executablePath).StartsWith(
                $shellOutputRoot,
                [System.StringComparison]::OrdinalIgnoreCase) -and
            $commandLine -match '(?i)(?:^|\s)--environment(?:\s+|=)Development(?:\s|$)'
        }
)

if ($developmentProcesses.Count -eq 0) {
    Write-Host 'The development host for this workspace is not running.'
    exit 0
}

foreach ($process in $developmentProcesses) {
    Write-Host "Stopping development host PID $($process.ProcessId)..."
    & taskkill.exe /PID $process.ProcessId /T /F | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to stop development host PID $($process.ProcessId)."
    }
}

Write-Host 'The development host and its child processes were stopped.'
