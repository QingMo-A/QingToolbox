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
    "'build-tauri-installer.ps1'",
    'sourceDirty',
    'sourceCommit',
    '-tauri-setup.exe',
    '-win-x64-setup.exe',
    'Get-FileHash',
    'SHA256',
    'HEAD changed while the Tauri candidate gate was running.',
    'Signing:       required before public release',
    'migration AppId (production cut-over not performed)'
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

Write-Host 'Tauri release candidate gate contracts passed.'
