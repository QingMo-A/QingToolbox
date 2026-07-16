[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Profile,
    [ValidateSet("Debug", "Release")][string]$Configuration = "Debug",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
if ($Profile -notmatch '^[A-Za-z0-9._-]{1,64}$' -or $Profile -in @('.', '..')) {
    throw "Profile must contain 1-64 letters, digits, '.', '_' or '-' only."
}
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$profilesRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.qingtoolbox\module-test'))
$sandboxRoot = [IO.Path]::GetFullPath((Join-Path $profilesRoot $Profile))
$prefix = $profilesRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $sandboxRoot.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Profile path escaped the module-test sandbox."
}
Write-Host "Environment:      ModuleTest"
Write-Host "Profile:          $Profile"
Write-Host "SandboxRoot:      $sandboxRoot"
Write-Host "Module directory: $(Join-Path $sandboxRoot 'local\modules')"
$arguments = @('run','--project',(Join-Path $repoRoot 'QingToolbox.Shell\QingToolbox.Shell.csproj'),'--configuration',$Configuration)
if ($NoBuild) { $arguments += '--no-build' }
$arguments += @('--','--environment','ModuleTest','--profile',$Profile,'--data-root',$sandboxRoot)
& dotnet @arguments
exit $LASTEXITCODE
