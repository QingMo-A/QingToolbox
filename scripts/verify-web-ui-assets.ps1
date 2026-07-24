[CmdletBinding()]param()
$ErrorActionPreference='Stop';$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\QingToolbox.WebUI'))
$node=(Get-Command node -ErrorAction SilentlyContinue).Source
if(-not$node){$candidate=Join-Path $HOME '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe';if(Test-Path -LiteralPath $candidate){$node=$candidate}}
if(-not$node){throw 'Node.js is required to verify WebUI source and output identities.'}
& $node (Join-Path $root 'tools\assets.mjs') verify
if($LASTEXITCODE-ne 0){throw 'WebUI source/output asset verification failed.'}
