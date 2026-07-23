[CmdletBinding()]
param([switch]$SkipRestore)
$ErrorActionPreference = 'Stop'
if (-not $SkipRestore) { & (Join-Path $PSScriptRoot 'restore-web-ui.ps1') }
$npm = (Get-Command npm -ErrorAction Stop).Source
Push-Location (Join-Path $PSScriptRoot '..\QingToolbox.WebUI')
try {
  & $npm run typecheck; if ($LASTEXITCODE -ne 0) { throw 'WebUI type check failed.' }
  & $npm run test; if ($LASTEXITCODE -ne 0) { throw 'WebUI tests failed.' }
}
finally { Pop-Location }
