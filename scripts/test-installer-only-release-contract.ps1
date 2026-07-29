[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot 'get-preview-release-metadata.ps1')

$metadata = Get-PreviewReleaseMetadata
$expectedInstaller = "QingToolbox-$($metadata.Version)-win-x64-setup.exe"
if ($metadata.InstallerFileName -ne $expectedInstaller) {
    throw "Installer release filename mismatch: $($metadata.InstallerFileName)"
}
if ($metadata.PSObject.Properties.Name -match '^Portable') {
    throw 'Release metadata still exposes a portable product asset.'
}

$workflowPath = Join-Path $repoRoot '.github\workflows\preview-release-validation.yml'
$workflow = Get-Content -LiteralPath $workflowPath -Raw
if ($workflow -match '(?i)portable_file|publish-preview|\.zip(?:\.sha256)?') {
    throw 'Preview validation still generates or uploads a portable product asset.'
}
if ($workflow -notmatch [regex]::Escape('artifacts/installer/output/${{ steps.release.outputs.installer_file }}') -or
    $workflow -notmatch [regex]::Escape('artifacts/installer/output/${{ steps.release.outputs.installer_file }}.sha256')) {
    throw 'Preview validation does not upload the installer and its same-name SHA256 sidecar.'
}

$installerScript = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'build-installer.ps1') -Raw
foreach ($requiredContract in @(
    'dotnet', 'publish', 'write-host-payload-manifest.ps1',
    'verify-host-web-asset-binding.ps1', 'host-payload.manifest.json')) {
    if ($installerScript -notmatch [regex]::Escape($requiredContract)) {
        throw "Installer payload capability is missing: $requiredContract"
    }
}

$manifestWriter = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'write-preview-manifest.ps1') -Raw
$manifestVerifier = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'verify-preview-assets.ps1') -Raw
if ($manifestWriter -match '(?i)portable' -or $manifestVerifier -match '(?i)portable') {
    throw 'Release manifest scripts still contain a portable product contract.'
}
if ($manifestWriter -notmatch 'distribution\s*=\s*"installer-only"' -or
    $manifestWriter -notmatch 'Get-VerifiedAssetRecord -Type "installer"' -or
    $manifestVerifier -notmatch 'must contain exactly one installer artifact') {
    throw 'Release manifest scripts do not enforce the installer-only asset set.'
}

Write-Host 'Installer-only release asset contract passed.'
