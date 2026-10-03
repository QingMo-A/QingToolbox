[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
function Read-Source([string]$relative) {
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Tauri development file is missing: $relative"
    }
    return [IO.File]::ReadAllText($path)
}

$latest = Read-Source 'run-latest.bat'
$dev = Read-Source 'run-tauri-dev.bat'
$stop = Read-Source 'stop-qingtoolbox.bat'
$runner = Read-Source 'scripts/run-tauri-latest.ps1'
$moduleBuilder = Read-Source 'scripts/build-tauri-modules.ps1'
$paths = Read-Source 'QingToolbox.Tauri/src-tauri/src/paths.rs'
$title = Read-Source 'QingToolbox.WebUI/src/design-system/components/QTitleBar.vue'

if ($latest -notmatch 'scripts\\run-tauri-latest\.ps1' -or
    $dev -notmatch 'scripts\\run-tauri-latest\.ps1' -or
    $dev -notmatch '-Configuration Debug' -or
    $stop -notmatch 'scripts\\stop-tauri-host\.ps1') {
    throw 'A desktop entry point no longer targets the Tauri host.'
}
if ($runner -notmatch 'tauri\.dev\.conf\.json' -or
    $runner -notmatch 'QING_TAURI_DISABLE_AUTOSTART_SYNC' -or
    $paths -notmatch 'QingToolbox\.Dev' -or
    $paths -notmatch 'profile_directory_name\(\)' -or
    $title -notmatch '\[Dev\]') {
    throw 'The development host identity or data isolation contract is missing.'
}

$production = Read-Source 'QingToolbox.Tauri/src-tauri/tauri.conf.json' | ConvertFrom-Json
$development = Read-Source 'QingToolbox.Tauri/src-tauri/tauri.dev.conf.json' | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace([string]$development.identifier) -or
    $development.identifier -eq $production.identifier) {
    throw 'Development and production Tauri identities must differ.'
}
if (Test-Path -LiteralPath (Join-Path $root 'run-legacy-wpf.bat') -PathType Leaf) {
    throw 'The retired WPF development launcher is still present.'
}

# Startup prepares these independent modules before it launches the host. Keep
# the list aligned with the available build scripts after retiring a module.
$moduleList = [regex]::Match($moduleBuilder, 'foreach \(\$module in @\(([^)]+)\)\)')
if (-not $moduleList.Success) { throw 'Cannot inspect development module build scopes.' }
$scopes = @([regex]::Matches($moduleList.Groups[1].Value, "'([^']+)'") |
    ForEach-Object { $_.Groups[1].Value })
if ($scopes.Count -eq 0 -or $scopes -contains 'transfer') {
    throw 'Development startup still depends on the retired transfer module.'
}
foreach ($scope in $scopes) {
    $buildScript = Read-Source "scripts/build-tauri-$scope.ps1"
    if ($scope -ne 'canary' -and $buildScript -notmatch 'Restore-QingModuleUiDependencies') {
        throw "Module $scope no longer reuses locked UI dependencies."
    }
}
if ($runner -notmatch 'Get-WorkspaceDebugHost' -or
    $runner -notmatch 'RedirectStandardError' -or
    $runner -notmatch 'MainWindowHandle -ne 0') {
    throw 'Development startup no longer verifies the actual window or preserves diagnostics.'
}

Write-Host 'Tauri development and production environment contracts passed.'
