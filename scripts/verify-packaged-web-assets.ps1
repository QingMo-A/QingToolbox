[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$WebUIRoot)
$ErrorActionPreference='Stop';$root=[IO.Path]::GetFullPath($WebUIRoot);$manifestPath=Join-Path $root 'qing-web-assets.json'
if(-not(Test-Path -LiteralPath $manifestPath -PathType Leaf)){throw 'Packaged WebUI asset manifest is missing.'}
$manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8|ConvertFrom-Json
$expected=@{};foreach($item in $manifest.outputFiles){$relative=[string]$item.path;if([IO.Path]::IsPathRooted($relative)-or$relative.Contains('..')){throw 'WebUI manifest contains an unsafe path.'};$expected[$relative.Replace('/','\')]=$item}
$actual=@(Get-ChildItem -LiteralPath $root -Recurse -File|Where-Object Name -ne 'qing-web-assets.json')
if($actual.Count-ne$expected.Count){throw 'Packaged WebUI file set does not match its manifest.'}
$prefix=$root.TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
foreach($file in $actual){if(-not$file.FullName.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Packaged WebUI file escaped its root.'};$relative=$file.FullName.Substring($prefix.Length);if(-not$expected.ContainsKey($relative)){throw "Unexpected packaged WebUI file: $relative"};$item=$expected[$relative];if($file.Length-ne[long]$item.size-or(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()-ne[string]$item.sha256){throw "Packaged WebUI hash mismatch: $relative"}}
Write-Host "Verified packaged WebUI assets: $($actual.Count) files; buildId=$($manifest.assetBuildId)"
