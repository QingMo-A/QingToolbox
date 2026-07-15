[CmdletBinding()]
param(
    [string]$Runtime,
    [bool]$SelfContained = $false
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot "get-preview-release-metadata.ps1")
$metadata = if ([string]::IsNullOrWhiteSpace($Runtime)) {
    Get-PreviewReleaseMetadata
}
else {
    Get-PreviewReleaseMetadata -Runtime $Runtime
}
$Runtime = $metadata.Runtime
$artifactsRoot = Join-Path $repoRoot "artifacts"
$publishDirectory = Join-Path $artifactsRoot "publish\$Runtime"
$archivePath = Join-Path $artifactsRoot $metadata.PortableFileName
$checksumPath = "$archivePath.sha256"
$shellProject = Join-Path $repoRoot "QingToolbox.Shell\QingToolbox.Shell.csproj"

function Assert-PathUnderArtifacts {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $artifactsPrefix = $artifactsRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith(
        $artifactsPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside artifacts: $resolvedPath"
    }
}

try {
    Assert-PathUnderArtifacts -Path $publishDirectory
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null
    New-Item -ItemType Directory -Force -Path $artifactsRoot | Out-Null

    $selfContainedValue = $SelfContained.ToString().ToLowerInvariant()
    Write-Host "Publishing $($metadata.ProductName) $($metadata.Version) for $Runtime..."
    dotnet publish $shellProject `
        --configuration Release `
        --runtime $Runtime `
        --self-contained $selfContainedValue `
        --output $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    Get-ChildItem -LiteralPath $publishDirectory -Filter "*.pdb" -File |
        Remove-Item -Force

    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") `
        -Destination $publishDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot "CHANGELOG.md") `
        -Destination $publishDirectory -Force
    $releaseDocsDirectory = Join-Path $publishDirectory "docs"
    New-Item -ItemType Directory -Force -Path $releaseDocsDirectory | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot "docs\QMOD_FORMAT.md") `
        -Destination $releaseDocsDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot `
        "docs\releases\$($metadata.Version).md") `
        -Destination $releaseDocsDirectory -Force

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
    if (Test-Path -LiteralPath $checksumPath) {
        Remove-Item -LiteralPath $checksumPath -Force
    }

    Compress-Archive -Path (Join-Path $publishDirectory "*") `
        -DestinationPath $archivePath `
        -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    "$hash  $(Split-Path -Leaf $archivePath)" |
        Set-Content -LiteralPath $checksumPath -Encoding ASCII

    Write-Host "Publish directory: $publishDirectory"
    Write-Host "Archive:           $archivePath"
    Write-Host "SHA256:            $hash"
}
catch {
    Write-Error $_
    exit 1
}
