[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot 'get-preview-release-metadata.ps1')

$metadata = Get-PreviewReleaseMetadata
$sourceAssertion = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'assert-preview-source.ps1') -Raw
if ($sourceAssertion -notmatch [regex]::Escape("`$branchOutput = @(Invoke-SourceGit") -or
    $sourceAssertion -notmatch [regex]::Escape("(`$branchOutput -join '')")) {
    throw 'Preview source validation must safely accept an allowed detached tag checkout.'
}
$expectedVersion = '0.2.6-alpha'
if ($metadata.Version -ne $expectedVersion -or $metadata.FileVersion -ne '0.2.6.0') {
    throw "Unexpected 0.2.6 candidate metadata: $($metadata.Version) / $($metadata.FileVersion)"
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
if ($workflow -notmatch [regex]::Escape('-Tag v0.2.5-alpha') -or
    $workflow -match [regex]::Escape('-Tag v0.2.4-alpha')) {
    throw 'Preview validation must use the published v0.2.5-alpha upgrade baseline.'
}
foreach ($remoteReleaseGuard in @(
    "github.event_name == 'workflow_dispatch' && inputs.publish_release",
    'GITHUB_REF_TYPE',
    'GITHUB_REF_NAME',
    '--verify-tag',
    '--prerelease',
    'sha256sum --check')) {
    if ($workflow -notmatch [regex]::Escape($remoteReleaseGuard)) {
        throw "Remote release publication guard is missing: $remoteReleaseGuard"
    }
}
foreach ($validatedCandidateGuard in @(
    'publish-validated-candidate',
    'inputs.candidate_run_id',
    'run.head_sha',
    'tagCommit',
    'actions: read')) {
    if ($workflow -notmatch [regex]::Escape($validatedCandidateGuard)) {
        throw "Validated-candidate publication guard is missing: $validatedCandidateGuard"
    }
}

$resolver = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'resolve-previous-preview-installer.ps1') -Raw
$candidateGate = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'build-preview-release-candidate.ps1') -Raw
$upgradeTest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'test-preview-upgrade.ps1') -Raw
if ($resolver -notmatch '\[string\]\$Tag\s*=\s*"v0\.2\.5-alpha"' -or
    $candidateGate -notmatch [regex]::Escape('-Tag "v0.2.5-alpha"') -or
    $candidateGate -match [regex]::Escape('-Tag "v0.2.4-alpha"')) {
    throw 'Release candidate scripts do not consistently use the published v0.2.5-alpha baseline.'
}
if ($upgradeTest -notmatch [regex]::Escape('[Diagnostics.FileVersionInfo]::GetVersionInfo($previous)') -or
    $upgradeTest -notmatch [regex]::Escape("Join-Path `$install 'host-payload.manifest.json'") -or
    $upgradeTest -match "previousFileVersion\s*=\s*'\d") {
    throw 'The upgrade test must derive the published baseline identity and payload manifest from the verified previous installer.'
}
foreach ($upgradeSynchronizationGuard in @(
    'Wait-ForShellWindowReady',
    'MainWindowHandle',
    'Responding',
    'Invoke-WebShellReadyProbe',
    'workspaceActivated',
    'failureCode',
    'Assert-WebAssetTree',
    'staleAssetMustBeRemoved',
    '$process.Kill($true)',
    'stale WebUI',
    'StartsWith($testRootPrefix')) {
    if ($upgradeTest -notmatch [regex]::Escape($upgradeSynchronizationGuard)) {
        throw "Upgrade test synchronization guard is missing: $upgradeSynchronizationGuard"
    }
}

