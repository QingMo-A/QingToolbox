[CmdletBinding()]
param([switch]$SkipRestore, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
if (-not $SkipRestore) { & (Join-Path $PSScriptRoot 'restore-web-ui.ps1') }
if (-not $SkipTests) { & (Join-Path $PSScriptRoot 'test-web-ui.ps1') -SkipRestore }
$npm = (Get-Command npm -ErrorAction Stop).Source
$root = Join-Path $PSScriptRoot '..\QingToolbox.WebUI'
$dist = Join-Path $root 'dist'
if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
Push-Location $root
try { & $npm run build; if ($LASTEXITCODE -ne 0) { throw 'WebUI production build failed.' } }
finally { Pop-Location }
if (-not (Test-Path -LiteralPath (Join-Path $dist 'index.html') -PathType Leaf)) { throw 'WebUI build did not produce dist/index.html.' }
