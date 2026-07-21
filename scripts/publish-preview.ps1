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
$maintenanceProject = Join-Path $repoRoot "QingToolbox.StartupMaintenance\QingToolbox.StartupMaintenance.csproj"

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
    dotnet publish $maintenanceProject `
        --configuration Release `
        --runtime $Runtime `
        --self-contained $selfContainedValue `
        --output $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Startup maintenance publish failed with exit code $LASTEXITCODE."
    }

    Get-ChildItem -LiteralPath $publishDirectory -Filter "*.pdb" -File -Recurse |
        Remove-Item -Force

    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") `
        -Destination $publishDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot "CHANGELOG.md") `
        -Destination $publishDirectory -Force
    $releaseDocsDirectory = Join-Path $publishDirectory "docs"
    $releaseNotesDirectory = Join-Path $releaseDocsDirectory "releases"
    $sdkDocsDirectory = Join-Path $releaseDocsDirectory "sdk"
    New-Item -ItemType Directory -Force -Path $releaseNotesDirectory | Out-Null
    New-Item -ItemType Directory -Force -Path $sdkDocsDirectory | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot "docs\QMOD_FORMAT.md") `
        -Destination $releaseDocsDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot "docs\sdk\README.md") `
        -Destination $sdkDocsDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot `
        "docs\releases\$($metadata.Version).md") `
        -Destination $releaseNotesDirectory -Force

    $modulePlaceholder = Join-Path $publishDirectory "Modules"
    if (Test-Path -LiteralPath $modulePlaceholder -PathType Container) {
        $moduleEntries = @(Get-ChildItem -LiteralPath $modulePlaceholder -Force -Recurse |
            Where-Object { $_.PSIsContainer -or $_.Name -ne "README.md" })
        if ($moduleEntries.Count -gt 0) {
            throw "Portable payload contains bundled module content: $($moduleEntries.FullName -join ', ')"
        }
        Remove-Item -LiteralPath $modulePlaceholder -Recurse -Force
    }

    $publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File)
    $forbiddenFiles = @($publishedFiles | Where-Object {
        $_.Name -eq "stop-qingtoolbox.bat" -or
        $_.Name -match '^settings(\.corrupt-[^.]*)?\.json$' -or
        $_.Name -like 'QingToolbox.Modules.*' -or
        $_.Extension -in @('.pdb', '.cs', '.csproj', '.sln')
    })
    if ($forbiddenFiles.Count -gt 0) {
        throw "Portable payload contains forbidden files: $($forbiddenFiles.FullName -join ', ')"
    }
    $forbiddenDirectories = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -Directory |
        Where-Object { $_.Name -in @('Modules', 'modules', 'tests', 'bin', 'obj', '.git') })
    if ($forbiddenDirectories.Count -gt 0) {
        throw "Portable payload contains forbidden directories: $($forbiddenDirectories.FullName -join ', ')"
    }
    & (Join-Path $PSScriptRoot "write-host-payload-manifest.ps1") -PayloadDirectory $publishDirectory
    $requiredFiles = @(
        'QingToolbox.Shell.exe',
        'QingToolbox.ModuleHost.exe',
        'QingToolbox.StartupMaintenance.exe',
        'Resources\Localization\en-US.json',
        'Resources\Localization\zh-CN.json',
        'LICENSE',
        'CHANGELOG.md',
        'docs\QMOD_FORMAT.md',
        "docs\releases\$($metadata.Version).md",
        'docs\sdk\README.md',
        'host-payload.manifest.json'
    )
    foreach ($relativePath in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $relativePath) -PathType Leaf)) {
            throw "Portable payload is missing required file: $relativePath"
        }
    }
    Add-Type -AssemblyName System.Drawing
    $publishedIcon = [System.Drawing.Icon]::ExtractAssociatedIcon(
        (Join-Path $publishDirectory 'QingToolbox.Shell.exe'))
    if ($null -eq $publishedIcon) {
        throw "Portable Shell does not expose the official embedded application icon."
    }
    $publishedIcon.Dispose()
    Write-Host "Host-only portable payload audit passed; no bundled modules."

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }
    if (Test-Path -LiteralPath $checksumPath) {
        Remove-Item -LiteralPath $checksumPath -Force
    }

    Compress-Archive -Path (Join-Path $publishDirectory "*") `
        -DestinationPath $archivePath `
        -CompressionLevel Optimal

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $archiveNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('/', '\') })
        $duplicateEntries = @($archiveNames |
            Group-Object { $_.ToUpperInvariant() } |
            Where-Object Count -GT 1)
        if ($duplicateEntries.Count -gt 0) {
            throw "Portable ZIP contains duplicate conflicting paths: " +
                ($duplicateEntries.Name -join ', ')
        }
        $unsafeEntries = @($archiveNames | Where-Object {
            [System.IO.Path]::IsPathRooted($_) -or
            $_ -match '^[A-Za-z]:' -or
            $_ -match '^\\\\' -or
            $_ -match '(^|\\)\.\.(\\|$)' -or
            $_ -match '(^|\\)(Modules|modules|tests|bin|obj|\.git)(\\|$)' -or
            $_ -match '(^|\\)(stop-qingtoolbox\.bat|settings\.json|settings\.corrupt-[^\\]*\.json)$' -or
            $_ -match '\.(pdb|cs|csproj|sln)$' -or
            $_ -match '(^|\\)(QingToolbox\.Modules\.[^\\]*|TextTools|ScreenPin|WindowTopmost|PowerGuard)(\.|\\|$)'
        })
        if ($unsafeEntries.Count -gt 0) {
            throw "Portable ZIP contains unsafe or forbidden entries: $($unsafeEntries -join ', ')"
        }
        foreach ($requiredEntry in $requiredFiles) {
            if ($archiveNames -notcontains $requiredEntry) {
                throw "Portable ZIP is missing required entry: $requiredEntry"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
    Write-Host "Portable ZIP content audit passed."
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
