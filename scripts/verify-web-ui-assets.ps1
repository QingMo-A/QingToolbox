[CmdletBinding()]param([string]$NodePath)
$ErrorActionPreference='Stop';$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\QingToolbox.WebUI'))
$node=$NodePath
if(-not$node){$node=(Get-Command node -ErrorAction SilentlyContinue).Source}
if(-not$node){throw 'Node.js is required to verify WebUI source and output identities.'}
& $node (Join-Path $root 'tools\assets.mjs') verify
if($LASTEXITCODE-ne 0){throw 'WebUI source/output asset verification failed.'}
