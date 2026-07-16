[CmdletBinding()]
param(
    [string]$Profile = "Shell",
    [ValidateSet("Debug", "Release")][string]$Configuration = "Debug",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
if ($Profile -notmatch '^[A-Za-z0-9._-]{1,64}$' -or $Profile -in @('.', '..')) {
    throw "Profile must contain 1-64 letters, digits, '.', '_' or '-' only."
}
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$profilesRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.qingtoolbox\development'))
$sandboxRoot = [IO.Path]::GetFullPath((Join-Path $profilesRoot $Profile))
$prefix = $profilesRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $sandboxRoot.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Profile path escaped the development sandbox."
}
Write-Host "Environment: Development"
Write-Host "Profile:     $Profile"
Write-Host "SandboxRoot: $sandboxRoot"
$arguments = @('run','--project',(Join-Path $repoRoot 'QingToolbox.Shell\QingToolbox.Shell.csproj'),'--configuration',$Configuration)
if ($NoBuild) { $arguments += '--no-build' }
$arguments += @('--','--environment','Development','--profile',$Profile,'--data-root',$sandboxRoot)
& dotnet @arguments
exit $LASTEXITCODE
