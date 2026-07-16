[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Development", "ModuleTest")][string]$Environment,
    [Parameter(Mandatory = $true)][string]$Profile,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'local-environment-common.ps1')
$profileInfo = Resolve-LocalEnvironmentProfile -Environment $Environment -Profile $Profile
if (-not (Test-Path -LiteralPath $profileInfo.ProfileRoot)) {
    Write-Host "Profile does not exist: $($profileInfo.ProfileRoot)"
    exit 0
}
Assert-NoLocalProfileReparsePoints -ProfileInfo $profileInfo -IncludeDescendants
if ($Force) { $ConfirmPreference = 'None' }
if ($PSCmdlet.ShouldProcess($profileInfo.ProfileRoot, 'Delete local QingToolbox profile')) {
    Remove-Item -LiteralPath $profileInfo.ProfileRoot -Recurse -Force
    Write-Host "Reset local profile: $($profileInfo.ProfileRoot)"
}
