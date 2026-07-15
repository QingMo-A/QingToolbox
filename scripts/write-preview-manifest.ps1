[CmdletBinding()]
param(
    [string]$ArtifactsRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot "get-preview-release-metadata.ps1")

function Get-VerifiedAssetRecord {
    param(
        [Parameter(Mandatory = $true)][string]$Type,
        [Parameter(Mandatory = $true)][string]$AssetPath
    )

    if (-not (Test-Path -LiteralPath $AssetPath -PathType Leaf)) {
        throw "Preview asset is missing: $AssetPath"
    }
    $checksumPath = "$AssetPath.sha256"
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        throw "Preview checksum file is missing: $checksumPath"
    }

    $actualHash = (Get-FileHash -LiteralPath $AssetPath -Algorithm SHA256).Hash
    $checksumText = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
    $match = [regex]::Match($checksumText,
        '^(?<hash>[0-9A-Fa-f]{64})\s{2}(?<file>[^\r\n]+)$')
    $fileName = Split-Path -Leaf $AssetPath
    if (-not $match.Success -or
        $match.Groups["hash"].Value.ToUpperInvariant() -ne $actualHash -or
        $match.Groups["file"].Value -ne $fileName) {
        throw "Checksum file does not match the asset: $checksumPath"
    }

    return [ordered]@{
        type = $Type
        fileName = $fileName
        sizeBytes = (Get-Item -LiteralPath $AssetPath).Length
        sha256 = $actualHash
    }
}

try {
    $metadata = Get-PreviewReleaseMetadata
    if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
        $ArtifactsRoot = Join-Path $repoRoot "artifacts"
    }
    $resolvedArtifactsRoot = [System.IO.Path]::GetFullPath($ArtifactsRoot)
    $portablePath = Join-Path $resolvedArtifactsRoot $metadata.PortableFileName
    $installerPath = Join-Path $resolvedArtifactsRoot (
        "installer\output\$($metadata.InstallerFileName)")

    $sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Unable to resolve a full source commit from Git."
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        product = $metadata.ProductName
        channel = "Preview"
        version = $metadata.Version
        fileVersion = $metadata.FileVersion
        runtime = $metadata.Runtime
        sourceCommit = $sourceCommit.ToLowerInvariant()
        generatedAtUtc = [DateTime]::UtcNow.ToString("o")
        artifacts = @(
            (Get-VerifiedAssetRecord -Type "portable" -AssetPath $portablePath),
            (Get-VerifiedAssetRecord -Type "installer" -AssetPath $installerPath)
        )
    }

    New-Item -ItemType Directory -Path $resolvedArtifactsRoot -Force | Out-Null
    $manifestPath = Join-Path $resolvedArtifactsRoot $metadata.ManifestFileName
    $manifest | ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Write-Host "Preview manifest: $manifestPath"
    Write-Host "Source commit:   $sourceCommit"
}
catch {
    Write-Error -ErrorRecord $_
    exit 1
}
