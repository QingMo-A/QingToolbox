[CmdletBinding()]
param(
    [string]$ModulesRoot,
    [string]$QingToolboxHostRoot = "..\..\QingToolbox-toolbox",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$ModulesRoot = if ([string]::IsNullOrWhiteSpace($ModulesRoot)) {
    Join-Path $PSScriptRoot "..\modules"
}
else {
    $ModulesRoot
}
$resolvedModulesRoot = [System.IO.Path]::GetFullPath($ModulesRoot)
$hostRoot = if ([System.IO.Path]::IsPathRooted($QingToolboxHostRoot)) {
    [System.IO.Path]::GetFullPath($QingToolboxHostRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $QingToolboxHostRoot))
}

$validatorProject = Join-Path $PSScriptRoot "..\tools\QingToolbox.ModuleUpdateMetadataValidator\QingToolbox.ModuleUpdateMetadataValidator.csproj"
Write-Host "Running module update metadata validator self-test..."
dotnet run --project $validatorProject -c $Configuration -- --self-test
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Validating module update metadata..."
dotnet run --project $validatorProject -c $Configuration -- --modules-root $resolvedModulesRoot
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $PSScriptRoot "check-module-i18n.ps1") -ModulesRoot $resolvedModulesRoot
if (-not $?) {
    exit 1
}

$projects = Get-ChildItem -LiteralPath $resolvedModulesRoot -Recurse -Filter "*.csproj" -File
if ($projects.Count -eq 0) {
    throw "No module projects found under: $resolvedModulesRoot"
}

foreach ($project in $projects) {
    Write-Host "Building $($project.Name) ($Configuration)..."
    dotnet build $project.FullName -c $Configuration "-p:QingToolboxHostRoot=$hostRoot"
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$powerGuardSmoke = Join-Path $PSScriptRoot "..\tests\PowerGuard.SmokeTest\QingToolbox.Modules.PowerGuard.SmokeTest.csproj"
if (Test-Path -LiteralPath $powerGuardSmoke -PathType Leaf) {
    Write-Host "Building and running PowerGuard safe smoke test ($Configuration)..."
    dotnet run --project $powerGuardSmoke -c $Configuration "-p:QingToolboxHostRoot=$hostRoot"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "Module verification passed."
