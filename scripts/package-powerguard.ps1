[CmdletBinding()]
param(
 [string]$QingToolboxHostRoot = "..\..\QingToolbox-toolbox",
 [ValidateSet("Debug","Release")][string]$Configuration = "Release",
 [string]$OutputDirectory
)
$ErrorActionPreference="Stop"
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$hostRoot=if([IO.Path]::IsPathRooted($QingToolboxHostRoot)){[IO.Path]::GetFullPath($QingToolboxHostRoot)}else{[IO.Path]::GetFullPath((Join-Path $PSScriptRoot $QingToolboxHostRoot))}
$output=if([string]::IsNullOrWhiteSpace($OutputDirectory)){Join-Path $repo "artifacts\modules"}else{[IO.Path]::GetFullPath($OutputDirectory)}
$branch=(git -C $repo branch --show-current).Trim();if($branch -ne "modules"){Write-Warning "Packaging from branch '$branch', expected 'modules'."}
& (Join-Path $PSScriptRoot "check-module-i18n.ps1") -ModulesRoot (Join-Path $repo "modules");if(-not $?){exit 1}
$project=Join-Path $repo "modules\PowerGuard\QingToolbox.Modules.PowerGuard.csproj"
dotnet build $project -c $Configuration "-p:QingToolboxHostRoot=$hostRoot";if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
$build=Join-Path (Split-Path $project) "bin\$Configuration\net10.0-windows"
$temp=Join-Path ([IO.Path]::GetTempPath()) ("PowerGuard-package-"+[Guid]::NewGuid().ToString("N"))
try{
 New-Item -ItemType Directory -Force -Path (Join-Path $temp "i18n"),$output|Out-Null
 foreach($name in @("module.json","icon.svg","QingToolbox.Modules.PowerGuard.dll")){Copy-Item -LiteralPath (Join-Path $build $name) -Destination (Join-Path $temp $name)}
 foreach($culture in @("en-US","zh-CN")){Copy-Item -LiteralPath (Join-Path $build "i18n\$culture.json") -Destination (Join-Path $temp "i18n\$culture.json")}
 $zip=Join-Path $output "QingToolbox.PowerGuard-0.1.0.zip";$qmod=Join-Path $output "QingToolbox.PowerGuard-0.1.0.qmod"
 Remove-Item -LiteralPath $zip,$qmod -Force -ErrorAction SilentlyContinue
 Compress-Archive -Path (Join-Path $temp "*") -DestinationPath $zip;Move-Item $zip $qmod
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $archive=[IO.Compression.ZipFile]::OpenRead($qmod);try{$names=@($archive.Entries|%{$_.FullName.Replace('\','/')});if(@($names|?{$_ -eq "module.json"}).Count-ne 1){throw "Package must contain exactly one root module.json."};foreach($required in @("QingToolbox.Modules.PowerGuard.dll","icon.svg","i18n/en-US.json","i18n/zh-CN.json")){if($required -notin $names){throw "Missing package entry: $required"}};if($names|?{$_ -match "QingToolbox\.Abstractions|\.pdb$|settings\.json|events\.jsonl"}){throw "Forbidden package content detected."}}finally{$archive.Dispose()}
 $hash=(Get-FileHash $qmod -Algorithm SHA256).Hash;Set-Content -LiteralPath "$qmod.sha256" -Value "$hash  $(Split-Path $qmod -Leaf)" -Encoding ASCII
 Write-Host "PowerGuard package: $qmod";Write-Host "SHA256: $hash"
}finally{if(Test-Path $temp){Remove-Item $temp -Recurse -Force}}
