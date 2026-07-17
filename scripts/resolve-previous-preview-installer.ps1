[CmdletBinding()]
param(
    [string]$Tag = "v0.1.0-alpha",
    [string]$InstallerPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$release = gh release view $Tag --repo QingMo-A/QingToolbox --json assets | ConvertFrom-Json
$installers = @($release.assets | Where-Object { $_.name -match '-win-x64-setup\.exe$' })
if ($installers.Count -ne 1) { throw "Official $Tag must contain exactly one win-x64 setup executable." }
$asset = $installers[0]
$sidecarName = "$($asset.name).sha256"
if (@($release.assets | Where-Object name -EQ $sidecarName).Count -ne 1) { throw "Official checksum asset is missing: $sidecarName" }
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    gh release download $Tag --repo QingMo-A/QingToolbox --dir $output --pattern $asset.name --pattern $sidecarName --clobber
    if ($LASTEXITCODE -ne 0) { throw "Official Preview installer download failed." }
    $InstallerPath = Join-Path $output $asset.name
} else {
    $InstallerPath = [IO.Path]::GetFullPath($InstallerPath)
    $sidecarSource = "$InstallerPath.sha256"
    if (-not (Test-Path -LiteralPath $sidecarSource -PathType Leaf)) {
        gh release download $Tag --repo QingMo-A/QingToolbox --dir $output --pattern $sidecarName --clobber
        if ($LASTEXITCODE -ne 0) { throw "Official Preview checksum download failed." }
        $sidecarSource = Join-Path $output $sidecarName
    }
    $sidecarName = Split-Path -Leaf $sidecarSource
}
if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf) -or (Get-Item $InstallerPath).Length -le 0) { throw "Previous installer is missing or empty." }
$sidecar = Join-Path $output $sidecarName
if (-not (Test-Path -LiteralPath $sidecar)) { $sidecar = "$InstallerPath.sha256" }
$text = (Get-Content -LiteralPath $sidecar -Raw).Trim()
$match = [regex]::Match($text, '^(?<hash>[0-9A-Fa-f]{64})\s{2}(?<file>[^\r\n]+)$')
if (-not $match.Success -or $match.Groups.file.Value -ne $asset.name) { throw "Official checksum sidecar format or filename is invalid." }
$actual = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash
if ($actual -ne $match.Groups.hash.Value.ToUpperInvariant()) { throw "Official Preview installer SHA256 mismatch." }
[pscustomobject]@{ Tag = $Tag; InstallerPath = [IO.Path]::GetFullPath($InstallerPath); FileName = $asset.name; Sha256 = $actual; Size = (Get-Item $InstallerPath).Length }
