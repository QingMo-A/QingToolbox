[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Error @'
Portable QingToolbox distribution was retired in Plan 013A.
Use scripts/build-installer.ps1 to build the supported Windows installer. The
installer build retains the internal publish directory, host payload audit,
Host Payload Manifest, and Web asset binding verification.
'@
exit 1
