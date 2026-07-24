[CmdletBinding()]param()
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest=Join-Path $root 'QingToolbox.WebUI\dist\qing-web-assets.json'
$generator=Join-Path $PSScriptRoot 'generate-web-asset-build-info.ps1'
$temp=Join-Path ([IO.Path]::GetTempPath()) ('QingToolbox-WebAssetGenerator-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp)|Out-Null
try {
  $windowsOutput=Join-Path $temp 'windows.g.cs'
  & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $generator -ManifestPath $manifest -OutputPath $windowsOutput
  if($LASTEXITCODE-ne 0){throw 'Windows PowerShell Web asset generation failed.'}
  $windowsBytes=[IO.File]::ReadAllBytes($windowsOutput)
  if($windowsBytes.Length-ge 3-and$windowsBytes[0]-eq 0xEF-and$windowsBytes[1]-eq 0xBB-and$windowsBytes[2]-eq 0xBF){throw 'Generated Web asset source contains a UTF-8 BOM.'}
  $pwsh=Get-Command pwsh -ErrorAction SilentlyContinue
  if($pwsh){$pwshOutput=Join-Path $temp 'pwsh.g.cs';& $pwsh.Source -NoProfile -File $generator -ManifestPath $manifest -OutputPath $pwshOutput;if($LASTEXITCODE-ne 0){throw 'PowerShell 7 Web asset generation failed.'};if([Convert]::ToBase64String($windowsBytes)-ne[Convert]::ToBase64String([IO.File]::ReadAllBytes($pwshOutput))){throw 'PowerShell editions generated different Web asset identities.'}}
  foreach($path in @($generator,(Join-Path $PSScriptRoot 'verify-web-ui-assets.ps1'))){$text=[IO.File]::ReadAllText($path);if($text-match'(?i)codex-runtimes|\\Users\\[^\\]+'){throw "Machine-specific runtime path found in $path."}}
  Write-Host 'PowerShell Web asset generator compatibility passed.'
} finally {if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Recurse -Force}}
