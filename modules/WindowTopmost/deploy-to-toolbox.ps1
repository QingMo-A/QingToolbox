[CmdletBinding()]
param([Alias("ToolboxPath")][string]$QingToolboxHostRoot="..\..\..\QingToolbox-toolbox",[ValidateSet("Debug","Release")][string]$Configuration="Debug")
$ErrorActionPreference="Stop"
$hostRoot=if([IO.Path]::IsPathRooted($QingToolboxHostRoot)){[IO.Path]::GetFullPath($QingToolboxHostRoot)}else{[IO.Path]::GetFullPath((Join-Path $PSScriptRoot $QingToolboxHostRoot))}
& (Join-Path $PSScriptRoot "build.ps1") -QingToolboxHostRoot $hostRoot -Configuration $Configuration
$output=Join-Path $PSScriptRoot "bin\$Configuration\net10.0-windows"
$target=Join-Path $hostRoot "QingToolbox.Shell\bin\$Configuration\net10.0-windows\Modules\WindowTopmost"
New-Item -ItemType Directory -Force $target|Out-Null
foreach($file in @("QingToolbox.Modules.WindowTopmost.dll","QingToolbox.Modules.WindowTopmost.deps.json","module.json","icon.svg")){$source=Join-Path $output $file;if(Test-Path $source){Copy-Item $source $target -Force}}
Copy-Item (Join-Path $PSScriptRoot "i18n") $target -Recurse -Force
Write-Host "Window Topmost deployed to: $target"
