[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'local-environment-common.ps1')
$shell = (Get-Process -Id $PID).Path
$reset = Join-Path $PSScriptRoot 'reset-local-profile.ps1'
$dev = Join-Path $PSScriptRoot 'start-dev-host.ps1'
$moduleTest = Join-Path $PSScriptRoot 'start-module-test-host.ps1'
$suffix = [Guid]::NewGuid().ToString('N')
$developmentProfile = "Contract-$suffix"
$junctionProfile = "Junction-$suffix"
$nestedJunctionProfile = "NestedJunction-$suffix"
$targetRoot = Join-Path ([IO.Path]::GetTempPath()) "QingToolbox-junction-target-$suffix"
$fakeRepositoryRoot = Join-Path ([IO.Path]::GetTempPath()) "QingToolbox-fake-repository-$suffix"

function Invoke-Script {
    param([string]$Script, [string[]]$Arguments)
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $shell -NoProfile -ExecutionPolicy Bypass -File $Script @Arguments *> $null
        return $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousErrorActionPreference }
}

function Assert-Rejected {
    param([string]$Script, [string[]]$Arguments)
    if ((Invoke-Script -Script $Script -Arguments $Arguments) -eq 0) {
        throw "Unsafe arguments were accepted by $Script."
    }
}

$development = Resolve-LocalEnvironmentProfile -Environment Development -Profile $developmentProfile
$module = Resolve-LocalEnvironmentProfile -Environment ModuleTest -Profile $junctionProfile
$nestedModule = Resolve-LocalEnvironmentProfile -Environment ModuleTest -Profile $nestedJunctionProfile
$localRootExisted = Test-Path -LiteralPath $development.LocalRoot
$developmentRootExisted = Test-Path -LiteralPath $development.EnvironmentRoot
$moduleTestRootExisted = Test-Path -LiteralPath $module.EnvironmentRoot
$expectedLocalRoot = [IO.Path]::GetFullPath((Join-Path $development.RepoRoot '.qingtoolbox'))
if (-not $development.LocalRoot.Equals($expectedLocalRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not $development.ProfileRoot.Equals(
        [IO.Path]::GetFullPath((Join-Path $expectedLocalRoot "development\$developmentProfile")),
        [StringComparison]::OrdinalIgnoreCase) -or
    -not $module.ProfileRoot.Equals(
        [IO.Path]::GetFullPath((Join-Path $expectedLocalRoot "module-test\$junctionProfile")),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Common helper did not resolve strict project-local profile paths.'
}
if (-not (Assert-LocalRepositoryRoot -RepositoryRoot $development.RepoRoot).Equals(
        $development.RepoRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Common helper did not validate the real repository root.'
}
[IO.Directory]::CreateDirectory($fakeRepositoryRoot) > $null
$fakeRejected = $false
try { Assert-LocalRepositoryRoot -RepositoryRoot $fakeRepositoryRoot > $null }
catch { $fakeRejected = $true }
if (-not $fakeRejected) { throw 'Repository root without required markers was accepted.' }

foreach ($launch in @(
    @{ Script = $dev; Arguments = @('-Profile', $developmentProfile, '-NoBuild', '-ValidateOnly') },
    @{ Script = $moduleTest; Arguments = @('-Profile', $junctionProfile, '-NoBuild', '-ValidateOnly') })) {
    $launchArguments = $launch.Arguments
    $output = @(& $shell -NoProfile -ExecutionPolicy Bypass -File $launch.Script @launchArguments)
    if ($LASTEXITCODE -ne 0) { throw "Launch validation failed for $($launch.Script)." }
    $repoArgument = [Array]::IndexOf($output, 'Argument: --repo-root')
    if ($repoArgument -lt 0 -or $repoArgument + 1 -ge $output.Count -or
        -not $output[$repoArgument + 1].Equals("Argument: $($development.RepoRoot)", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Launch arguments did not contain the validated repository root: $($launch.Script)"
    }
}

foreach ($profile in @('..', '../escape', '..\escape', 'bad/name', 'bad\name', 'bad*name', 'bad?name')) {
    Assert-Rejected $dev @('-Profile', $profile, '-NoBuild')
    Assert-Rejected $moduleTest @('-Profile', $profile, '-NoBuild')
    Assert-Rejected $reset @('-Environment', 'Development', '-Profile', $profile, '-Force')
}
Assert-Rejected $reset @('-Environment', 'Unknown', '-Profile', $developmentProfile, '-Force')

try {
    [IO.Directory]::CreateDirectory($development.ProfileRoot) > $null
    [IO.File]::WriteAllText((Join-Path $development.ProfileRoot 'sentinel.txt'), 'keep')
    if ((Invoke-Script $reset @('-Environment', 'Development', '-Profile', $developmentProfile, '-WhatIf')) -ne 0 -or
        -not (Test-Path -LiteralPath $development.ProfileRoot)) {
        throw '-WhatIf removed or rejected a normal test profile.'
    }
    if ((Invoke-Script $reset @('-Environment', 'Development', '-Profile', $developmentProfile, '-Force', '-WhatIf')) -ne 0 -or
        -not (Test-Path -LiteralPath $development.ProfileRoot)) {
        throw '-Force -WhatIf removed or rejected a normal test profile.'
    }
    if ((Invoke-Script $reset @('-Environment', 'Development', '-Profile', $developmentProfile, '-Force')) -ne 0 -or
        (Test-Path -LiteralPath $development.ProfileRoot)) {
        throw '-Force did not reset the normal test profile.'
    }

    [IO.Directory]::CreateDirectory($targetRoot) > $null
    $targetSentinel = Join-Path $targetRoot 'external-sentinel.txt'
    [IO.File]::WriteAllText($targetSentinel, 'must survive')
    [IO.Directory]::CreateDirectory($module.EnvironmentRoot) > $null
    $junctionCreated = $false
    try {
        New-Item -ItemType Junction -Path $module.ProfileRoot -Target $targetRoot -ErrorAction Stop > $null
        $junctionCreated = $true
    }
    catch {
        Write-Host "Junction checks skipped: $($_.Exception.Message)"
    }
    if ($junctionCreated) {
        $reparseRejected = $false
        try { Assert-NoLocalProfileReparsePoints -ProfileInfo $module }
        catch { $reparseRejected = $true }
        if (-not $reparseRejected) { throw 'Common helper accepted a profile-root Junction.' }
        Assert-Rejected $moduleTest @('-Profile', $junctionProfile, '-NoBuild')
        Assert-Rejected $reset @('-Environment', 'ModuleTest', '-Profile', $junctionProfile, '-Force')
        if (-not (Test-Path -LiteralPath $targetSentinel)) { throw 'Junction target content was modified.' }

        [IO.Directory]::CreateDirectory($nestedModule.ProfileRoot) > $null
        $nestedJunction = Join-Path $nestedModule.ProfileRoot 'linked-directory'
        New-Item -ItemType Junction -Path $nestedJunction -Target $targetRoot -ErrorAction Stop > $null
        Assert-Rejected $reset @('-Environment', 'ModuleTest', '-Profile', $nestedJunctionProfile, '-Force')
        if (-not (Test-Path -LiteralPath $targetSentinel)) { throw 'Nested Junction target content was modified.' }
        Write-Host 'Junction checks passed.'
    }
}
finally {
    if (Test-Path -LiteralPath $module.ProfileRoot) {
        $attributes = [IO.File]::GetAttributes($module.ProfileRoot)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            [IO.Directory]::Delete($module.ProfileRoot)
        }
    }
    if (Test-Path -LiteralPath $nestedModule.ProfileRoot) {
        $nestedJunction = Join-Path $nestedModule.ProfileRoot 'linked-directory'
        if (Test-Path -LiteralPath $nestedJunction) {
            $attributes = [IO.File]::GetAttributes($nestedJunction)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
                throw 'Refusing cleanup because the nested test path is not a reparse point.'
            }
            [IO.Directory]::Delete($nestedJunction)
        }
        [IO.Directory]::Delete($nestedModule.ProfileRoot, $true)
    }
    if (Test-Path -LiteralPath $development.ProfileRoot) {
        Remove-Item -LiteralPath $development.ProfileRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $targetRoot) {
        if (-not (Test-Path -LiteralPath (Join-Path $targetRoot 'external-sentinel.txt'))) {
            throw 'Junction target sentinel did not survive cleanup.'
        }
        Remove-Item -LiteralPath $targetRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $fakeRepositoryRoot) {
        Remove-Item -LiteralPath $fakeRepositoryRoot -Recurse -Force
    }
    foreach ($entry in @(
        @{ Path = $development.EnvironmentRoot; Existed = $developmentRootExisted },
        @{ Path = $module.EnvironmentRoot; Existed = $moduleTestRootExisted },
        @{ Path = $development.LocalRoot; Existed = $localRootExisted })) {
        if (-not $entry.Existed -and (Test-Path -LiteralPath $entry.Path) -and
            [IO.Directory]::GetFileSystemEntries($entry.Path).Length -eq 0) {
            [IO.Directory]::Delete($entry.Path)
        }
    }
}

if ((Test-Path -LiteralPath $development.ProfileRoot) -or
    (Test-Path -LiteralPath $module.ProfileRoot) -or
    (Test-Path -LiteralPath $nestedModule.ProfileRoot)) {
    throw 'Local environment contract test left a profile behind.'
}
Write-Host 'Local environment script contracts passed.'
