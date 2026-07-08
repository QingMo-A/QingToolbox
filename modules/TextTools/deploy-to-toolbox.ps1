[CmdletBinding()]
param(
    [string]$QingToolboxHostRoot = "..\..\..\QingToolbox-toolbox",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$hostRoot = if ([System.IO.Path]::IsPathRooted($QingToolboxHostRoot)) {
    [System.IO.Path]::GetFullPath($QingToolboxHostRoot)
}
else {
    [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot $QingToolboxHostRoot))
}
$buildScript = Join-Path $PSScriptRoot "build.ps1"

& $buildScript -QingToolboxHostRoot $hostRoot -Configuration $Configuration

$targetFramework = "net10.0-windows"
$moduleOutput = Join-Path $PSScriptRoot "bin\$Configuration\$targetFramework"
$moduleTarget = Join-Path $hostRoot "QingToolbox.Shell\bin\$Configuration\$targetFramework\Modules\TextTools"
$files = @(
    "QingToolbox.Modules.TextTools.dll",
    "module.json",
    "icon.svg",
    "QingToolbox.Modules.TextTools.deps.json"
)

New-Item -ItemType Directory -Force -Path $moduleTarget | Out-Null

foreach ($file in $files) {
    $source = Join-Path $moduleOutput $file
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination $moduleTarget -Force
    }
    elseif ($file -ne "QingToolbox.Modules.TextTools.deps.json") {
        throw "Required module output was not found: $source"
    }
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot "i18n") `
    -Destination $moduleTarget `
    -Recurse `
    -Force

Write-Host "Text Tools deployed to:"
Write-Host "  $moduleTarget"
Write-Host "Run the toolbox Shell and use Refresh Modules -> Load -> Open."
