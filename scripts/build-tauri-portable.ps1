[CmdletBinding()]
param(
    [switch]$SkipModuleBuild,
    [switch]$Smoke,
    [switch]$Zip,
    [string]$OutputDirectory,
    [ValidateSet('portable-preview', 'production')]
    [string]$Distribution = 'portable-preview'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tauri-packaging-path.ps1')

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$appRoot = Join-Path $repoRoot 'QingToolbox.Tauri'
$rustRoot = Join-Path $appRoot 'src-tauri'
$releaseRoot = Join-Path $rustRoot 'target\release'
$artifactsDirectory = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$artifactRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $artifactsDirectory 'tauri-portable'
} else {
    [IO.Path]::GetFullPath($OutputDirectory)
}
$stageRoot = Join-Path $artifactRoot 'QingToolbox'
$outputExe = Join-Path $stageRoot 'QingToolbox.exe'
$builtExe = Join-Path $releaseRoot 'qingtoolbox-tauri.exe'
$builtResources = Join-Path $releaseRoot 'resources'
$sourceBundledModules = Join-Path $rustRoot 'resources\modules'
$cacheScript = Join-Path $PSScriptRoot 'tauri-build-cache.mjs'

function Assert-ArtifactPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $root = [IO.Path]::GetFullPath($artifactRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        $resolved -ne $root) {
        throw "Refusing to modify a path outside the selected Tauri artifact root: $resolved"
    }
    return $resolved
}

