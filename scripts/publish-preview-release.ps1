[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Version,

    [Parameter(Mandatory = $false)]
    [string]$ConfirmVersion,

    [switch]$ReadVersionFromEnvironment,

    # WhatIf and TestMode are deliberately handled before every git/gh call.  They
    # validate local release metadata, print the plan, and never mutate, watch, or
    # poll an external service.
    [switch]$WhatIf,
    [switch]$TestMode,

    [ValidateRange(5, 1800)]
    [int]$DiscoveryTimeoutSeconds = 120,

    [ValidateRange(1, 60)]
    [int]$PollSeconds = 3
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$repository = "QingMo-A/QingToolbox"
$workflow = "preview-release-validation.yml"
$semVerIdentifier = '(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)'
$semVerPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-' +
    $semVerIdentifier + '(?:\.' + $semVerIdentifier + ')*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'

if ($ReadVersionFromEnvironment) {
    # Values collected by the BAT are inherited as environment data, never
    # interpolated into a cmd.exe or PowerShell command string.  Clear the process
    # copy immediately after reading it so a nested process cannot reuse stale input.
    $environmentVersion = [Environment]::GetEnvironmentVariable("QING_RELEASE_VERSION_FIRST", "Process")
    $environmentConfirmVersion = [Environment]::GetEnvironmentVariable("QING_RELEASE_VERSION_SECOND", "Process")
    [Environment]::SetEnvironmentVariable("QING_RELEASE_VERSION_FIRST", $null, "Process")
    [Environment]::SetEnvironmentVariable("QING_RELEASE_VERSION_SECOND", $null, "Process")
    $Version = [string]$environmentVersion
    $ConfirmVersion = [string]$environmentConfirmVersion
}

function Get-OutputText {
    param(
        [AllowEmptyCollection()]
        [object[]]$Lines
    )

    if ($null -eq $Lines) {
        return ""
    }
    return (($Lines | ForEach-Object { [string]$_ }) -join "`n")
}

function Test-StrictSemVer {
    param([AllowEmptyString()][string]$Value)

    if ([string]::IsNullOrEmpty($Value)) {
        return $false
    }
    return $Value -cmatch $script:semVerPattern
}

function Invoke-Git {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = @(& git -C $repoRoot @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Git failed (exit $exitCode): git -C `"$repoRoot`" $($Arguments -join ' ')`n$(Get-OutputText $output)"
    }
    return $output
}

function Invoke-GitProbe {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = @(& git -C $repoRoot @Arguments 2>&1)
    return [pscustomobject]@{
        Output = $output
        ExitCode = $LASTEXITCODE
    }
}

function Invoke-Gh {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = @(& gh @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "GitHub CLI failed (exit $exitCode): gh $($Arguments -join ' ')`n$(Get-OutputText $output)"
    }
    return $output
}

function Invoke-GhProbe {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $output = @(& gh @Arguments 2>&1)
    return [pscustomobject]@{
        Output = $output
        ExitCode = $LASTEXITCODE
    }
}

function ConvertFrom-GhJson {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Lines
    )

    $json = Get-OutputText $Lines
    if ([string]::IsNullOrWhiteSpace($json)) {
        throw "GitHub CLI returned empty JSON."
    }
    try {
        return ($json | ConvertFrom-Json)
    }
    catch {
        throw "GitHub CLI returned invalid JSON: $json"
    }
}

function Assert-ReleaseInputs {
    if ($Version -cne $ConfirmVersion) {
        throw "The two target version entries must match exactly."
    }
    if (-not (Test-StrictSemVer $Version)) {
        throw "Target version '$Version' is not a valid SemVer 2.0 value."
    }

    . (Join-Path $PSScriptRoot "get-preview-release-metadata.ps1")
    $metadata = Get-PreviewReleaseMetadata
    if ([string]$metadata.Version -cne $Version) {
        throw "Target version '$Version' does not match Directory.Build.props Version '$($metadata.Version)'."
    }

    $notesPath = Join-Path (Join-Path $repoRoot "docs\releases") "$Version.md"
    if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) {
        throw "Release notes were not found: $notesPath"
    }

    return $metadata
}

