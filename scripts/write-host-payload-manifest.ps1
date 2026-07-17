[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$PayloadDirectory, [string]$OutputPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath($PayloadDirectory)
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Payload directory does not exist: $root" }
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $root "host-payload.manifest.json" }
$output = [IO.Path]::GetFullPath($OutputPath)
$prefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Host manifest must be inside the payload directory." }

function Get-Category([string]$relativePath) {
    if ($relativePath.StartsWith("docs/", [StringComparison]::Ordinal) -or $relativePath -in @("LICENSE", "CHANGELOG.md")) { return "documentation" }
    if ($relativePath.StartsWith("Resources/", [StringComparison]::Ordinal)) { return "resource" }
    if ($relativePath.StartsWith("QingToolbox.StartupMaintenance", [StringComparison]::Ordinal)) { return "maintenance" }
    if ([IO.Path]::GetExtension($relativePath) -in @(".dll", ".exe", ".json")) { return "assembly" }
    return "runtime"
}
function Get-RelativePath([string]$basePath, [string]$fullPath) {
    $baseUri = [Uri]($basePath.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar)
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri([Uri]$fullPath).ToString()).Replace('/', [IO.Path]::DirectorySeparatorChar)
}

$entries = foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) {
    if ($file.FullName -eq $output -or $file.Extension -in @(".pdb", ".cs", ".csproj", ".sln") -or
        $file.Name -match '^unins\d+\.(exe|dat|msg)$') { continue }
    $relative = (Get-RelativePath $root $file.FullName).Replace('\', '/')
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|/)\.\.(/|$)' -or
        $relative -match '(^|/)(Modules|modules|bin|obj|tests|\.git)(/|$)' -or
        $relative -match '(?i)(^|/)(settings\.json|startup-health\.json)$') { throw "Forbidden host payload path: $relative" }
    [ordered]@{ relativePath = $relative; size = [long]$file.Length;
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash;
        category = Get-Category $relative }
}
$manifest = [ordered]@{ schemaVersion = 1; entries = @($entries | Sort-Object { $_.relativePath }) }
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $output -Encoding UTF8
Write-Host "Host payload manifest: $output ($($manifest.entries.Count) entries)"
