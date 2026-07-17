[CmdletBinding()]
param(
    [string]$ArtifactsRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot "get-preview-release-metadata.ps1")
. (Join-Path $PSScriptRoot "assert-preview-source.ps1")

function Get-VerifiedAsset {
    param([Parameter(Mandatory = $true)][string]$AssetPath)

    if (-not (Test-Path -LiteralPath $AssetPath -PathType Leaf)) {
        throw "Preview asset is missing: $AssetPath"
    }
    $checksumPath = "$AssetPath.sha256"
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        throw "Preview checksum file is missing: $checksumPath"
    }

    $checksumText = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
    $match = [regex]::Match($checksumText,
        '^(?<hash>[0-9A-Fa-f]{64})\s{2}(?<file>[^\r\n]+)$')
    if (-not $match.Success) {
        throw "Invalid SHA256 file format: $checksumPath"
    }

    $fileName = Split-Path -Leaf $AssetPath
    if ($match.Groups["file"].Value -ne $fileName) {
        throw "Checksum filename mismatch for $AssetPath."
    }

    $expectedHash = $match.Groups["hash"].Value.ToUpperInvariant()
    $actualHash = (Get-FileHash -LiteralPath $AssetPath -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) {
        throw "SHA256 mismatch for $AssetPath. " +
              "Expected $expectedHash, actual $actualHash."
    }

    return [pscustomobject]@{
        FileName = $fileName
        Path = $AssetPath
        ChecksumPath = $checksumPath
        SizeBytes = (Get-Item -LiteralPath $AssetPath).Length
        Sha256 = $actualHash
    }
}

try {
    $source = Assert-PreviewSource
    $metadata = Get-PreviewReleaseMetadata
    if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
        $ArtifactsRoot = Join-Path $repoRoot "artifacts"
    }
    $resolvedArtifactsRoot = [System.IO.Path]::GetFullPath($ArtifactsRoot)
    $portable = Get-VerifiedAsset -AssetPath (
        Join-Path $resolvedArtifactsRoot $metadata.PortableFileName)
    $installer = Get-VerifiedAsset -AssetPath (
        Join-Path $resolvedArtifactsRoot `
            "installer\output\$($metadata.InstallerFileName)")

    $manifestPath = Join-Path $resolvedArtifactsRoot $metadata.ManifestFileName
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Preview manifest is missing: $manifestPath"
    }
    $manifestText = Get-Content -LiteralPath $manifestPath -Raw
    if ($manifestText -match '[A-Za-z]:[\\/]' -or
        $manifestText -match '(?i)runner\.temp') {
        throw "Preview manifest contains an absolute or runner-temp path."
    }
    $manifest = $manifestText | ConvertFrom-Json

    if ([int]$manifest.schemaVersion -ne 2 -or
        $manifest.product -ne $metadata.ProductName -or
        $manifest.channel -ne "Preview" -or
        $manifest.version -ne $metadata.Version -or
        $manifest.fileVersion -ne $metadata.FileVersion -or
        $manifest.releaseDisplayName -ne $metadata.ReleaseDisplayName -or
        $manifest.portableKind -ne "framework-dependent" -or
        $manifest.runtime -ne $metadata.Runtime -or
        $manifest.sourceRepository -ne "QingMo-A/QingToolbox" -or
        $manifest.sourceTreeClean -ne $true) {
        throw "Preview manifest metadata does not match release metadata."
    }
    if ($manifest.sourceCommit -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Preview manifest sourceCommit is not a full Git commit."
    }

    if ($manifest.sourceCommit -ne $source.Commit) {
        throw "Preview manifest sourceCommit does not match HEAD. " +
              "Manifest $($manifest.sourceCommit), HEAD $($source.Commit)."
    }

    $manifestArtifacts = @($manifest.artifacts)
    if ($manifestArtifacts.Count -ne 2 -or
        $manifestArtifacts[0].type -ne "portable" -or
        $manifestArtifacts[1].type -ne "installer") {
        throw "Preview manifest must contain portable then installer artifacts."
    }

    $expectedAssets = @($portable, $installer)
    for ($index = 0; $index -lt $expectedAssets.Count; $index++) {
        $entry = $manifestArtifacts[$index]
        $expected = $expectedAssets[$index]
        if ($entry.fileName -ne $expected.FileName -or
            [long]$entry.sizeBytes -ne $expected.SizeBytes -or
            ([string]$entry.sha256).ToUpperInvariant() -ne $expected.Sha256) {
            throw "Preview manifest artifact does not match: $($expected.FileName)"
        }
    }

    Write-Host "Preview asset verification passed."
    Write-Host "Source:    $($manifest.sourceCommit)"
    Write-Host "Portable:  $($portable.FileName) ($($portable.SizeBytes) bytes)"
    Write-Host "SHA256:    $($portable.Sha256)"
    Write-Host "Installer: $($installer.FileName) ($($installer.SizeBytes) bytes)"
    Write-Host "SHA256:    $($installer.Sha256)"
    Write-Host "Manifest:  $($metadata.ManifestFileName)"
}
catch {
    Write-Error -ErrorRecord $_
    exit 1
}
