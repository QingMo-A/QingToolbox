[CmdletBinding()]
param([string]$QingToolboxHostRoot="..\..\..\QingToolbox-toolbox",[ValidateSet("Debug","Release")][string]$Configuration="Debug")
$ErrorActionPreference="Stop"
$hostRoot=if([IO.Path]::IsPathRooted($QingToolboxHostRoot)){[IO.Path]::GetFullPath($QingToolboxHostRoot)}else{[IO.Path]::GetFullPath((Join-Path $PSScriptRoot $QingToolboxHostRoot))}
$project=Join-Path $PSScriptRoot "QingToolbox.Modules.ScreenPin.csproj"
dotnet build $project -c $Configuration "-p:QingToolboxHostRoot=$hostRoot"
if($LASTEXITCODE-ne 0){throw "Screen Pin build failed."}
Write-Host "Output: $(Join-Path $PSScriptRoot "bin\$Configuration\net10.0-windows")"
