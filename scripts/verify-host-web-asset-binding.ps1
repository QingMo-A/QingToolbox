[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PayloadRoot
)

$ErrorActionPreference = 'Stop'

function Test-ByteSequence {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Buffer,
        [Parameter(Mandatory = $true)][byte[]]$Sequence
    )

    if ($Sequence.Length -eq 0 -or $Sequence.Length -gt $Buffer.Length) {
        return $false
    }

    $lastStart = $Buffer.Length - $Sequence.Length
    for ($start = 0; $start -le $lastStart; $start++) {
        if ($Buffer[$start] -ne $Sequence[0]) {
            continue
        }

        $matches = $true
        for ($offset = 1; $offset -lt $Sequence.Length; $offset++) {
            if ($Buffer[$start + $offset] -ne $Sequence[$offset]) {
                $matches = $false
                break
            }
        }

        if ($matches) {
            return $true
        }
    }

    return $false
}

function Test-AssemblyContainsText {
    param(
        [Parameter(Mandatory = $true)][byte[]]$AssemblyBytes,
        [Parameter(Mandatory = $true)][string]$Value
    )

    return (Test-ByteSequence $AssemblyBytes ([Text.Encoding]::UTF8.GetBytes($Value))) -or
        (Test-ByteSequence $AssemblyBytes ([Text.Encoding]::Unicode.GetBytes($Value)))
}

$root = [IO.Path]::GetFullPath($PayloadRoot)
$web = Join-Path $root 'WebUI'
$shell = Join-Path $root 'QingToolbox.Shell.dll'
& (Join-Path $PSScriptRoot 'verify-packaged-web-assets.ps1') -WebUIRoot $web
if (-not (Test-Path -LiteralPath $shell -PathType Leaf)) {
    throw 'Packaged Shell assembly is missing.'
}

$manifestPath = Join-Path $web 'qing-web-assets.json'
$manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$actualHash = (Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$assetId = [string]$manifest.assetBuildId
$assemblyBytes = [IO.File]::ReadAllBytes($shell)
if (-not (Test-AssemblyContainsText $assemblyBytes $actualHash) -or
    -not (Test-AssemblyContainsText $assemblyBytes $assetId)) {
    throw 'Packaged WebUI identity is not anchored to the Shell assembly.'
}

Write-Host "Verified host/WebUI binding; buildId=$assetId; manifestSha256=$actualHash"