$installerScript = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'build-installer.ps1') -Raw
$innoScript = Get-Content -LiteralPath (Join-Path $repoRoot 'installer\QingToolbox.iss') -Raw
foreach ($closeContract in @(
    'CloseApplications=force',
    'CloseApplicationsFilter=QingToolbox.Shell.exe,QingToolbox.ModuleHost.exe,QingToolbox.StartupMaintenance.exe')) {
    if ($innoScript -notmatch [regex]::Escape($closeContract)) {
        throw "Installer close-applications fallback is missing: $closeContract"
    }
}
if ($installerScript -notmatch [regex]::Escape('installer\baselines\0.2.5-alpha-host-payload.json') -or
    $installerScript -match [regex]::Escape('installer\baselines\0.2.4-alpha-obsolete-host-payload.json')) {
    throw 'The installer must clean obsolete host files against the published v0.2.5-alpha payload baseline.'
}
if ($innoScript -notmatch '(?m)Type:\s*filesandordirs;\s*Name:\s*"\{app\}\\WebUI\\assets"') {
    throw 'Installer must clear the host-owned WebUI/assets tree before copying a new payload.'
}
if ($innoScript -match '(?m)Name:\s*"\{app\}\\WebUI\\\*"') {
    throw 'Installer WebUI cleanup must not use a broad WebUI wildcard.'
}
$previousCleanupBaseline = Get-Content -LiteralPath (
    Join-Path $repoRoot 'installer\baselines\0.2.5-alpha-host-payload.json') -Raw
foreach ($obsoletePath in @(
    'docs/releases/0.2.5-alpha.md',
    'WebUI/assets/index-CFJmK4AQ.js',
    'WebUI/assets/index-CmLfpQE4.css')) {
    if ($previousCleanupBaseline -notmatch [regex]::Escape($obsoletePath)) {
        throw "Published v0.2.5-alpha cleanup baseline is missing: $obsoletePath"
    }
}
foreach ($requiredContract in @(
    'dotnet', 'publish', 'write-host-payload-manifest.ps1',
    'verify-host-web-asset-binding.ps1', 'host-payload.manifest.json')) {
    if ($installerScript -notmatch [regex]::Escape($requiredContract)) {
        throw "Installer payload capability is missing: $requiredContract"
    }
}
$hostWebBinding = Get-Content -LiteralPath (
    Join-Path $repoRoot 'scripts\verify-host-web-asset-binding.ps1') -Raw
foreach ($byteBindingContract in @(
    'Test-ByteSequence',
    '[Text.Encoding]::UTF8.GetBytes($Value)',
    '[Text.Encoding]::Unicode.GetBytes($Value)')) {
    if ($hostWebBinding -notmatch [regex]::Escape($byteBindingContract)) {
        throw "Host/WebUI binding must search exact encoded bytes: $byteBindingContract"
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

$releaseNotes = Get-Content -LiteralPath (Join-Path $repoRoot 'docs\releases\0.2.6-alpha.md') -Raw -Encoding UTF8
$chineseIndependentDelivery = [Text.Encoding]::UTF8.GetString(
    [Convert]::FromBase64String('5LiN6ZqP5a6/5Li75a6J6KOF5Zmo5oiW5a6/5Li7IFJlbGVhc2Ug5o2G57uR'))
$chineseUnpublished = [Text.Encoding]::UTF8.GetString(
    [Convert]::FromBase64String('5bCa5pyq5Y+R5biD'))
foreach ($deliveryStatement in @(
    'not bundled with the host installer or host Release',
    $chineseIndependentDelivery)) {
    if ($releaseNotes -notmatch [regex]::Escape($deliveryStatement)) {
        throw "Release Notes do not state the independent module delivery contract: $deliveryStatement"
    }
}
foreach ($candidateOnlyText in @('Release Candidate', $chineseUnpublished)) {
    if ($releaseNotes -match [regex]::Escape($candidateOnlyText)) {
        throw "Final Release Notes still contain candidate-only text: $candidateOnlyText"
    }
}
if ($releaseNotes -notmatch [regex]::Escape('v0.2.5-alpha')) {
    throw 'Final Release Notes do not identify the published upgrade baseline.'
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
