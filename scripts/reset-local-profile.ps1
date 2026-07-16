[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Development", "ModuleTest")][string]$Environment,
    [Parameter(Mandatory = $true)][string]$Profile,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
if ($Profile -notmatch '^[A-Za-z0-9._-]{1,64}$' -or $Profile -in @('.', '..')) {
    throw "Profile must contain 1-64 letters, digits, '.', '_' or '-' only."
}
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$localRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.qingtoolbox'))
$environmentFolder = if ($Environment -eq 'Development') { 'development' } else { 'module-test' }
$profilesRoot = [IO.Path]::GetFullPath((Join-Path $localRoot $environmentFolder))
$target = [IO.Path]::GetFullPath((Join-Path $profilesRoot $Profile))
$prefix = $profilesRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
    $target -in @($repoRoot, $localRoot, $profilesRoot, [IO.Path]::GetPathRoot($target))) {
    throw "Refusing to reset a path outside a concrete local profile."
}
if (-not (Test-Path -LiteralPath $target)) { Write-Host "Profile does not exist: $target"; exit 0 }
if ($Force -or $PSCmdlet.ShouldProcess($target, 'Delete local QingToolbox profile')) {
    Remove-Item -LiteralPath $target -Recurse -Force
    Write-Host "Reset local profile: $target"
}
