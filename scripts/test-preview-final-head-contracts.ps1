[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Convert-GhRunList {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Lines)

    $items = (($Lines -join "`n") | ConvertFrom-Json)
    return @($items | ForEach-Object { $_ })
}

$cases = @(
    @{ Json = @('[]'); Expected = @() },
    @{ Json = @('[{"databaseId":1}]'); Expected = @(1L) },
    @{ Json = @('[{"databaseId":1},', '{"databaseId":2}]'); Expected = @(1L, 2L) }
)

foreach ($case in $cases) {
    $actual = @(Convert-GhRunList -Lines $case.Json | ForEach-Object { [long]$_.databaseId })
    if ($actual.Count -ne $case.Expected.Count -or
        (Compare-Object -ReferenceObject $case.Expected -DifferenceObject $actual)) {
        throw "GitHub run JSON enumeration mismatch for: $($case.Json -join '')"
    }
}

$beforeIds = [Collections.Generic.HashSet[long]]::new()
[void]$beforeIds.Add(41L)
if (-not $beforeIds.Contains(41L)) { throw 'A pre-dispatch run ID was not rejected.' }
if ($beforeIds.Contains(42L)) { throw 'A new run ID was incorrectly treated as pre-existing.' }

$verifierPath = Join-Path $PSScriptRoot 'verify-preview-final-head.ps1'
$verifierText = Get-Content -LiteralPath $verifierPath -Raw
if ($verifierText -notmatch '\$beforeIds\.Contains\(\[long\]\$runId\)') {
    throw 'Final HEAD verifier does not reject a run ID present before dispatch.'
}

Write-Host 'Preview final HEAD PowerShell contracts passed.'
