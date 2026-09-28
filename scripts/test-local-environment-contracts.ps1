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

Write-Host 'Tauri development and production environment contracts passed.'
