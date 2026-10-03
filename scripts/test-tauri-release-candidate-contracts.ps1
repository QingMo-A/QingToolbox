[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$candidateScript = Join-Path $PSScriptRoot 'build-tauri-release-candidate.ps1'
if (-not (Test-Path -LiteralPath $candidateScript -PathType Leaf)) {
    throw "Tauri candidate gate is missing: $candidateScript"
}

$description = @(& $candidateScript -Describe)
if ($LASTEXITCODE -ne 0) {
    throw 'Tauri candidate gate description failed.'
}
$expectedStages = @(
    'Stage: Verify Tauri host and all official modules',
    'Stage: Package Tauri modules',
    'Stage: Verify local environment contracts',
    'Stage: Build Tauri production host',
    'Stage: Build and smoke Tauri installer',
    'Stage: Verify Tauri candidate assets',
    'Stage: Verify final source state'
)
if ($description.Count -ne $expectedStages.Count) {
    throw "Tauri candidate gate described $($description.Count) stages; expected $($expectedStages.Count)."
}
for ($index = 0; $index -lt $expectedStages.Count; $index++) {
    if ($description[$index] -cne $expectedStages[$index]) {
        throw "Tauri candidate stage $index is '$($description[$index])'; expected '$($expectedStages[$index])'."
    }
}

$content = [IO.File]::ReadAllText($candidateScript)
foreach ($contract in @(
    "branch -ne 'toolbox'",
    'GITHUB_BASE_REF',
    'Invoke-CandidateStage',
    'Invoke-CandidateScript',
    "'status', '--porcelain=v1', '--untracked-files=all'",
    "'verify-tauri.ps1'",
    "@('-BuildDesktop', '-SmokeDesktop', '-SmokeEverything')",
    "'package-tauri-modules.ps1'",
    "'test-local-environment-contracts.ps1'",
    "'build-tauri-production.ps1'",
    "@('-SkipModuleBuild', '-Smoke')",
    "'build-tauri-installer.ps1'",
    "@('-SkipBuild', '-Smoke')",
    'sourceDirty',
    'sourceCommit',
    '-tauri-setup.exe',
    '-win-x64-setup.exe',
    'Get-FileHash',
    'SHA256',
    'HEAD changed while the Tauri candidate gate was running.',
    'Signing:       unsigned alpha (publisher certificate not configured)',
    'QingToolbox product AppId (WPF-to-Tauri in-place upgrade)'
)) {
    if ($content.IndexOf($contract, [StringComparison]::Ordinal) -lt 0) {
        throw "Tauri candidate gate lost contract text: $contract"
    }
}
foreach ($forbidden in @('git push', 'git tag', 'gh release', 'build-installer.ps1')) {
    if ($content.IndexOf($forbidden, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Tauri candidate gate contains a forbidden publish/legacy operation: $forbidden"
    }
}

$installer = [IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) 'installer/QingToolbox.Tauri.iss'))
if ($installer.IndexOf('AppId={{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}', [StringComparison]::Ordinal) -lt 0 -or
    $installer.IndexOf('C9E5A4D1-1E8E-4F39-8F70-9D8D1C4B7A61', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'The public Tauri installer must use the QingToolbox product AppId, never the isolated test AppId.'
}
if ($installer.IndexOf('MigrateLegacyBundledModules();', [StringComparison]::Ordinal) -lt 0 -or
    $installer.IndexOf("Type: filesandordirs; Name: `"{app}\resources`"", [StringComparison]::Ordinal) -lt 0) {
    throw 'The installer must migrate old bundled modules before removing host-owned resources.'
}
foreach ($contract in @(
    'CloseApplications=no',
    'UpdateHandoffToken',
    '--qing-update-shutdown=',
    '{param:QINGHOSTPID|0}',
    '{param:QINGRELAUNCH|0}',
    'PrepareInstalledHostForOverwrite()'
)) {
    if ($installer.IndexOf($contract, [StringComparison]::Ordinal) -lt 0) {
        throw "The Tauri installer lost its graceful overwrite contract: $contract"
    }
}
if ($installer.IndexOf('CloseApplications=force', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'The Tauri installer must never force-close the Rust host during an overwrite.'
}
if ($installer.IndexOf('WbemScripting', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
    $installer.IndexOf('Win32_Process', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw 'The Tauri installer must use bounded Win32 process APIs instead of WMI.'
}
$hostUpdateSource = [IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) 'QingToolbox.Tauri/src-tauri/src/host_update.rs'))
$hostSource = [IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) 'QingToolbox.Tauri/src-tauri/src/lib.rs'))
foreach ($contract in @(
    '--qing-update-shutdown=',
    '/QINGHOSTPID=',
    '/QINGRELAUNCH=1',
    'publish_update_handoff_token',
    'is_supported_update_shutdown_target',
    'try_handle_update_shutdown'
)) {
    if (($hostUpdateSource + $hostSource).IndexOf($contract, [StringComparison]::Ordinal) -lt 0) {
        throw "The Rust host lost its graceful update handoff contract: $contract"
    }
}
$shutdownSource = $hostSource.Substring($hostSource.IndexOf('fn handle_startup_update_shutdown'))
if ($shutdownSource -match 'is_supported_tauri_production_installation\(') {
    throw 'Authenticated shutdown must not depend on strict payload/version update eligibility.'
}
if ($installer -match 'WaitForProcessExit\(RequestedProcessId,' -or
    $installer -notmatch 'SW_HIDE, ewNoWait, ExitCode') {
    throw 'Installer must request shutdown of the exact installed host with bounded waiting, not blindly wait on a supplied PID.'
}
$tauriConfig = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'QingToolbox.Tauri/src-tauri/tauri.conf.json') -Raw | ConvertFrom-Json
if ($tauriConfig.bundle.PSObject.Properties.Name -contains 'resources') {
    throw 'The host Tauri bundle must not include official modules.'
}

Write-Host 'Tauri release candidate gate contracts passed.'
