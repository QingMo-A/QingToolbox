[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot 'get-preview-release-metadata.ps1')

$metadata = Get-PreviewReleaseMetadata
$expectedVersion = '0.2.1-alpha'
if ($metadata.Version -ne $expectedVersion -or $metadata.FileVersion -ne '0.2.1.0') {
    throw "Unexpected 0.2.1 candidate metadata: $($metadata.Version) / $($metadata.FileVersion)"
}
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
if ($workflow -notmatch [regex]::Escape('-Tag v0.1.0-alpha') -or
    $workflow -match [regex]::Escape('-Tag v0.2.0-alpha')) {
    throw 'Preview validation must use the published v0.1.0-alpha upgrade baseline.'
}

$resolver = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'resolve-previous-preview-installer.ps1') -Raw
$candidateGate = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'build-preview-release-candidate.ps1') -Raw
if ($resolver -notmatch '\[string\]\$Tag\s*=\s*"v0\.1\.0-alpha"' -or
    $candidateGate -notmatch [regex]::Escape('-Tag "v0.1.0-alpha"') -or
    $candidateGate -match [regex]::Escape('-Tag "v0.2.0-alpha"')) {
    throw 'Release candidate scripts do not consistently use v0.1.0-alpha.'
}

$installerScript = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'build-installer.ps1') -Raw
foreach ($requiredContract in @(
    'dotnet', 'publish', 'write-host-payload-manifest.ps1',
    'verify-host-web-asset-binding.ps1', 'host-payload.manifest.json')) {
    if ($installerScript -notmatch [regex]::Escape($requiredContract)) {
        throw "Installer payload capability is missing: $requiredContract"
    }
}
foreach ($hostOnlyGuard in @(
    'Installer payload contains concrete module files',
    '$modulePlaceholder',
    'Installer payload contains bundled module content')) {
    if ($installerScript -notmatch [regex]::Escape($hostOnlyGuard)) {
        throw "Installer host-only module guard is missing: $hostOnlyGuard"
    }
}

$releaseNotes = Get-Content -LiteralPath (Join-Path $repoRoot 'docs\releases\0.2.1-alpha.md') -Raw -Encoding UTF8
foreach ($deliveryStatement in @(
    'not bundled with the host installer or host Release')) {
    if ($releaseNotes -notmatch [regex]::Escape($deliveryStatement)) {
        throw "Release Notes do not state the independent module delivery contract: $deliveryStatement"
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
