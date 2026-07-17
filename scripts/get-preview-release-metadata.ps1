Set-StrictMode -Version Latest

function Get-PreviewReleaseMetadata {
    [CmdletBinding()]
    param(
        [ValidateSet("win-x64")]
        [string]$Runtime = "win-x64",
        [string]$PropsPath
    )

    if ([string]::IsNullOrWhiteSpace($PropsPath)) {
        $repoRoot = [System.IO.Path]::GetFullPath(
            (Split-Path -Parent $PSScriptRoot))
        $PropsPath = Join-Path $repoRoot "Directory.Build.props"
    }
    $resolvedPropsPath = [System.IO.Path]::GetFullPath($PropsPath)
    if (-not (Test-Path -LiteralPath $resolvedPropsPath -PathType Leaf)) {
        throw "Directory.Build.props was not found: $resolvedPropsPath"
    }

    [xml]$props = Get-Content -LiteralPath $resolvedPropsPath -Raw
    $version = [string]@($props.Project.PropertyGroup.Version)[0]
    $fileVersion = [string]@($props.Project.PropertyGroup.FileVersion)[0]
    $previewNumber = [string]@($props.Project.PropertyGroup.PreviewNumber)[0]
    $releaseDisplayName = [string]@($props.Project.PropertyGroup.ReleaseDisplayName)[0]
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "Version is missing from Directory.Build.props."
    }
    if ($fileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "FileVersion must contain four numeric components: '$fileVersion'."
    }
    if ($previewNumber -notmatch '^\d+$' -or [string]::IsNullOrWhiteSpace($releaseDisplayName)) {
        throw "PreviewNumber and ReleaseDisplayName are required release metadata."
    }

    $productName = "QingToolbox"
    $portableBaseName = "$productName-$version-$Runtime"
    $installerBaseName = "$portableBaseName-setup"
    $metadata = [pscustomobject]@{
        ProductName = $productName
        Version = $version
        FileVersion = $fileVersion
        PreviewNumber = [int]$previewNumber
        ReleaseDisplayName = $releaseDisplayName
        ProductDisplayName = "$productName $version $releaseDisplayName"
        PortableKind = "framework-dependent"
        Runtime = $Runtime
        PortableBaseName = $portableBaseName
        PortableFileName = "$portableBaseName.zip"
        InstallerBaseName = $installerBaseName
        InstallerFileName = "$installerBaseName.exe"
        ManifestFileName = "$portableBaseName.manifest.json"
        ValidationArtifactName = "$productName-$version-preview-$Runtime"
    }

    foreach ($name in @(
        $metadata.PortableFileName,
        $metadata.InstallerFileName,
        $metadata.ManifestFileName,
        $metadata.ValidationArtifactName)) {
        if ($name.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
            $name.Contains([System.IO.Path]::DirectorySeparatorChar) -or
            $name.Contains([System.IO.Path]::AltDirectorySeparatorChar)) {
            throw "Generated release name contains invalid path characters: $name"
        }
    }

    return $metadata
}

if ($MyInvocation.InvocationName -ne ".") {
    Get-PreviewReleaseMetadata
}
