[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Profile,
    [ValidateSet("Debug", "Release")][string]$Configuration = "Debug",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'local-environment-common.ps1')
$profileInfo = Resolve-LocalEnvironmentProfile -Environment ModuleTest -Profile $Profile
Assert-NoLocalProfileReparsePoints -ProfileInfo $profileInfo
Write-Host "Environment:      ModuleTest"
Write-Host "Profile:          $Profile"
Write-Host "ProfileRoot:      $($profileInfo.ProfileRoot)"
Write-Host "Module directory: $(Join-Path $profileInfo.ProfileRoot 'local\modules')"
$arguments = @('run','--project',(Join-Path $profileInfo.RepoRoot 'QingToolbox.Shell\QingToolbox.Shell.csproj'),'--configuration',$Configuration)
if ($NoBuild) { $arguments += '--no-build' }
$arguments += @('--','--environment','ModuleTest','--profile',$Profile,'--data-root',$profileInfo.ProfileRoot)
& dotnet @arguments
exit $LASTEXITCODE
