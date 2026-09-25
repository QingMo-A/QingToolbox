[CmdletBinding(SupportsShouldProcess = $true)]
param([switch]$Apply)

# Only disposable build directories. Keep Release, installed/staged modules,
# installers, APKs, node_modules and all .qingtoolbox profiles intact.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$candidates = New-Object 'System.Collections.Generic.List[string]'
function Add-Candidate([string]$Relative) {
    $path = [IO.Path]::GetFullPath((Join-Path $repo $Relative))
    if (-not $path.StartsWith($repo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Out-of-workspace cleanup target: $path"
    }
    if (Test-Path -LiteralPath $path -PathType Container) { $candidates.Add($path) }
}

Add-Candidate 'QingToolbox.Tauri/src-tauri/target/debug'
foreach ($module in @('native-module-canary','native-launcher','native-pdf','native-transfer','native-texttools','native-windowtopmost','native-powerguard','native-screenpin')) {
    Add-Candidate "QingToolbox.Tauri/$module/target/debug"
}
foreach ($project in @(Get-ChildItem -LiteralPath $repo -Directory | Where-Object Name -Like 'QingToolbox.*')) {
    foreach ($generated in @('bin','obj')) { Add-Candidate "$($project.Name)/$generated" }
}
foreach ($name in @(
    'appearance-contract-compile','appearance-host','appearance-host-check',
    'badge-position-final','badge-position-smoke','font-build','font-build-obj',
    'font-smoke','font-smoke-obj','font-smoke2','font-smoke2-obj',
    'module-drag-await-check','module-drag-await-check2','module-position-build-check',
    'shell-build-check','shell-drag-await-check','shell-position-build-check',
    'tauri-production-fixed','testbuild'
)) { Add-Candidate "artifacts/$name" }

# Validate all targets before removing any: no tracked files, junctions or
# running workspace processes. A failed/unknown condition leaves files alone.
$tracked = @(& git -C $repo ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Cannot establish tracked source boundary.' }
$rows = foreach ($path in $candidates) {
    $relative = $path.Substring($repo.Length + 1).Replace('\','/')
    if (@($tracked | Where-Object { $_ -eq $relative -or $_.StartsWith($relative + '/', [StringComparison]::OrdinalIgnoreCase) }).Count) {
        throw "Tracked files under cleanup target: $path"
    }
    $cursor = Get-Item -LiteralPath $path -Force
    while ($cursor -and $cursor.FullName -ne $repo) {
        if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point in cleanup path: $($cursor.FullName)" }
        $cursor = $cursor.Parent
    }
    $items = @(Get-ChildItem -LiteralPath $path -Recurse -Force)
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw "Reparse point inside: $path" }
    $files = @($items | Where-Object { -not $_.PSIsContainer })
    $bytes = if ($files.Count) { ($files | Measure-Object -Property Length -Sum).Sum } else { 0 }
    [pscustomobject]@{ Path = $path; Bytes = [long]$bytes; GiB = [math]::Round($bytes / 1GB, 3) }
}
$rows | Select-Object GiB,Path | Format-Table -AutoSize
$total = if (@($rows).Count) { ($rows | Measure-Object Bytes -Sum).Sum } else { 0 }
Write-Host ('Disposable total: {0:N2} GiB' -f ($total / 1GB))
if (-not $Apply) { Write-Host 'Preview only. Pass -Apply to remove these generated directories.'; return }

$busy = @(Get-CimInstance Win32_Process | Where-Object {
    $_.Name -match '^(cargo|rustc|dotnet|MSBuild|ISCC)\.exe$' -or
    ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase))
})
if ($busy.Count) { throw 'Build tools or workspace executables are running; close them before cleaning.' }
foreach ($row in $rows) {
    if ($PSCmdlet.ShouldProcess($row.Path, 'Remove disposable build directory')) {
        Remove-Item -LiteralPath $row.Path -Recurse -Force
        Write-Host "Removed: $($row.Path)"
    }
}