function Assert-SourceGuards {
    $branch = (Get-OutputText @(Invoke-Git @("branch", "--show-current"))).Trim()
    if ($branch -ne "toolbox") {
        throw "Release publication requires the toolbox branch; current branch: '$branch'."
    }

    $dirty = @(
        @(Invoke-Git @("status", "--porcelain=v1", "--untracked-files=all")) |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
    )
    if ($dirty.Count -ne 0) {
        throw "Release publication requires a clean worktree:`n$(Get-OutputText $dirty)"
    }

    $diffIssues = @(
        @(
            @(Invoke-Git @("diff", "--check"))
            @(Invoke-Git @("diff", "--cached", "--check"))
        ) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
    )
    if ($diffIssues.Count -ne 0) {
        throw "Git whitespace validation failed:`n$(Get-OutputText $diffIssues)"
    }

    [void](Invoke-Git @("fetch", "--no-tags", "origin", "toolbox"))
    $head = (Get-OutputText @(Invoke-Git @("rev-parse", "HEAD"))).Trim().ToLowerInvariant()
    $originHead = (Get-OutputText @(Invoke-Git @("rev-parse", "refs/remotes/origin/toolbox"))).Trim().ToLowerInvariant()
    if ($head -notmatch '^[0-9a-f]{40}$' -or $originHead -notmatch '^[0-9a-f]{40}$') {
        throw "Unable to resolve full HEAD and origin/toolbox commit SHAs."
    }
    if ($head -ne $originHead) {
        throw "HEAD is not synchronized with origin/toolbox. HEAD $head, origin/toolbox $originHead."
    }
    return $head
}

function Assert-RemoteGuards {
    [void](Invoke-Gh @("auth", "status"))

    $tag = "v$Version"
    $localTag = Invoke-GitProbe @("rev-parse", "--verify", "--quiet", "refs/tags/$tag")
    if ($localTag.ExitCode -eq 0) {
        throw "Tag $tag already exists locally."
    }
    if ((Get-OutputText $localTag.Output) -match '(?i)fatal|error') {
        # A normal --quiet not-found probe has no output.  Do not disguise a real
        # repository error as an absent tag.
        throw "Unable to verify that local tag $tag is absent.`n$(Get-OutputText $localTag.Output)"
    }

    $remoteTag = Invoke-GitProbe @("ls-remote", "--exit-code", "--refs", "origin", "refs/tags/$tag")
    if ($remoteTag.ExitCode -eq 0) {
        throw "Tag $tag already exists on origin."
    }
    if ($remoteTag.ExitCode -ne 2 -and $remoteTag.ExitCode -ne 1) {
        throw "Unable to verify that origin tag $tag is absent.`n$(Get-OutputText $remoteTag.Output)"
    }

    $release = Invoke-GhProbe @("release", "view", $tag, "--repo", $repository, "--json", "tagName")
    if ($release.ExitCode -eq 0) {
        throw "GitHub Release $tag already exists."
    }
    $releaseError = Get-OutputText $release.Output
    if ($releaseError -notmatch '(?i)(not found|404)') {
        throw "Unable to verify that GitHub Release $tag is absent.`n$releaseError"
    }
}

function Get-WorkflowRuns {
    param([AllowNull()][string]$Branch)

    $arguments = @(
        "run", "list", "--repo", $repository,
        "--workflow", $workflow, "--event", "workflow_dispatch",
        "--limit", "100",
        "--json", "databaseId,event,headBranch,headSha,status,conclusion,url"
    )
    if (-not [string]::IsNullOrWhiteSpace($Branch)) {
        $arguments += @("--branch", $Branch)
    }
    return @(ConvertFrom-GhJson (Invoke-Gh $arguments))
}

function Get-WorkflowRun {
    param([Parameter(Mandatory = $true)][long]$RunId)

    $run = @(ConvertFrom-GhJson (Invoke-Gh @(
        "run", "view", ([string]$RunId), "--repo", $repository,
        "--json", "databaseId,event,headBranch,headSha,status,conclusion,url")))
    if ($run.Count -ne 1) {
        throw "Expected one workflow run from gh run view $RunId; received $($run.Count)."
    }
    return $run[0]
}

function New-RunIdSet {
    param([AllowEmptyCollection()][object[]]$Runs)

    $ids = [Collections.Generic.HashSet[long]]::new()
    foreach ($run in $Runs) {
        [void]$ids.Add([long]$run.databaseId)
    }
    return $ids
}

function Assert-ExactWorkflowRun {
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$ExpectedHeadSha,
        [AllowNull()][string]$ExpectedBranch
    )

    if ([string]$Run.event -ne "workflow_dispatch") {
        throw "Workflow run $($Run.databaseId) event is '$($Run.event)', not workflow_dispatch."
    }
    if ([string]$Run.headSha -ne $ExpectedHeadSha) {
        throw "Workflow run $($Run.databaseId) head SHA $($Run.headSha) does not match $ExpectedHeadSha."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedBranch) -and
        -not [string]::IsNullOrWhiteSpace([string]$Run.headBranch) -and
        [string]$Run.headBranch -ne $ExpectedBranch) {
        throw "Workflow run $($Run.databaseId) ref is '$($Run.headBranch)', not '$ExpectedBranch'."
    }
}

