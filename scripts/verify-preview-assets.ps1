[CmdletBinding()]
param(
    [string]$ArtifactsRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

function Assert-AssetChecksum {
    param([Parameter(Mandatory = $true)][string]$AssetPath)

    if (-not (Test-Path -LiteralPath $AssetPath -PathType Leaf)) {
        throw "Preview asset is missing: $AssetPath"
    }
    $checksumPath = "$AssetPath.sha256"
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        throw "Preview checksum file is missing: $checksumPath"
    }

    $checksumText = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
    $match = [regex]::Match($checksumText, `
        '^(?<hash>[0-9A-Fa-f]{64})\s{2}(?<file>[^\r\n]+)$')
    if (-not $match.Success) {
        throw "Invalid SHA256 file format: $checksumPath"
    }

    $expectedFilename = Split-Path -Leaf $AssetPath
    $recordedFilename = $match.Groups["file"].Value
    if ($recordedFilename -ne $expectedFilename) {
        throw "Checksum filename mismatch for $AssetPath. " +
              "Expected '$expectedFilename', recorded '$recordedFilename'."
    }

    $expectedHash = $match.Groups["hash"].Value.ToUpperInvariant()
    $actualHash = (Get-FileHash -LiteralPath $AssetPath -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) {
        throw "SHA256 mismatch for $AssetPath. " +
              "Expected $expectedHash, actual $actualHash."
    }

    $size = (Get-Item -LiteralPath $AssetPath).Length
    Write-Host "Verified: $AssetPath"
    Write-Host "Size:     $size bytes"
    Write-Host "SHA256:   $actualHash"
}

try {
    if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
        $ArtifactsRoot = Join-Path $repoRoot "artifacts"
    }
    $resolvedArtifactsRoot = [System.IO.Path]::GetFullPath($ArtifactsRoot)
    $propsPath = Join-Path $repoRoot "Directory.Build.props"
    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $version = [string]@($props.Project.PropertyGroup.Version)[0]
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "Version is missing from Directory.Build.props."
    }

    $runtime = "win-x64"
    $archivePath = Join-Path $resolvedArtifactsRoot `
        "QingToolbox-$version-$runtime.zip"
    $installerPath = Join-Path $resolvedArtifactsRoot `
        "installer\output\QingToolbox-$version-$runtime-setup.exe"

    Assert-AssetChecksum -AssetPath $archivePath
    Assert-AssetChecksum -AssetPath $installerPath
    Write-Host "Preview asset verification passed."
}
catch {
    Write-Error -ErrorRecord $_
    exit 1
}
