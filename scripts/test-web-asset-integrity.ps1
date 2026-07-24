[CmdletBinding()]param([string]$NodePath)
$ErrorActionPreference='Stop';$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\QingToolbox.WebUI'));$verify=Join-Path $PSScriptRoot 'verify-web-ui-assets.ps1'
$node=$NodePath;if(-not$node){$node=(Get-Command node -ErrorAction SilentlyContinue).Source};if(-not$node){throw 'Node.js is required to test WebUI asset identity.'}
& $verify -NodePath $node
$manifestPath=Join-Path $root 'dist\qing-web-assets.json';$asset=(Get-ChildItem (Join-Path $root 'dist\assets') -File|Select-Object -First 1).FullName
$manifestBytes=[IO.File]::ReadAllBytes($manifestPath);$assetBytes=[IO.File]::ReadAllBytes($asset)
try {
  [IO.File]::AppendAllText($asset,'tamper');& $verify -NodePath $node 2>$null;if($LASTEXITCODE-eq 0){throw 'Modified Dist asset was accepted.'}
} catch { if($_.Exception.Message-eq'Modified Dist asset was accepted.'){throw} } finally {[IO.File]::WriteAllBytes($asset,$assetBytes)}
try {
  $manifest=Get-Content $manifestPath -Raw|ConvertFrom-Json;$manifest.sourceTreeSha256='0'*64;$manifest|ConvertTo-Json -Depth 8|Set-Content $manifestPath -Encoding utf8NoBOM;& $verify -NodePath $node 2>$null;if($LASTEXITCODE-eq 0){throw 'Stale source identity was accepted.'}
} catch { if($_.Exception.Message-eq'Stale source identity was accepted.'){throw} } finally {[IO.File]::WriteAllBytes($manifestPath,$manifestBytes)}
foreach($toolName in @('asset-identity.mjs','assets.mjs')){$tool=Join-Path $root "tools\$toolName";$bytes=[IO.File]::ReadAllBytes($tool);try{$before=(& $node (Join-Path $root 'tools\assets.mjs') source).Trim();[IO.File]::AppendAllText($tool,"`n// identity-test");$after=(& $node (Join-Path $root 'tools\assets.mjs') source).Trim();if($before-eq$after){throw "Source identity did not change for $toolName."}}finally{[IO.File]::WriteAllBytes($tool,$bytes)}}
& $verify -NodePath $node
Write-Host 'WebUI stale-source and modified-dist integrity tests passed.'
