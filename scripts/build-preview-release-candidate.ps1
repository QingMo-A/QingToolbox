[CmdletBinding()]
param([string]$IsccPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot "get-preview-release-metadata.ps1")
. (Join-Path $PSScriptRoot "assert-preview-source.ps1")
. (Join-Path $PSScriptRoot "preview-stage-runner.ps1")

function Resolve-CandidateIscc {
    param([string]$ExplicitPath)

    $candidates = if ([string]::IsNullOrWhiteSpace($ExplicitPath)) {
        @(
            "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
            "C:\Program Files\Inno Setup 6\ISCC.exe",
            (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
        )
    }
    else { @($ExplicitPath) }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }
    throw "ISCC.exe is required for the Preview candidate gate. Install " +
          "Inno Setup 6 or pass -IsccPath '<path-to-ISCC.exe>'."
}

$tempRoot = Join-Path $env:TEMP (
    "QingToolbox-preview-candidate-" + [guid]::NewGuid().ToString("N"))
$originalLocalAppData = $env:LOCALAPPDATA
$originalAppData = $env:APPDATA

function Assert-CandidateArtifactPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $artifactsPrefix = $artifactsRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith(
        $artifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Candidate asset path is outside artifacts: $resolvedPath"
    }
    return $resolvedPath
}

