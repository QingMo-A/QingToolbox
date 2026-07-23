[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$npm = Get-Command npm -ErrorAction SilentlyContinue
if ($null -eq $npm) { throw 'Node.js/npm is required to restore QingToolbox.WebUI. Install Node.js 24 LTS and ensure npm is on PATH.' }
Push-Location (Join-Path $PSScriptRoot '..\QingToolbox.WebUI')
try { & $npm.Source ci; if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE." } }
finally { Pop-Location }
