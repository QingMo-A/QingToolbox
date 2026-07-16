[CmdletBinding()]
param(
    [string]$Profile = "Shell",
    [ValidateSet("Debug", "Release")][string]$Configuration = "Debug",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'local-environment-common.ps1')
$profileInfo = Resolve-LocalEnvironmentProfile -Environment Development -Profile $Profile
Assert-NoLocalProfileReparsePoints -ProfileInfo $profileInfo
Write-Host "Environment: Development"
Write-Host "Profile:     $Profile"
Write-Host "ProfileRoot: $($profileInfo.ProfileRoot)"
$arguments = @('run','--project',(Join-Path $profileInfo.RepoRoot 'QingToolbox.Shell\QingToolbox.Shell.csproj'),'--configuration',$Configuration)
if ($NoBuild) { $arguments += '--no-build' }
$arguments += @('--','--environment','Development','--profile',$Profile,'--data-root',$profileInfo.ProfileRoot)
& dotnet @arguments
exit $LASTEXITCODE
