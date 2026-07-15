[CmdletBinding()]
param([string]$IsccPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot "get-preview-release-metadata.ps1")
. (Join-Path $PSScriptRoot "assert-preview-source.ps1")

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Command,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage Exit code: $LASTEXITCODE."
    }
}

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

try {
    $initialSource = Assert-PreviewSource `
        -RequireToolboxBranch -RequireOriginSync
    $metadata = Get-PreviewReleaseMetadata
    $resolvedIscc = Resolve-CandidateIscc -ExplicitPath $IsccPath
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    Write-Host "`n==> Build Release"
    Invoke-Checked -FailureMessage "Release build failed." -Command {
        dotnet build -c Release
    }

    Write-Host "`n==> Deploy development module"
    & (Join-Path $PSScriptRoot "deploy-dev-modules.ps1") `
        -Configuration Release

    Write-Host "`n==> Run module load smoke test"
    Invoke-Checked -FailureMessage "Module load smoke test failed." -Command {
        dotnet run `
            --project QingToolbox.DevTools.ModuleLoadSmokeTest `
            -c Release --no-build
    }

    Write-Host "`n==> Build portable Preview archive"
    & (Join-Path $PSScriptRoot "publish-preview.ps1")

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
    $preparedIscc = & (Join-Path $PSScriptRoot "prepare-inno-setup.ps1") `
        -IsccPath (Join-Path $isolatedInnoRoot "ISCC.exe")

    Write-Host "`n==> Build Preview installer"
    & (Join-Path $PSScriptRoot "build-installer.ps1") `
        -IsccPath $preparedIscc -SkipPreflight

    Write-Host "`n==> Test isolated installer roundtrip"
    $profileRoot = Join-Path $tempRoot "Profile"
    $env:LOCALAPPDATA = Join-Path $profileRoot "LocalAppData"
    $env:APPDATA = Join-Path $profileRoot "AppData"
    $roundtripRoot = Join-Path $tempRoot "Roundtrip"
    $installerPath = Join-Path $repoRoot (
        "artifacts\installer\output\$($metadata.InstallerFileName)")
    & (Join-Path $PSScriptRoot "test-installer-roundtrip.ps1") `
        -InstallerPath $installerPath -TestRoot $roundtripRoot

    $env:LOCALAPPDATA = $originalLocalAppData
    $env:APPDATA = $originalAppData

    Write-Host "`n==> Generate and verify Preview manifest"
    & (Join-Path $PSScriptRoot "write-preview-manifest.ps1")
    & (Join-Path $PSScriptRoot "verify-preview-assets.ps1")

    $finalSource = Assert-PreviewSource `
        -RequireToolboxBranch -RequireOriginSync
    if ($finalSource.Commit -ne $initialSource.Commit) {
        throw "HEAD changed during the candidate build."
    }

    $portablePath = Join-Path $repoRoot (
        "artifacts\$($metadata.PortableFileName)")
    $manifestPath = Join-Path $repoRoot (
        "artifacts\$($metadata.ManifestFileName)")
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
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
