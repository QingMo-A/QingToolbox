[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallDirectory,
    [Parameter(Mandatory = $true)][string]$MigrationBackupDirectory,
    [switch]$Apply
)

# Retire only unchanged files owned by the old installer, after an accepted
# local Tauri upgrade. Never recursively delete an installation or AppData.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$backup = [IO.Path]::GetFullPath($MigrationBackupDirectory).TrimEnd('\')
$prefix = $root + '\'
if ($root.Length -le 3 -or $backup.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $root -eq $backup) {
    throw 'Installation and migration backup must be separate, bounded directories.'
}

function Resolve-OwnedPath([string]$Base, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or
        $Relative.Contains(':') -or ($Relative -split '[\\/]') -contains '..') {
        throw "Unsafe manifest path: $Relative"
    }
    $path = [IO.Path]::GetFullPath((Join-Path $Base $Relative))
    if (-not $path.StartsWith($Base.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes its owned directory: $path"
    }
    # Check every ancestor, not just the final file, before reads/removals.
    $current = $path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing reparse-point traversal: $current"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
    return $path
}

$registry = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}_is1')
if (-not $registry) { throw 'Registered QingToolbox product installation was not found.' }
try { $registered = [IO.Path]::GetFullPath([string]$registry.GetValue('InstallLocation')).TrimEnd('\') }
finally { $registry.Dispose() }
if ($registered -ne $root) { throw 'The requested directory is not the registered product installation.' }
$marker = Resolve-OwnedPath $backup 'install-location.txt'
if ([IO.File]::ReadAllText($marker).Trim().TrimEnd('\') -ne $root) { throw 'Migration backup belongs to another installation.' }
$backupRoot = Join-Path $backup 'installation'
$oldPath = Resolve-OwnedPath $root 'host-payload.manifest.json'
$savedManifest = Resolve-OwnedPath $backupRoot 'host-payload.manifest.json'
$oldManifestSource = if (Test-Path -LiteralPath $oldPath -PathType Leaf) { $oldPath } else { $savedManifest }
$old = Get-Content -LiteralPath $oldManifestSource -Raw | ConvertFrom-Json
$newPath = Resolve-OwnedPath $root 'portable-manifest.json'
$new = Get-Content -LiteralPath $newPath -Raw | ConvertFrom-Json
if ($new.backend -ne 'rust' -or $new.framework -ne 'tauri-2' -or $new.executable -ne 'QingToolbox.exe') {
    throw 'The installed application is not the expected Tauri host.'
}
if (@(Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}).Count -gt 0) { throw 'Exit all processes from the installation before retiring WPF files.' }

$protected = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
[void]$protected.Add('portable-manifest.json')
foreach ($entry in $new.files) {
    $relative = ([string]$entry.path).Replace('/', '\')
    $file = Resolve-OwnedPath $root $relative
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne [string]$entry.sha256) {
        throw "Installed Tauri payload failed verification: $relative"
    }
    [void]$protected.Add($relative)
}
$removals = New-Object 'System.Collections.Generic.List[object]'
$modified = New-Object 'System.Collections.Generic.List[string]'
$alreadyAbsent = New-Object 'System.Collections.Generic.List[string]'
foreach ($entry in $old.entries) {
    $relative = ([string]$entry.relativePath).Replace('/', '\')
    if ($protected.Contains($relative) -or $relative -match '^(Modules|Data|Settings|Logs)(\\|$)' -or $relative -match '^unins') { continue }
    $file = Resolve-OwnedPath $root $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { $alreadyAbsent.Add($relative); continue }
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    if ($hash -ne [string]$entry.sha256) { $modified.Add($relative); continue }
    $saved = Resolve-OwnedPath $backupRoot $relative
    if (-not (Test-Path -LiteralPath $saved -PathType Leaf) -or (Get-FileHash -LiteralPath $saved -Algorithm SHA256).Hash -ne $hash) {
        throw "Verified rollback copy is missing: $relative"
    }
    $removals.Add([pscustomobject]@{ relative = $relative; path = $file; sha256 = $hash; size = (Get-Item -LiteralPath $file).Length })
}
$oldHash = (Get-FileHash -LiteralPath $oldManifestSource -Algorithm SHA256).Hash
if ((Get-FileHash -LiteralPath $savedManifest -Algorithm SHA256).Hash -ne $oldHash) { throw 'Legacy manifest backup differs.' }
if (Test-Path -LiteralPath $oldPath -PathType Leaf) {
    $removals.Add([pscustomobject]@{ relative = 'host-payload.manifest.json'; path = $oldPath; sha256 = $oldHash; size = (Get-Item -LiteralPath $oldPath).Length })
} else { $alreadyAbsent.Add('host-payload.manifest.json') }
$bytes = 0L
foreach ($entry in $removals) { $bytes += $entry.size }
Write-Host "Verified legacy files: $($removals.Count); size: $([Math]::Round($bytes / 1MB, 1)) MiB"
Write-Host "Preserved modified legacy files: $($modified.Count)"
Write-Host "Legacy files already absent: $($alreadyAbsent.Count)"
if ($modified.Count) { $modified | ForEach-Object { Write-Warning "Preserved changed file: $_" } }
if (-not $Apply) { Write-Host 'Dry run only. Pass -Apply to remove these backed-up files.'; return }

# Full preflight above completes before the first removal. Re-check each
# exact file and path immediately before deletion; do not pass paths to cmd.
$directories = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $removals) {
    $file = Resolve-OwnedPath $root $entry.relative
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw "File changed during cleanup: $file" }
    Remove-Item -LiteralPath $file -Force
    $parent = [IO.Path]::GetDirectoryName($file)
    while ($parent.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        [void]$directories.Add($parent)
        $parent = [IO.Path]::GetDirectoryName($parent)
    }
}
foreach ($directory in @($directories | Sort-Object Length -Descending)) {
    $relative = $directory.Substring($prefix.Length)
    $checked = Resolve-OwnedPath $root $relative
    if ((Test-Path -LiteralPath $checked -PathType Container) -and @(Get-ChildItem -LiteralPath $checked -Force).Count -eq 0) {
        Remove-Item -LiteralPath $checked -Force
    }
}
$report = [ordered]@{ installation = $root; tauriVersion = $new.version; removedFiles = $removals.Count; removedBytes = $bytes; alreadyAbsent = $alreadyAbsent.ToArray(); preservedModified = $modified.ToArray(); removed = $removals.ToArray() }
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $backup 'wpf-retirement.json') -Encoding UTF8
Write-Host "Legacy WPF payload retired. Rollback files remain at: $backup"
