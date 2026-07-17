[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Join-Path $env:TEMP ("QingToolbox-host-manifest-" + [guid]::NewGuid().ToString("N"))
try {
    New-Item -ItemType Directory -Path (Join-Path $root "Resources") -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $root "QingToolbox.Shell.exe") -Value "host" -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $root "Resources\en-US.json") -Value "{}" -Encoding ASCII
    & (Join-Path $PSScriptRoot "write-host-payload-manifest.ps1") -PayloadDirectory $root
    $manifest = Get-Content -LiteralPath (Join-Path $root "host-payload.manifest.json") -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or @($manifest.entries).Count -ne 2) { throw "Host manifest contract failed." }
    $paths = @($manifest.entries.relativePath)
    if (@(Compare-Object $paths @($paths | Sort-Object)).Count -ne 0) { throw "Host manifest paths are not deterministic." }
    function Assert-SafeRelativePath([string]$path) {
        if ([string]::IsNullOrWhiteSpace($path) -or [IO.Path]::IsPathRooted($path) -or
            $path -match '^[A-Za-z]:' -or $path -match '^\\' -or
            $path -match '(^|[\\/])\.\.([\\/]|$)' -or $path -match ':') { throw "Unsafe obsolete path: $path" }
    }
    foreach ($unsafe in @('..\escape.dll', 'C:\escape.dll', '\\server\share.dll', 'file.dll:stream')) {
        $rejected = $false; try { Assert-SafeRelativePath $unsafe } catch { $rejected = $true }
        if (-not $rejected) { throw "Unsafe obsolete path was accepted: $unsafe" }
    }
    Write-Host "Host payload manifest and obsolete-path contracts passed."
}
finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
