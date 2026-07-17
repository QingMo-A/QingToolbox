[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PreviousManifestPath,
    [Parameter(Mandatory = $true)][string]$CurrentManifestPath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
function Assert-Safe([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path) -or [IO.Path]::IsPathRooted($path) -or
        $path -match '^[A-Za-z]:' -or $path -match '^[/\\]{2}' -or
        $path -match '(^|[/\\])\.\.([/\\]|$)' -or $path -match ':') { throw "Unsafe owned payload path: $path" }
    $normalized = $path.Replace('\', '/')
    if ($normalized -match '(^|/)(Modules|modules)(/|$)') { throw "Module paths cannot be host-owned cleanup entries: $path" }
    return $normalized
}
$previous = Get-Content -LiteralPath $PreviousManifestPath -Raw | ConvertFrom-Json
$current = Get-Content -LiteralPath $CurrentManifestPath -Raw | ConvertFrom-Json
if ($previous.schemaVersion -ne 1 -or $current.schemaVersion -ne 1) { throw "Unsupported host payload manifest schema." }
$currentSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $current.entries) { [void]$currentSet.Add((Assert-Safe ([string]$entry.relativePath))) }
$obsolete = @($previous.entries | ForEach-Object { Assert-Safe ([string]$_.relativePath) } |
    Where-Object { -not $currentSet.Contains($_) } | Sort-Object -Unique)
$lines = @('; Generated exact Preview host cleanup. Do not edit.', '[InstallDelete]')
foreach ($path in $obsolete) { $lines += 'Type: files; Name: "{app}\' + $path.Replace('/', '\') + '"' }
$directories = @($obsolete | ForEach-Object { [IO.Path]::GetDirectoryName($_.Replace('/', '\')) } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object Length -Descending -Unique)
foreach ($directory in $directories) { $lines += 'Type: dirifempty; Name: "{app}\' + $directory + '"' }
$parent = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath)); New-Item -ItemType Directory -Path $parent -Force | Out-Null
$lines | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Write-Host "Obsolete host payload include: $OutputPath ($($obsolete.Count) files)"