$artifactsPrefix = $artifactsDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $artifactRoot.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $artifactRoot -eq $artifactsDirectory) {
    throw "Refusing to write outside the repository artifacts directory: $artifactRoot"
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

function Stop-WorkspaceTauriHosts {
    # A running portable executable keeps its image locked on Windows. Stop
    # only hosts produced by this checkout before replacing generated stages;
    # installed/user copies are deliberately outside these exact paths.
    $candidatePaths = @(
        (Join-Path $repoRoot 'QingToolbox.Tauri\src-tauri\target\debug\qingtoolbox-tauri.exe'),
        (Join-Path $repoRoot 'QingToolbox.Tauri\src-tauri\target\release\qingtoolbox-tauri.exe'),
        (Join-Path $repoRoot 'artifacts\tauri-production\QingToolbox\QingToolbox.exe'),
        (Join-Path $repoRoot 'artifacts\tauri-portable\QingToolbox\QingToolbox.exe')
    ) | ForEach-Object { [IO.Path]::GetFullPath($_) }
    $candidateSet = @{}
    foreach ($path in $candidatePaths) { $candidateSet[$path] = $true }
    foreach ($process in @(Get-CimInstance Win32_Process | Where-Object {
        $path = $_.ExecutablePath
        if ([string]::IsNullOrWhiteSpace($path)) { return $false }
        try { $candidateSet.ContainsKey([IO.Path]::GetFullPath($path)) }
        catch { $false }
    })) {
        Write-Host "Stopping workspace Tauri host PID $($process.ProcessId) before rebuilding..."
        & taskkill.exe /PID $process.ProcessId /T /F | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Failed to stop workspace Tauri host PID $($process.ProcessId)." }
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
        $relative = (Get-TauriRelativePath -Root $Root -Path $_.FullName).Replace('\', '/')
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
    Invoke-Checked -Label 'Prepare changed Tauri modules' -Action {
        & (Join-Path $repoRoot 'scripts\build-tauri-modules.ps1')
    }
}

$buildFingerprint = & node $cacheScript fingerprint host
if ($LASTEXITCODE -ne 0) { throw 'Cannot fingerprint Tauri build inputs.' }
Push-Location $appRoot
try {
    # tauri build runs beforeBuildCommand once; that Vue build includes typecheck.
    # Tauri copies resource files into target/release but does not prune old
    # hashed Vite assets on every incremental build. Clear only this exact,
    # generated resource directory so the production package stays small and
    # cannot carry an unreachable UI bundle from an older module build.
    $expectedReleaseResources = [IO.Path]::GetFullPath((Join-Path $rustRoot 'target\release\resources'))
    if ([IO.Path]::GetFullPath($builtResources) -ne $expectedReleaseResources) {
        throw "Refusing to clean an unexpected Tauri release resource path: $builtResources"
    }
    Stop-WorkspaceTauriHosts
    if (Test-Path -LiteralPath $expectedReleaseResources -PathType Container) {
        Remove-Item -LiteralPath $expectedReleaseResources -Recurse -Force
    }
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
# `bundle.active=false` keeps the portable build independent from Tauri's
# platform bundler (and avoids downloading installer toolchains), so
# `tauri build --no-bundle` is not required to materialize bundle resources.
# The module build scripts already publish validated binaries/assets into this
# repository-owned source root; copy that exact root into the release layout
# explicitly instead of relying on an incidental bundler side effect.
if (-not (Test-Path -LiteralPath $sourceBundledModules -PathType Container)) {
    throw "Bundled module resources were not produced: $sourceBundledModules"
}
$expectedReleaseResources = [IO.Path]::GetFullPath((Join-Path $rustRoot 'target\release\resources'))
if ([IO.Path]::GetFullPath($builtResources) -ne $expectedReleaseResources) {
    throw "Refusing to stage an unexpected Tauri release resource path: $builtResources"
}
if (Test-Path -LiteralPath $builtResources) {
    Remove-Item -LiteralPath $builtResources -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $builtResources | Out-Null
Copy-Item -LiteralPath $sourceBundledModules -Destination $builtResources -Recurse -Force
$bundledModules = Join-Path $builtResources 'modules'
if (-not (Test-Path -LiteralPath $bundledModules -PathType Container)) {
    throw "Failed to stage bundled modules into the release layout: $bundledModules"
}

$resolvedStage = Assert-ArtifactPath $stageRoot
if (Test-Path -LiteralPath $resolvedStage) {
    # The stage is generated output. Its resolved path was checked above and
    # is kept below the repository's ignored artifacts directory.
    Stop-WorkspaceTauriHosts
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $resolvedStage | Out-Null
Copy-Item -LiteralPath $builtExe -Destination $outputExe -Force
$stageResources = Join-Path $stageRoot 'resources'
New-Item -ItemType Directory -Force -Path $stageResources | Out-Null
# Keep the resource root shape intact. Copying `resources\*` into a newly
# created directory can flatten the `modules` child on Windows PowerShell,
# which makes the portable host fall back to user-installed (legacy) modules.
# The bundled host must always see resources/modules/<module-id>.
Copy-Item -LiteralPath $bundledModules -Destination $stageResources -Recurse -Force
Copy-Item -LiteralPath (Join-Path $appRoot 'THIRD_PARTY_NOTICES.md') -Destination $stageRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $stageRoot -Force

$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit)) {
    throw 'Unable to resolve the source commit for the portable manifest.'
}
$sourceDirty = @(& git -C $repoRoot status --porcelain --untracked-files=normal).Count -gt 0
$manifest = [ordered]@{
    schemaVersion = 1
    productName = 'QingToolbox'
    distribution = $Distribution
    version = $version
    backend = 'rust'
    framework = 'tauri-2'
    frontend = 'vue-3'
    buildProfile = 'release'
    target = 'x86_64-pc-windows-msvc'
    runtime = 'WebView2 (system)'
    sourceCommit = $sourceCommit
    sourceDirty = $sourceDirty
    executable = 'QingToolbox.exe'
    files = @(Get-PortableFiles -Root $stageRoot)
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
}
$manifestPath = Join-Path $stageRoot 'portable-manifest.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
if ($Distribution -eq 'production' -and $stageRoot -eq (Join-Path $repoRoot 'artifacts\tauri-production\QingToolbox')) {
    & node $cacheScript save host $buildFingerprint
    if ($LASTEXITCODE -ne 0) { throw 'Cannot save the development build cache.' }
}

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
