[CmdletBinding()]
param(
    [switch]$RequireToolboxBranch,
    [switch]$RequireOriginSync
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

function Invoke-SourceGit {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = @(& git -C $repoRoot @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        throw "Git command failed: git $($Arguments -join ' ')`n$($output -join "`n")"
    }
    return $output
}

function Assert-PreviewSource {
    [CmdletBinding()]
    param(
        [switch]$RequireToolboxBranch,
        [switch]$RequireOriginSync
    )

    [void](Invoke-SourceGit -Arguments @("rev-parse", "--is-inside-work-tree"))
    $commit = ([string](Invoke-SourceGit -Arguments @("rev-parse", "HEAD"))).Trim()
    if ($commit -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Unable to resolve a full source commit from Git."
    }

    $branch = ([string](Invoke-SourceGit -Arguments @(
        "branch", "--show-current"))).Trim()
    if ($RequireToolboxBranch) {
        if ([string]::IsNullOrWhiteSpace($branch)) {
            throw "Preview candidates must not be built from detached HEAD."
        }
        if ($branch -ne "toolbox") {
            throw "Preview candidates must be built from toolbox; current branch: $branch"
        }
    }

    $dirty = @(
        @(Invoke-SourceGit -Arguments @(
            "status", "--porcelain=v1", "--untracked-files=all")) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($dirty.Count -gt 0) {
        throw "Preview source tree is dirty. Commit or remove these changes:`n" +
              ($dirty -join "`n")
    }

    $diffIssues = @(
        @(
            Invoke-SourceGit -Arguments @("diff", "--check")
            Invoke-SourceGit -Arguments @("diff", "--cached", "--check")
        ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($diffIssues.Count -gt 0) {
        throw "Git whitespace validation failed:`n$($diffIssues -join "`n")"
    }

    $isOriginSynced = $false
    if ($RequireOriginSync) {
        [void](Invoke-SourceGit -Arguments @(
            "fetch", "--no-tags", "origin", "toolbox"))
        $originCommit = ([string](Invoke-SourceGit -Arguments @(
            "rev-parse", "refs/remotes/origin/toolbox"))).Trim()
        if ($commit -ne $originCommit) {
            throw "HEAD is not synchronized with origin/toolbox. " +
                  "HEAD $commit, origin/toolbox $originCommit."
        }
        $isOriginSynced = $true
    }

    return [pscustomobject]@{
        Commit = $commit.ToLowerInvariant()
        Branch = $branch
        IsClean = $true
        IsOriginSynced = $isOriginSynced
    }
}

if ($MyInvocation.InvocationName -ne ".") {
    try {
        Assert-PreviewSource @PSBoundParameters
    }
    catch {
        Write-Error -ErrorRecord $_
        exit 1
    }
}