function Assert-FileExists {
    param(
        [Parameter(Mandatory = $true)][string]$StageName,
        [Parameter(Mandatory = $true)][string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Preview stage '$StageName' did not create expected file: $Path"
    }
}

try {
    $initialSource = Assert-PreviewSource `
        -RequireToolboxBranch -RequireOriginSync
    $metadata = Get-PreviewReleaseMetadata
    $resolvedIscc = Resolve-CandidateIscc -ExplicitPath $IsccPath
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    $portablePath = Assert-CandidateArtifactPath (Join-Path $repoRoot (
        "artifacts\$($metadata.PortableFileName)"))
    $manifestPath = Assert-CandidateArtifactPath (Join-Path $repoRoot (
        "artifacts\$($metadata.ManifestFileName)"))
    $installerPath = Assert-CandidateArtifactPath (Join-Path $repoRoot (
        "artifacts\installer\output\$($metadata.InstallerFileName)"))
    foreach ($staleAsset in @(
        $portablePath, "$portablePath.sha256", $manifestPath,
        $installerPath, "$installerPath.sha256")) {
        $verifiedStaleAsset = Assert-CandidateArtifactPath $staleAsset
        if (Test-Path -LiteralPath $verifiedStaleAsset -PathType Leaf) {
            Remove-Item -LiteralPath $verifiedStaleAsset -Force
        }
    }

    Invoke-CheckedStage -StageName "Preview stage runner contracts" -Action {
        & (Join-Path $PSScriptRoot "test-preview-stage-runner.ps1")
    }

    Write-Host "`n==> Build Release"
    Invoke-CheckedStage -StageName "Build Release" -Action {
        dotnet build -c Release
    }

    Write-Host "`n==> Deploy development module"
    Invoke-CheckedStage -StageName "Deploy development module" -Action {
        & (Join-Path $PSScriptRoot "deploy-dev-modules.ps1") `
            -Configuration Release
    }

    Write-Host "`n==> Run module load smoke test"
    Invoke-CheckedStage -StageName "Run module load smoke test" -Action {
        dotnet run `
            --project QingToolbox.DevTools.ModuleLoadSmokeTest `
            -c Release --no-build
    }

    Write-Host "`n==> Build portable Preview archive"
    Invoke-CheckedStage -StageName "Build portable Preview archive" -Action {
        & (Join-Path $PSScriptRoot "publish-preview.ps1")
    }
    Assert-FileExists "Build portable Preview archive" $portablePath
    Assert-FileExists "Build portable Preview archive" "$portablePath.sha256"

    Write-Host "`n==> Prepare isolated Inno Setup"
    $isolatedInnoRoot = Join-Path $tempRoot "InnoSetup"
    New-Item -ItemType Directory -Path $isolatedInnoRoot -Force | Out-Null
    Copy-Item -Path (Join-Path (Split-Path -Parent $resolvedIscc) "*") `
        -Destination $isolatedInnoRoot -Recurse
    $isolatedChinese = Join-Path $isolatedInnoRoot `
        "Languages\ChineseSimplified.isl"
    if (Test-Path -LiteralPath $isolatedChinese) {
        Remove-Item -LiteralPath $isolatedChinese -Force
    }
    $preparedOutput = @(Invoke-CheckedStageWithOutput `
        -StageName "Prepare isolated Inno Setup" -Action {
            & (Join-Path $PSScriptRoot "prepare-inno-setup.ps1") `
                -IsccPath (Join-Path $isolatedInnoRoot "ISCC.exe")
        })
    $preparedPaths = @($preparedOutput | Where-Object {
        $_ -is [string] -and -not [string]::IsNullOrWhiteSpace($_)
    })
    if ($preparedPaths.Count -ne 1) {
        throw "Preview stage 'Prepare isolated Inno Setup' returned " +
              "$($preparedPaths.Count) candidate paths; expected exactly one."
    }
    $preparedIscc = [System.IO.Path]::GetFullPath($preparedPaths[0])
    Assert-FileExists "Prepare isolated Inno Setup" $preparedIscc

    Write-Host "`n==> Build Preview installer"
    Invoke-CheckedStage -StageName "Build Preview installer" -Action {
        & (Join-Path $PSScriptRoot "build-installer.ps1") `
            -IsccPath $preparedIscc -SkipPreflight
    }
    Assert-FileExists "Build Preview installer" $installerPath
    Assert-FileExists "Build Preview installer" "$installerPath.sha256"

    Write-Host "`n==> Test isolated installer roundtrip"
    $profileRoot = Join-Path $tempRoot "Profile"
    $env:LOCALAPPDATA = Join-Path $profileRoot "LocalAppData"
    $env:APPDATA = Join-Path $profileRoot "AppData"
    $roundtripRoot = Join-Path $tempRoot "Roundtrip"
    Invoke-CheckedStage -StageName "Test isolated installer roundtrip" -Action {
        & (Join-Path $PSScriptRoot "test-installer-roundtrip.ps1") `
            -InstallerPath $installerPath -TestRoot $roundtripRoot
    }

    $env:LOCALAPPDATA = $originalLocalAppData
    $env:APPDATA = $originalAppData

    Write-Host "`n==> Generate and verify Preview manifest"
    Invoke-CheckedStage -StageName "Write Preview manifest" -Action {
        & (Join-Path $PSScriptRoot "write-preview-manifest.ps1")
    }
    Assert-FileExists "Write Preview manifest" $manifestPath
    Invoke-CheckedStage -StageName "Verify Preview assets" -Action {
        & (Join-Path $PSScriptRoot "verify-preview-assets.ps1")
    }

    $finalSource = Assert-PreviewSource `
        -RequireToolboxBranch -RequireOriginSync
    if ($finalSource.Commit -ne $initialSource.Commit) {
        throw "HEAD changed during the candidate build."
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.sourceCommit -ne $finalSource.Commit) {
        throw "Verified manifest sourceCommit does not match current HEAD."
    }
    $portable = Get-Item -LiteralPath $portablePath
    $installer = Get-Item -LiteralPath $installerPath

    Write-Host "`nPreview release candidate gate passed."
    Write-Host "Product:           $($metadata.ProductName)"
    Write-Host "Version:           $($metadata.Version)"
    Write-Host "File version:      $($metadata.FileVersion)"
    Write-Host "Runtime:           $($metadata.Runtime)"
    Write-Host "Source commit:     $($finalSource.Commit)"
    Write-Host "Branch:            $($finalSource.Branch)"
    Write-Host "Source clean:      $($finalSource.IsClean)"
    Write-Host "Origin synchronized: $($finalSource.IsOriginSynced)"
    Write-Host "Portable:          $($portable.FullName)"
    Write-Host "Portable size:     $($portable.Length) bytes"
    Write-Host "Portable SHA256:   $((Get-FileHash $portable.FullName -Algorithm SHA256).Hash)"
    Write-Host "Installer:         $($installer.FullName)"
    Write-Host "Installer size:    $($installer.Length) bytes"
    Write-Host "Installer SHA256:  $((Get-FileHash $installer.FullName -Algorithm SHA256).Hash)"
    Write-Host "Manifest:          $manifestPath"
    Write-Host "Roundtrip:         passed"
}
catch {
    Write-Error -ErrorRecord $_
    exit 1
}
finally {
    $env:LOCALAPPDATA = $originalLocalAppData
    $env:APPDATA = $originalAppData
    try {
        if (Test-Path -LiteralPath $tempRoot) {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force
        }
    }
    catch {
        Write-Warning "Preview candidate temporary cleanup failed: $($_.Exception.Message)"
    }
}
