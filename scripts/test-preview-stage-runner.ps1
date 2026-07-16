[CmdletBinding()]
param(
    [ValidateSet("Contracts", "ExitSeven")]
    [string]$Mode = "Contracts"
)

if ($Mode -eq "ExitSeven") { exit 7 }

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "preview-stage-runner.ps1")

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) { throw $Message }
}

$exitFailure = $null
try {
    Invoke-CheckedStage -StageName "Exit seven contract" -Action {
        & $PSCommandPath -Mode ExitSeven
    }
}
catch { $exitFailure = $_ }
Assert-Condition ($null -ne $exitFailure) "Exit-code failure was not detected."
Assert-Condition ($exitFailure.ToString().Contains("Exit seven contract")) `
    "Exit-code failure omitted the stage name."
Assert-Condition ($exitFailure.ToString().Contains("ExitCode=7")) `
    "Exit-code failure omitted exit code 7."

$throwFailure = $null
try {
    Invoke-CheckedStage -StageName "Throw contract" -Action { throw "contract boom" }
}
catch { $throwFailure = $_ }
Assert-Condition ($null -ne $throwFailure) "Thrown failure was not detected."
Assert-Condition ($throwFailure.ToString().Contains("Throw contract")) `
    "Thrown failure omitted the stage name."

$global:LASTEXITCODE = 19
$output = @(Invoke-CheckedStageWithOutput -StageName "Output contract" -Action {
    "prepared-path"
})
Assert-Condition ($output.Count -eq 1 -and $output[0] -eq "prepared-path") `
    "Successful stage output was not preserved."

$nextStageExecuted = $false
try {
    Invoke-CheckedStage -StageName "Stop pipeline contract" -Action {
        & (Join-Path $PSHOME "powershell.exe") -NoProfile -Command "exit 3"
    }
    $nextStageExecuted = $true
}
catch { }
Assert-Condition (-not $nextStageExecuted) "A stage after failure was executed."

$global:LASTEXITCODE = 0
Write-Host "Preview stage runner contracts passed."
