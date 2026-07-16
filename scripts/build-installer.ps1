[CmdletBinding()]
param(
    [string]$Runtime,
    [ValidateSet("Release")]
    [string]$Configuration = "Release",
    [string]$IsccPath,
    [switch]$SkipPreflight
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
$installerRoot = Join-Path $repoRoot "artifacts\installer"
$payloadDirectory = Join-Path $installerRoot "payload"
$outputDirectory = Join-Path $installerRoot "output"
$shellProject = Join-Path $repoRoot "QingToolbox.Shell\QingToolbox.Shell.csproj"
$smokeProject = Join-Path $repoRoot "QingToolbox.DevTools.ModuleLoadSmokeTest\QingToolbox.DevTools.ModuleLoadSmokeTest.csproj"
$installerScript = Join-Path $repoRoot "installer\QingToolbox.iss"
$brandIconPath = Join-Path $repoRoot `
    "QingToolbox.Shell\Assets\Branding\QingToolbox.ico"
$brandMarkPath = Join-Path $repoRoot `
    "QingToolbox.Shell\Assets\Branding\QingToolbox.Mark.svg"

function Assert-InstallerArtifactPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $installerPrefix = $installerRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith(
        $installerPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside artifacts/installer: $resolvedPath"
    }
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage Exit code: $LASTEXITCODE."
    }
}

