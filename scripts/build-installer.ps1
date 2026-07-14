[CmdletBinding()]
param(
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64",
    [ValidateSet("Release")]
    [string]$Configuration = "Release",
    [string]$IsccPath,
    [bool]$SelfContained = $true
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$installerRoot = Join-Path $repoRoot "artifacts\installer"
$payloadDirectory = Join-Path $installerRoot "payload"
$outputDirectory = Join-Path $installerRoot "output"
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$shellProject = Join-Path $repoRoot "QingToolbox.Shell\QingToolbox.Shell.csproj"
$smokeProject = Join-Path $repoRoot "QingToolbox.DevTools.ModuleLoadSmokeTest\QingToolbox.DevTools.ModuleLoadSmokeTest.csproj"
$installerScript = Join-Path $repoRoot "installer\QingToolbox.iss"

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
            "C:\Program Files\Inno Setup 6\ISCC.exe"
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
    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $version = @($props.Project.PropertyGroup.Version)[0]
    if ($version -ne "0.1.0-alpha") {
        throw "Expected version 0.1.0-alpha in Directory.Build.props, found '$version'."
    }

    foreach ($path in @($payloadDirectory, $outputDirectory)) {
        Assert-InstallerArtifactPath -Path $path
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }

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

    $selfContainedValue = $SelfContained.ToString().ToLowerInvariant()
    Invoke-DotNet -Arguments @(
        "publish", $shellProject,
        "-c", $Configuration,
        "-r", $Runtime,
        "--self-contained", $selfContainedValue,
        "-o", $payloadDirectory
    ) -FailureMessage "Shell publish failed."

    Get-ChildItem -LiteralPath $payloadDirectory -Recurse -Filter "*.pdb" -File |
        Remove-Item -Force
    $bundledModuleDlls = Get-ChildItem -LiteralPath $payloadDirectory `
        -Recurse -Filter "*.dll" -File |
        Where-Object FullName -Match "[\\/]Modules[\\/]"
    if ($bundledModuleDlls) {
        throw "Installer payload unexpectedly contains module DLLs."
    }

    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") `
        -Destination $payloadDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot "CHANGELOG.md") `
        -Destination $payloadDirectory -Force
    $docsDirectory = Join-Path $payloadDirectory "docs"
    New-Item -ItemType Directory -Force -Path $docsDirectory | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot "docs\QMOD_FORMAT.md") `
        -Destination $docsDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot "docs\releases\0.1.0-alpha.md") `
        -Destination $docsDirectory -Force

    $resolvedIsccPath = Resolve-IsccPath -ExplicitPath $IsccPath
    $innoRoot = Split-Path -Parent $resolvedIsccPath
    $englishMessages = Join-Path $innoRoot "Default.isl"
    $chineseMessages = Join-Path $innoRoot "Languages\ChineseSimplified.isl"
    foreach ($messagesFile in @($englishMessages, $chineseMessages)) {
        if (-not (Test-Path -LiteralPath $messagesFile -PathType Leaf)) {
            throw "Required Inno Setup language file was not found: $messagesFile"
        }
    }

    $outputBaseFilename = "QingToolbox-$version-$Runtime-setup"
    & $resolvedIsccPath `
        "/DAppVersion=$version" `
        "/DSourceDir=$payloadDirectory" `
        "/DOutputDir=$outputDirectory" `
        "/DOutputBaseFilename=$outputBaseFilename" `
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
