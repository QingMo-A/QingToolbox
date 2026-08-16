[CmdletBinding()]
param(
    [string]$QingToolboxHostRoot = "..\..\QingToolbox-toolbox",
    [ValidateSet("Debug", "Release")][string]$Configuration = "Release",
    [string]$OutputDirectory
)

$arguments = @{
    ModuleName = "Launcher"
    QingToolboxHostRoot = $QingToolboxHostRoot
    Configuration = $Configuration
    SmokeTestProject = "tests\Launcher.SmokeTest\QingToolbox.Modules.Launcher.SmokeTest.csproj"
}
if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) { $arguments.OutputDirectory = $OutputDirectory }
& (Join-Path $PSScriptRoot "package-module.ps1") @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project (Join-Path $PSScriptRoot "..\tests\Launcher.SmokeTest\QingToolbox.Modules.Launcher.SmokeTest.csproj") `
    -c $Configuration "/p:QingToolboxHostRoot=$QingToolboxHostRoot" -- --everything-runtime
if ($LASTEXITCODE -ne 0) { throw "Launcher built-in Everything runtime smoke test failed." }