function Resolve-IsccPath {
    param([string]$ExplicitPath)

    $candidates = if ([string]::IsNullOrWhiteSpace($ExplicitPath)) {
        @(
            "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
            "C:\Program Files\Inno Setup 6\ISCC.exe",
            (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
        )
    }
    else {
        @($ExplicitPath)
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    throw "ISCC.exe was not found. Install Inno Setup 6 or pass " +
          "-IsccPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'."
}

try {
    foreach ($brandAsset in @($brandIconPath, $brandMarkPath)) {
        if (-not (Test-Path -LiteralPath $brandAsset -PathType Leaf)) {
            throw "QingToolbox brand asset is missing: $brandAsset"
        }
    }
    foreach ($path in @($payloadDirectory, $outputDirectory)) {
        Assert-InstallerArtifactPath -Path $path
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }

    if ($SkipPreflight) {
        Write-Host "Skipping Release build, module deployment, and smoke test."
    }
    else {
        Invoke-DotNet -Arguments @("build", "-c", $Configuration) `
            -FailureMessage "Release build failed."

        & (Join-Path $PSScriptRoot "deploy-dev-modules.ps1") `
            -Configuration $Configuration
        if (-not $?) {
            throw "Development module deployment failed."
        }

        Invoke-DotNet -Arguments @(
            "run",
            "--project", $smokeProject,
            "-c", $Configuration,
            "--no-build"
        ) -FailureMessage "Module smoke test failed."
    }

    Invoke-DotNet -Arguments @(
        "publish", $shellProject,
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", "true",
        "-p:DebugSymbols=false",
        "-p:DebugType=None",
        "-o", $payloadDirectory
    ) -FailureMessage "Shell publish failed."

    $shellExecutable = Join-Path $payloadDirectory "QingToolbox.Shell.exe"
    if (-not (Test-Path -LiteralPath $shellExecutable -PathType Leaf)) {
        throw "Installer payload is missing QingToolbox.Shell.exe."
    }

    Add-Type -AssemblyName System.Drawing
    $embeddedIcon = [System.Drawing.Icon]::ExtractAssociatedIcon(
        $shellExecutable)
    if ($null -eq $embeddedIcon) {
        throw "QingToolbox.Shell.exe does not expose an embedded application icon."
    }
    $embeddedIcon.Dispose()

    $payloadFiles = @(Get-ChildItem -LiteralPath $payloadDirectory -Recurse -File)
    $pdbFiles = @($payloadFiles | Where-Object Extension -EQ ".pdb")
    if ($pdbFiles.Count -gt 0) {
        throw "Installer payload contains PDB files: $($pdbFiles.FullName -join ', ')"
    }

    $looseBrandImages = @($payloadFiles | Where-Object {
        $_.Extension -in @(".ico", ".png")
    })
    if ($looseBrandImages.Count -gt 0) {
        throw "Installer payload contains loose icon exports: " +
              ($looseBrandImages.FullName -join ', ')
    }

    $forbiddenModuleFiles = @($payloadFiles | Where-Object {
        $_.Name -match '^(TextTools|ScreenPin|WindowTopmost|PowerGuard)(\.|$)' -or
        $_.Name -like 'QingToolbox.Modules.*'
    })
    if ($forbiddenModuleFiles.Count -gt 0) {
        throw "Installer payload contains concrete module files: $($forbiddenModuleFiles.FullName -join ', ')"
    }

    $sourceFiles = @($payloadFiles | Where-Object Extension -In @(".cs", ".csproj", ".sln"))
    if ($sourceFiles.Count -gt 0) {
        throw "Installer payload contains source or project files: $($sourceFiles.FullName -join ', ')"
    }

    $modulePlaceholder = Join-Path $payloadDirectory "Modules"
    if (Test-Path -LiteralPath $modulePlaceholder -PathType Container) {
        $moduleEntries = @(Get-ChildItem -LiteralPath $modulePlaceholder -Force -Recurse |
            Where-Object { $_.PSIsContainer -or $_.Name -ne "README.md" })
        if ($moduleEntries.Count -gt 0) {
            throw "Installer payload contains bundled module content: $($moduleEntries.FullName -join ', ')"
        }
        Remove-Item -LiteralPath $modulePlaceholder -Recurse -Force
    }

    $forbiddenSupportFiles = @($payloadFiles | Where-Object {
        $_.Name -eq "stop-qingtoolbox.bat" -or
        $_.Name -match '^settings(\.corrupt-[^.]*)?\.json$'
    })
    if ($forbiddenSupportFiles.Count -gt 0) {
        throw "Installer payload contains forbidden support or user files: " +
              ($forbiddenSupportFiles.FullName -join ', ')
    }

    $buildDirectories = @(Get-ChildItem -LiteralPath $payloadDirectory -Recurse -Directory |
        Where-Object Name -In @("Modules", "modules", "tests", "bin", "obj", ".git"))
    if ($buildDirectories.Count -gt 0) {
        throw "Installer payload contains forbidden directories: $($buildDirectories.FullName -join ', ')"
    }

    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") `
        -Destination $payloadDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot "CHANGELOG.md") `
        -Destination $payloadDirectory -Force
    $docsDirectory = Join-Path $payloadDirectory "docs"
    $releaseNotesDirectory = Join-Path $docsDirectory "releases"
    $sdkDocsDirectory = Join-Path $docsDirectory "sdk"
    New-Item -ItemType Directory -Force -Path $releaseNotesDirectory | Out-Null
    New-Item -ItemType Directory -Force -Path $sdkDocsDirectory | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot "docs\QMOD_FORMAT.md") `
        -Destination $docsDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot "docs\sdk\README.md") `
        -Destination $sdkDocsDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot `
        "docs\releases\$($metadata.Version).md") `
        -Destination $releaseNotesDirectory -Force

    $requiredPayloadFiles = @(
        "LICENSE",
        "CHANGELOG.md",
        "docs\QMOD_FORMAT.md",
        "docs\releases\$($metadata.Version).md",
        "docs\sdk\README.md",
        "Resources\Localization\en-US.json",
        "Resources\Localization\zh-CN.json"
    )
    foreach ($relativePath in $requiredPayloadFiles) {
        $requiredPath = Join-Path $payloadDirectory $relativePath
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Installer payload is missing required file: $relativePath"
        }
    }

    $resolvedIsccPath = Resolve-IsccPath -ExplicitPath $IsccPath
    $innoRoot = Split-Path -Parent $resolvedIsccPath
    $englishMessages = Join-Path $innoRoot "Default.isl"
    $chineseMessages = Join-Path $innoRoot "Languages\ChineseSimplified.isl"
    foreach ($messagesFile in @($englishMessages, $chineseMessages)) {
        if (-not (Test-Path -LiteralPath $messagesFile -PathType Leaf)) {
            throw "Required Inno Setup language file was not found: $messagesFile"
        }
    }

    $outputBaseFilename = $metadata.InstallerBaseName
    & $resolvedIsccPath `
        "/DAppVersion=$($metadata.Version)" `
        "/DFileVersion=$($metadata.FileVersion)" `
        "/DSourceDir=$payloadDirectory" `
        "/DOutputDir=$outputDirectory" `
        "/DOutputBaseFilename=$outputBaseFilename" `
        "/DBrandIconPath=$brandIconPath" `
        $installerScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
    }

    $installerPath = Join-Path $outputDirectory "$outputBaseFilename.exe"
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw "Inno Setup did not create the expected installer: $installerPath"
    }

    $hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
    $checksumPath = "$installerPath.sha256"
    "$hash  $(Split-Path -Leaf $installerPath)" |
        Set-Content -LiteralPath $checksumPath -Encoding ASCII

    Write-Host "Installer: $installerPath"
    Write-Host "SHA256:   $hash"
    Write-Host "Checksum: $checksumPath"
}
catch {
    Write-Error $_
    exit 1
}
