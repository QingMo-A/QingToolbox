Set-StrictMode -Version Latest

function New-PreviewStageErrorRecord {
    param(
        [Parameter(Mandatory = $true)][string]$StageName,
        [Parameter(Mandatory = $true)]
        [System.Management.Automation.ErrorRecord]$ErrorRecord
    )

    $ErrorRecord.ErrorDetails = [System.Management.Automation.ErrorDetails]::new(
        "Preview stage '$StageName' failed: $($ErrorRecord.Exception.Message)")
    return $ErrorRecord
}

function Invoke-CheckedStage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$StageName,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    $global:LASTEXITCODE = 0
    try {
        & $Action
        $stageSucceeded = $?
        $stageExitCode = $global:LASTEXITCODE
    }
    catch {
        throw (New-PreviewStageErrorRecord -StageName $StageName -ErrorRecord $_)
    }

    if (-not $stageSucceeded -or $stageExitCode -ne 0) {
        throw "Preview stage '$StageName' failed. " +
              "PowerShellSuccess=$stageSucceeded; ExitCode=$stageExitCode."
    }
}

function Invoke-CheckedStageWithOutput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$StageName,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )

    $global:LASTEXITCODE = 0
    try {
        $stageOutput = @(& $Action)
        $stageSucceeded = $?
        $stageExitCode = $global:LASTEXITCODE
    }
    catch {
        throw (New-PreviewStageErrorRecord -StageName $StageName -ErrorRecord $_)
    }

    if (-not $stageSucceeded -or $stageExitCode -ne 0) {
        throw "Preview stage '$StageName' failed. " +
              "PowerShellSuccess=$stageSucceeded; ExitCode=$stageExitCode."
    }

    return $stageOutput
}
