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

Write-Host "Module verification passed."
