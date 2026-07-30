[CmdletBinding()]
param([string]$QingToolboxHostRoot="..\QingToolbox-toolbox",[ValidateSet("Debug","Release")][string]$Configuration="Release",[string]$OutputDirectory)
& (Join-Path $PSScriptRoot "package-module.ps1") -ModuleName TextTools -QingToolboxHostRoot $QingToolboxHostRoot -Configuration $Configuration -OutputDirectory $OutputDirectory
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
