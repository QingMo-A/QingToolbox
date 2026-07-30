[CmdletBinding()]
param([string]$QingToolboxHostRoot="..\QingToolbox-toolbox",[ValidateSet("Debug","Release")][string]$Configuration="Release",[string]$OutputDirectory)
& (Join-Path $PSScriptRoot "package-module.ps1") -ModuleName PowerGuard -QingToolboxHostRoot $QingToolboxHostRoot -Configuration $Configuration -OutputDirectory $OutputDirectory -SmokeTestProject "tests\PowerGuard.SmokeTest\QingToolbox.Modules.PowerGuard.SmokeTest.csproj"
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
