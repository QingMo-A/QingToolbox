[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$IsccPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$dependenciesPath = Join-Path $repoRoot "installer\dependencies.psd1"
$downloadPath = $null

try {
    $resolvedIsccPath = [System.IO.Path]::GetFullPath($IsccPath)
    if (-not (Test-Path -LiteralPath $resolvedIsccPath -PathType Leaf)) {
        throw "ISCC.exe was not found: $resolvedIsccPath"
    }
    if (-not (Test-Path -LiteralPath $dependenciesPath -PathType Leaf)) {
        throw "Installer dependency configuration was not found: $dependenciesPath"
    }

    $innoRoot = Split-Path -Parent $resolvedIsccPath
    $defaultMessages = Join-Path $innoRoot "Default.isl"
    if (-not (Test-Path -LiteralPath $defaultMessages -PathType Leaf)) {
        throw "Default Inno Setup messages were not found: $defaultMessages"
    }

    $dependencies = Import-PowerShellDataFile -LiteralPath $dependenciesPath
    $translation = $dependencies.ChineseSimplifiedMessages
    $expectedHash = ([string]$translation.Sha256).ToUpperInvariant()
    if ($expectedHash -notmatch '^[0-9A-F]{64}$') {
        throw "Pinned ChineseSimplified.isl SHA256 is invalid: $expectedHash"
    }

    $languageDirectory = Join-Path $innoRoot "Languages"
    New-Item -ItemType Directory -Path $languageDirectory -Force | Out-Null
    $languageFile = Join-Path $languageDirectory "ChineseSimplified.isl"

    if (Test-Path -LiteralPath $languageFile -PathType Leaf) {
        Write-Host "Validating existing ChineseSimplified.isl without replacing it."
    }
    else {
        $downloadPath = Join-Path $languageDirectory (
            "ChineseSimplified.{0}.download" -f [guid]::NewGuid().ToString("N"))
        Write-Host "Downloading ChineseSimplified.isl from the pinned official source."
        Invoke-WebRequest -Uri ([string]$translation.Uri) -OutFile $downloadPath
        $downloadHash = (Get-FileHash -LiteralPath $downloadPath `
            -Algorithm SHA256).Hash
        if ($downloadHash -ne $expectedHash) {
            throw "Downloaded ChineseSimplified.isl SHA256 mismatch. " +
                  "Expected $expectedHash, actual $downloadHash."
        }
        if (-not (Select-String -LiteralPath $downloadPath `
            -Pattern ([string]$translation.ContentPattern) -Quiet)) {
            throw "Downloaded ChineseSimplified.isl failed content validation."
        }
        Move-Item -LiteralPath $downloadPath -Destination $languageFile
        $downloadPath = $null
    }

    $actualHash = (Get-FileHash -LiteralPath $languageFile -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) {
        throw "Existing ChineseSimplified.isl differs from the pinned file. " +
              "Expected $expectedHash, actual $actualHash. The file was not overwritten."
    }
    if (-not (Select-String -LiteralPath $languageFile `
        -Pattern ([string]$translation.ContentPattern) -Quiet)) {
        throw "ChineseSimplified.isl failed content validation: $languageFile"
    }

    Write-Host "ChineseSimplified.isl SHA256 verified: $actualHash"
    Write-Output $resolvedIsccPath
}
catch {
    Write-Error -ErrorRecord $_
    exit 1
}
finally {
    if ($null -ne $downloadPath -and (Test-Path -LiteralPath $downloadPath)) {
        Remove-Item -LiteralPath $downloadPath -Force
    }
}