function Resolve-NewWorkflowRun {
    param(
        [Parameter(Mandatory = $true)][Collections.Generic.HashSet[long]]$ExistingRunIds,
        [Parameter(Mandatory = $true)][string]$ExpectedHeadSha,
        [AllowNull()][string]$ExpectedBranch
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($DiscoveryTimeoutSeconds)
    do {
        $runs = @(Get-WorkflowRuns -Branch $ExpectedBranch)
        $candidates = @(
            $runs | Where-Object {
                -not $ExistingRunIds.Contains([long]$_.databaseId) -and
                [string]$_.event -eq "workflow_dispatch" -and
                [string]$_.headSha -eq $ExpectedHeadSha
            }
        )
        if ($candidates.Count -gt 1) {
            throw "More than one new exact-HEAD workflow run was found; refusing ambiguous evidence."
        }
        if ($candidates.Count -eq 1) {
            $resolved = Get-WorkflowRun ([long]$candidates[0].databaseId)
            Assert-ExactWorkflowRun -Run $resolved -ExpectedHeadSha $ExpectedHeadSha -ExpectedBranch $ExpectedBranch
            return $resolved
        }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            break
        }
        Start-Sleep -Seconds $PollSeconds
    } while ($true)

    throw "The newly dispatched exact-HEAD workflow run was not found within $DiscoveryTimeoutSeconds seconds."
}

function Start-WorkflowDispatch {
    param(
        [Parameter(Mandatory = $true)][string]$Ref,
        [Parameter(Mandatory = $true)][hashtable]$Fields,
        [Parameter(Mandatory = $true)][Collections.Generic.HashSet[long]]$ExistingRunIds,
        [Parameter(Mandatory = $true)][string]$ExpectedHeadSha,
        [AllowNull()][string]$ExpectedBranch
    )

    $arguments = @("workflow", "run", $workflow, "--repo", $repository, "--ref", $Ref)
    foreach ($fieldName in @("publish_release", "candidate_run_id")) {
        if ($Fields.ContainsKey($fieldName)) {
            # One array element is passed for each value; no shell expression is
            # assembled from the version or candidate id.
            $arguments += @("--field", ("$fieldName=" + [string]$Fields[$fieldName]))
        }
    }
    $dispatchOutput = @(Invoke-Gh $arguments)
    $dispatchText = Get-OutputText $dispatchOutput
    $urlMatch = [regex]::Match($dispatchText, '/actions/runs/(?<id>[0-9]+)')
    if ($urlMatch.Success) {
        $runId = [long]$urlMatch.Groups["id"].Value
        if ($ExistingRunIds.Contains($runId)) {
            throw "Dispatched workflow run $runId existed before dispatch; refusing stale evidence."
        }
        $run = Get-WorkflowRun $runId
        Assert-ExactWorkflowRun -Run $run -ExpectedHeadSha $ExpectedHeadSha -ExpectedBranch $ExpectedBranch
        return $run
    }

    return Resolve-NewWorkflowRun -ExistingRunIds $ExistingRunIds `
        -ExpectedHeadSha $ExpectedHeadSha -ExpectedBranch $ExpectedBranch
}

function Wait-ForSuccessfulWorkflowRun {
    param(
        [Parameter(Mandatory = $true)]$Run,
        [Parameter(Mandatory = $true)][string]$ExpectedHeadSha,
        [AllowNull()][string]$ExpectedBranch
    )

    $runId = [long]$Run.databaseId
    Write-Host "Watching workflow_dispatch run $runId (HEAD $ExpectedHeadSha)."
    $watchOutput = @(& gh run watch ([string]$runId) --repo $repository --exit-status 2>&1)
    $watchExitCode = $LASTEXITCODE
    $finalRun = Get-WorkflowRun $runId
    Assert-ExactWorkflowRun -Run $finalRun -ExpectedHeadSha $ExpectedHeadSha -ExpectedBranch $ExpectedBranch
    if ($watchExitCode -ne 0 -or [string]$finalRun.status -ne "completed" -or
        [string]$finalRun.conclusion -ne "success") {
        throw "Workflow run $runId did not complete successfully.`n$(Get-OutputText $watchOutput)"
    }
    return $finalRun
}

