[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repoRoot "QingToolbox.sln"
$targetFramework = "net10.0-windows"
$helloOutput = Join-Path $repoRoot "QingToolbox.Modules.Hello\bin\$Configuration\$targetFramework"
$shellOutput = Join-Path $repoRoot "QingToolbox.Shell\bin\$Configuration\$targetFramework"
$moduleTarget = Join-Path $shellOutput "Modules\Hello"
$helloAssembly = Join-Path $helloOutput "QingToolbox.Modules.Hello.dll"
$helloManifest = Join-Path $repoRoot "QingToolbox.Modules.Hello\module.json"

Write-Host "Building QingToolbox solution ($Configuration)..."
dotnet build $solutionPath --configuration $Configuration

if ($LASTEXITCODE -ne 0) {
    throw "Solution build failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $helloAssembly)) {
    throw "Hello module assembly was not found: $helloAssembly"
}

if (-not (Test-Path -LiteralPath $helloManifest)) {
    throw "Hello module manifest was not found: $helloManifest"
}

Write-Host "Creating runtime module directory..."
New-Item -ItemType Directory -Force -Path $moduleTarget | Out-Null

Write-Host "Deploying Hello module manifest and assembly..."
Copy-Item -LiteralPath $helloAssembly -Destination $moduleTarget -Force
Copy-Item -LiteralPath $helloManifest -Destination $moduleTarget -Force

Write-Host "Hello module deployed to:"
Write-Host "  $moduleTarget"
Write-Host "Run the Shell with:"
Write-Host "  dotnet run --project QingToolbox.Shell"
Write-Host "Run the module load smoke test with:"
Write-Host "  dotnet run --project QingToolbox.DevTools.ModuleLoadSmokeTest"
