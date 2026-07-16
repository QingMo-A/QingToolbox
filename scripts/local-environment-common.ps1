function Assert-LocalProfileName {
    param([Parameter(Mandatory = $true)][string]$Profile)
    if ($Profile -notmatch '^[A-Za-z0-9._-]{1,64}$' -or $Profile -in @('.', '..') -or
        $Profile.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $Profile.Contains('*') -or $Profile.Contains('?')) {
        throw "Profile must contain 1-64 letters, digits, '.', '_' or '-' only."
    }
}

function Resolve-LocalEnvironmentProfile {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('Development', 'ModuleTest')]
        [string]$Environment,
        [Parameter(Mandatory = $true)][string]$Profile
    )
    Assert-LocalProfileName -Profile $Profile
    $repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    $localRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.qingtoolbox'))
    $environmentFolder = if ($Environment -eq 'Development') { 'development' } else { 'module-test' }
    $environmentRoot = [IO.Path]::GetFullPath((Join-Path $localRoot $environmentFolder))
    $profileRoot = [IO.Path]::GetFullPath((Join-Path $environmentRoot $Profile))
    $result = [pscustomobject]@{
        RepoRoot = $repoRoot
        LocalRoot = $localRoot
        EnvironmentRoot = $environmentRoot
        ProfileRoot = $profileRoot
        EnvironmentKind = $Environment
        ProfileName = $Profile
    }
    Assert-LocalProfilePath -ProfileInfo $result
    return $result
}

function Assert-LocalProfilePath {
    param([Parameter(Mandatory = $true)]$ProfileInfo)
    $comparison = [StringComparison]::OrdinalIgnoreCase
    $expectedLocalRoot = [IO.Path]::GetFullPath((Join-Path $ProfileInfo.RepoRoot '.qingtoolbox'))
    $environmentFolder = if ($ProfileInfo.EnvironmentKind -eq 'Development') { 'development' }
        elseif ($ProfileInfo.EnvironmentKind -eq 'ModuleTest') { 'module-test' }
        else { throw "Environment must be Development or ModuleTest." }
    $expectedEnvironmentRoot = [IO.Path]::GetFullPath((Join-Path $expectedLocalRoot $environmentFolder))
    $expectedProfileRoot = [IO.Path]::GetFullPath((Join-Path $expectedEnvironmentRoot $ProfileInfo.ProfileName))
    if (-not $ProfileInfo.LocalRoot.Equals($expectedLocalRoot, $comparison) -or
        -not $ProfileInfo.EnvironmentRoot.Equals($expectedEnvironmentRoot, $comparison) -or
        -not $ProfileInfo.ProfileRoot.Equals($expectedProfileRoot, $comparison)) {
        throw "Profile path must match <RepoRoot>\.qingtoolbox\$environmentFolder\<Profile>."
    }
}

function Assert-NoLocalProfileReparsePoints {
    param(
        [Parameter(Mandatory = $true)]$ProfileInfo,
        [switch]$IncludeDescendants
    )
    Assert-LocalProfilePath -ProfileInfo $ProfileInfo
    foreach ($path in @($ProfileInfo.LocalRoot, $ProfileInfo.EnvironmentRoot, $ProfileInfo.ProfileRoot)) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $attributes = [IO.File]::GetAttributes($path)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Local profile path contains a reparse point: $path"
        }
    }
    if (-not $IncludeDescendants -or -not (Test-Path -LiteralPath $ProfileInfo.ProfileRoot -PathType Container)) { return }

    $pending = New-Object 'System.Collections.Generic.Queue[string]'
    $pending.Enqueue($ProfileInfo.ProfileRoot)
    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        foreach ($child in @(Get-ChildItem -LiteralPath $directory -Directory -Force -ErrorAction Stop)) {
            $attributes = [IO.File]::GetAttributes($child.FullName)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Local profile contains a reparse point: $($child.FullName)"
            }
            $pending.Enqueue($child.FullName)
        }
    }
}
