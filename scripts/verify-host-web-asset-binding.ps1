[CmdletBinding()]param([Parameter(Mandatory=$true)][string]$PayloadRoot)
$ErrorActionPreference='Stop';$root=[IO.Path]::GetFullPath($PayloadRoot);$web=Join-Path $root 'WebUI';$shell=Join-Path $root 'QingToolbox.Shell.dll'
& (Join-Path $PSScriptRoot 'verify-packaged-web-assets.ps1') -WebUIRoot $web
if(-not(Test-Path -LiteralPath $shell -PathType Leaf)){throw 'Packaged Shell assembly is missing.'}
$manifestPath=Join-Path $web 'qing-web-assets.json';$manifest=Get-Content $manifestPath -Raw -Encoding UTF8|ConvertFrom-Json;$actualHash=(Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant();$assetId=[string]$manifest.assetBuildId
$assemblyBytes=[IO.File]::ReadAllBytes($shell);$assemblyUtf8=[Text.Encoding]::UTF8.GetString($assemblyBytes);$assemblyUnicode=[Text.Encoding]::Unicode.GetString($assemblyBytes)
if((-not$assemblyUtf8.Contains($actualHash)-and-not$assemblyUnicode.Contains($actualHash))-or(-not$assemblyUtf8.Contains($assetId)-and-not$assemblyUnicode.Contains($assetId))){throw 'Packaged WebUI identity is not anchored to the Shell assembly.'}
Write-Host "Verified host/WebUI binding; buildId=$assetId; manifestSha256=$actualHash"
