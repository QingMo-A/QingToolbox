[CmdletBinding()]param()
$ErrorActionPreference='Stop';$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\QingToolbox.WebUI'));$verify=Join-Path $PSScriptRoot 'verify-web-ui-assets.ps1'
& $verify
$manifestPath=Join-Path $root 'dist\qing-web-assets.json';$asset=(Get-ChildItem (Join-Path $root 'dist\assets') -File|Select-Object -First 1).FullName
$manifestBytes=[IO.File]::ReadAllBytes($manifestPath);$assetBytes=[IO.File]::ReadAllBytes($asset)
try {
  [IO.File]::AppendAllText($asset,'tamper');& $verify 2>$null;if($LASTEXITCODE-eq 0){throw 'Modified Dist asset was accepted.'}
} catch { if($_.Exception.Message-eq'Modified Dist asset was accepted.'){throw} } finally {[IO.File]::WriteAllBytes($asset,$assetBytes)}
try {
  $manifest=Get-Content $manifestPath -Raw|ConvertFrom-Json;$manifest.sourceTreeSha256='0'*64;$manifest|ConvertTo-Json -Depth 8|Set-Content $manifestPath -Encoding utf8NoBOM;& $verify 2>$null;if($LASTEXITCODE-eq 0){throw 'Stale source identity was accepted.'}
} catch { if($_.Exception.Message-eq'Stale source identity was accepted.'){throw} } finally {[IO.File]::WriteAllBytes($manifestPath,$manifestBytes)}
& $verify
Write-Host 'WebUI stale-source and modified-dist integrity tests passed.'
