[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Profile,
    [ValidateSet("Debug", "Release")][string]$Configuration = "Debug",
    [switch]$NoBuild,
    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'local-environment-common.ps1')
$profileInfo = Resolve-LocalEnvironmentProfile -Environment ModuleTest -Profile $Profile
Assert-NoLocalProfileReparsePoints -ProfileInfo $profileInfo
Write-Host "Environment:      ModuleTest"
Write-Host "Profile:          $Profile"
Write-Host "RepositoryRoot:   $($profileInfo.RepoRoot)"
Write-Host "ProfileRoot:      $($profileInfo.ProfileRoot)"
Write-Host "Module directory: $(Join-Path $profileInfo.ProfileRoot 'local\modules')"
$arguments = @('run','--project',(Join-Path $profileInfo.RepoRoot 'QingToolbox.Shell\QingToolbox.Shell.csproj'),'--configuration',$Configuration)
if ($NoBuild) { $arguments += '--no-build' }
$arguments += @('--','--environment','ModuleTest','--profile',$Profile,'--repo-root',$profileInfo.RepoRoot)
if ($ValidateOnly) {
    foreach ($item in $arguments) { Write-Output "Argument: $item" }
    exit 0
}
& dotnet @arguments
exit $LASTEXITCODE
