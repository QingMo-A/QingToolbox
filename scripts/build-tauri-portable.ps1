[CmdletBinding()]
param(
    [switch]$SkipModuleBuild,
    [switch]$Smoke,
    [switch]$Zip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$appRoot = Join-Path $repoRoot 'QingToolbox.Tauri'
$rustRoot = Join-Path $appRoot 'src-tauri'
$releaseRoot = Join-Path $rustRoot 'target\release'
$artifactRoot = Join-Path $repoRoot 'artifacts\tauri-portable'
$stageRoot = Join-Path $artifactRoot 'QingToolbox'
$outputExe = Join-Path $stageRoot 'QingToolbox.exe'
$builtExe = Join-Path $releaseRoot 'qingtoolbox-tauri.exe'
$builtResources = Join-Path $releaseRoot 'resources'

function Assert-ArtifactPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $root = [IO.Path]::GetFullPath($artifactRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        $resolved -ne $root) {
        throw "Refusing to modify a path outside artifacts/tauri-portable: $resolved"
    }
    return $resolved
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    Write-Host "`n==> $Label"
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE."
    }
}

function Resolve-Cargo {
    $cargo = Get-Command cargo.exe -ErrorAction SilentlyContinue
    if ($cargo) { return $cargo.Source }
    $candidate = Join-Path $HOME '.cargo\bin\cargo.exe'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    throw 'cargo was not found. Install Rust stable before building the Tauri host.'
}

function Get-ProjectVersion {
    $cargoToml = Get-Content -LiteralPath (Join-Path $rustRoot 'Cargo.toml') -Raw
    $match = [regex]::Match($cargoToml, '(?m)^version\s*=\s*"([^"]+)"')
    if (-not $match.Success) { throw 'Unable to resolve the Tauri host version from Cargo.toml.' }
    return $match.Groups[1].Value
}

function Get-PortableFiles {
    param([Parameter(Mandatory = $true)][string]$Root)

    $files = @(
        Get-ChildItem -LiteralPath $Root -File -Recurse |
            Where-Object { $_.Name -notin @('portable-manifest.json') }
    )
    return @($files | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
        [ordered]@{
            path = $relative
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
}

$cargoPath = Resolve-Cargo
$env:Path = "$(Split-Path -Parent $cargoPath);$env:Path"
$version = Get-ProjectVersion

if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'package.json') -PathType Leaf)) {
    throw "Tauri application was not found: $appRoot"
}
if (-not (Test-Path -LiteralPath (Join-Path $rustRoot 'tauri.conf.json') -PathType Leaf)) {
    throw "Tauri configuration was not found: $rustRoot"
}

if (-not $SkipModuleBuild) {
    Invoke-Checked -Label 'Build protocol canary' -Action {
        & (Join-Path $repoRoot 'scripts\build-tauri-canary.ps1')
    }
    Invoke-Checked -Label 'Build Qing Launcher module' -Action {
        & (Join-Path $repoRoot 'scripts\build-tauri-launcher.ps1')
    }
    Invoke-Checked -Label 'Build Qing PDF module' -Action {
        & (Join-Path $repoRoot 'scripts\build-tauri-pdf.ps1')
    }
    Invoke-Checked -Label 'Build QingTransfer module' -Action {
        & (Join-Path $repoRoot 'scripts\build-tauri-transfer.ps1')
    }
}

Push-Location $appRoot
try {
    Invoke-Checked -Label 'Typecheck Tauri Vue frontend' -Action { npm run typecheck }
    Invoke-Checked -Label 'Build Tauri Vue frontend' -Action { npm run build }
    Invoke-Checked -Label 'Build Tauri release executable' -Action {
        npm run tauri -- build --no-bundle
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath $builtExe -PathType Leaf)) {
    throw "Tauri release executable was not produced: $builtExe"
}
if (-not (Test-Path -LiteralPath $builtResources -PathType Container)) {
    throw "Tauri release resources were not produced: $builtResources"
}

$resolvedStage = Assert-ArtifactPath $stageRoot
if (Test-Path -LiteralPath $resolvedStage) {
    # The stage is generated output. Its resolved path was checked above and
    # is kept below the repository's ignored artifacts directory.
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $resolvedStage | Out-Null
Copy-Item -LiteralPath $builtExe -Destination $outputExe -Force
Copy-Item -Path (Join-Path $builtResources '*') -Destination (Join-Path $stageRoot 'resources') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $appRoot 'THIRD_PARTY_NOTICES.md') -Destination $stageRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $stageRoot -Force

$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit)) {
    throw 'Unable to resolve the source commit for the portable manifest.'
}
$manifest = [ordered]@{
    schemaVersion = 1
    productName = 'QingToolbox'
    version = $version
    backend = 'rust'
    framework = 'tauri-2'
    frontend = 'vue-3'
    target = 'x86_64-pc-windows-msvc'
    runtime = 'WebView2 (system)'
    sourceCommit = $sourceCommit
    executable = 'QingToolbox.exe'
    files = @(Get-PortableFiles -Root $stageRoot)
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
}
$manifestPath = Join-Path $stageRoot 'portable-manifest.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8

if ($Zip) {
    $zipPath = Assert-ArtifactPath (Join-Path $artifactRoot ("QingToolbox-tauri-$version-win-x64-portable.zip"))
    if (Test-Path -LiteralPath $zipPath -PathType Leaf) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "Portable archive: $zipPath"
}

if ($Smoke) {
    Invoke-Checked -Label 'Smoke portable Tauri host' -Action {
        & (Join-Path $repoRoot 'scripts\smoke-tauri-host.ps1') -ExecutablePath $outputExe
    }
    $node = Get-Command node.exe -ErrorAction SilentlyContinue
    if (-not $node) { throw 'node.exe is required for the module-window smoke test.' }
    Invoke-Checked -Label 'Smoke portable module window' -Action {
        & $node.Source (Join-Path $repoRoot 'scripts\smoke-tauri-module-window.mjs') $outputExe
    }
}

Write-Host "`nTauri portable package prepared."
Write-Host "Directory: $stageRoot"
Write-Host "Executable SHA256: $((Get-FileHash -LiteralPath $outputExe -Algorithm SHA256).Hash)"
Write-Host "Manifest: $manifestPath"