function Publish-Tag {
    param([Parameter(Mandatory = $true)][string]$Tag, [Parameter(Mandatory = $true)][string]$HeadSha)

    # The candidate is watched and proven successful before these two mutating calls.
    [void](Invoke-Git @("tag", "--annotate", $Tag, $HeadSha, "--message", "QingToolbox $Version"))
    [void](Invoke-Git @("push", "origin", ("refs/tags/$Tag")))
}

function Assert-PublishedRelease {
    param([Parameter(Mandatory = $true)]$Metadata, [Parameter(Mandatory = $true)][string]$Tag)

    $release = @(ConvertFrom-GhJson (Invoke-Gh @(
        "release", "view", $Tag, "--repo", $repository,
        "--json", "tagName,isDraft,isPrerelease,assets")))
    if ($release.Count -ne 1) {
        throw "Expected one GitHub Release response for $Tag."
    }
    $release = $release[0]
    if ([string]$release.tagName -ne $Tag -or [bool]$release.isDraft -or -not [bool]$release.isPrerelease) {
        throw "GitHub Release $Tag has unexpected tag, draft, or prerelease state."
    }

    $installerName = [string]$Metadata.InstallerFileName
    $requiredAssets = @($installerName, "$installerName.sha256")
    $assets = @($release.assets)
    $assetNames = @($assets | ForEach-Object { [string]$_.name })
    if ($assetNames.Count -ne 2) {
        throw "GitHub Release $Tag must contain exactly the installer and its SHA256 sidecar."
    }
    foreach ($assetName in $requiredAssets) {
        if ($assetNames -notcontains $assetName) {
            throw "GitHub Release $Tag is missing asset $assetName."
        }
        $asset = @($assets | Where-Object { [string]$_.name -ceq $assetName })[0]
        if ($null -eq $asset -or [long]$asset.size -le 0) {
            throw "GitHub Release asset $assetName is empty."
        }
    }
}

function Invoke-PreviewRelease {
    $metadata = Assert-ReleaseInputs
    $tag = "v$Version"

    $dryRun = $WhatIf -or $TestMode
    if ($dryRun) {
        Write-Host "WhatIf/TestMode: validated $Version against current release metadata and notes."
        Write-Host "WhatIf/TestMode: would require toolbox, clean, origin/toolbox sync, gh auth, and absent $tag release/tag."
        Write-Host "WhatIf/TestMode: would dispatch publish_release=false on toolbox, watch the exact candidate, then create and push $tag."
        Write-Host "WhatIf/TestMode: would dispatch --ref $tag with publish_release=true and candidate_run_id, watch it, and verify installer + same-name SHA256 assets."
        return
    }

    # No external command is reached until all input checks above have passed.
    $head = Assert-SourceGuards
    Assert-RemoteGuards

    $candidateBefore = @(Get-WorkflowRuns -Branch "toolbox")
    $candidateBeforeIds = New-RunIdSet $candidateBefore
    $candidateRun = Start-WorkflowDispatch -Ref "toolbox" `
        -Fields @{ publish_release = "false"; candidate_run_id = "" } `
        -ExistingRunIds $candidateBeforeIds -ExpectedHeadSha $head -ExpectedBranch "toolbox"
    $candidateRun = Wait-ForSuccessfulWorkflowRun -Run $candidateRun `
        -ExpectedHeadSha $head -ExpectedBranch "toolbox"

    # Tag creation/push is intentionally after candidate success and before the
    # publish_release=true dispatch that consumes candidate_run_id.
    Publish-Tag -Tag $tag -HeadSha $head

    $publishBefore = @(Get-WorkflowRuns -Branch $null)
    $publishBeforeIds = New-RunIdSet $publishBefore
    $publishRun = Start-WorkflowDispatch -Ref $tag `
        -Fields @{ publish_release = "true"; candidate_run_id = [string]$candidateRun.databaseId } `
        -ExistingRunIds $publishBeforeIds -ExpectedHeadSha $head -ExpectedBranch $tag
    $publishRun = Wait-ForSuccessfulWorkflowRun -Run $publishRun `
        -ExpectedHeadSha $head -ExpectedBranch $tag

    Assert-PublishedRelease -Metadata $metadata -Tag $tag
    Write-Host "Published and verified $tag (candidate run $($candidateRun.databaseId), publish run $($publishRun.databaseId))."
}

try {
    Invoke-PreviewRelease
}
catch {
    Write-Error -ErrorRecord $_
    exit 1
}
