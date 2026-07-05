[CmdletBinding()]
param(
    [string]$QingToolboxHostRoot = "..\..\..\QingToolbox-toolbox",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "QingToolbox.Modules.TextTools.csproj"
$hostRoot = if ([System.IO.Path]::IsPathRooted($QingToolboxHostRoot)) {
    [System.IO.Path]::GetFullPath($QingToolboxHostRoot)
}
else {
    [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot $QingToolboxHostRoot))
}
$abstractionsProject = Join-Path $hostRoot "QingToolbox.Abstractions\QingToolbox.Abstractions.csproj"

if (-not (Test-Path -LiteralPath $abstractionsProject)) {
    throw "QingToolbox.Abstractions project was not found: $abstractionsProject"
}

Write-Host "Building Text Tools ($Configuration)..."
Write-Host "Host root: $hostRoot"
dotnet build $project -c $Configuration "-p:QingToolboxHostRoot=$hostRoot"

if ($LASTEXITCODE -ne 0) {
    throw "Text Tools build failed with exit code $LASTEXITCODE."
}
