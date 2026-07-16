[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$shell = (Get-Process -Id $PID).Path
function Assert-Rejected {
    param([string]$Script, [string[]]$Arguments)
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        & $shell -NoProfile -ExecutionPolicy Bypass -File $Script @Arguments *> $null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -eq 0) { throw "Unsafe profile arguments were accepted by $Script." }
}
$dev = Join-Path $PSScriptRoot 'start-dev-host.ps1'
$moduleTest = Join-Path $PSScriptRoot 'start-module-test-host.ps1'
$reset = Join-Path $PSScriptRoot 'reset-local-profile.ps1'
foreach ($profile in @('..', '../escape', '..\escape', 'bad/name', 'bad\name')) {
    Assert-Rejected $dev @('-Profile', $profile, '-NoBuild')
    Assert-Rejected $moduleTest @('-Profile', $profile, '-NoBuild')
    Assert-Rejected $reset @('-Environment', 'Development', '-Profile', $profile, '-Force')
}
Write-Host "Local environment script contracts passed."
