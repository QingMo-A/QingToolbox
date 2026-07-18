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
    $currentPath = Join-Path $root 'current.json'
    $previousPath = Join-Path $root 'previous.json'
    $includePath = Join-Path $root 'obsolete.iss'
    [ordered]@{ schemaVersion = 1; entries = @([ordered]@{ relativePath = 'QingToolbox.Shell.exe' }) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $currentPath -Encoding UTF8
    [ordered]@{ schemaVersion = 1; entries = @(
        [ordered]@{ relativePath = 'QingToolbox.Shell.exe' },
        [ordered]@{ relativePath = 'docs/releases/old.md' }) } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $previousPath -Encoding UTF8
    & (Join-Path $PSScriptRoot 'write-obsolete-host-files-include.ps1') `
        -PreviousManifestPath $previousPath -CurrentManifestPath $currentPath -OutputPath $includePath
    $include = Get-Content -LiteralPath $includePath -Raw
    if ($include -match '\[InstallDelete\]' -or
        $include -notmatch "SafeDeleteObsoleteHostFile\('docs\\releases\\old\.md'\)" -or
        $include -notmatch "SafeRemoveObsoleteHostDirectory\('docs\\releases'\)") {
        throw 'Obsolete cleanup did not generate guarded exact-path installer code.'
    }
    foreach ($unsafe in @('..\escape.dll', 'C:\escape.dll', '\\server\share.dll', 'file.dll:stream')) {
        [ordered]@{ schemaVersion = 1; entries = @([ordered]@{ relativePath = $unsafe }) } |
            ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $previousPath -Encoding UTF8
        $rejected = $false
        try {
            & (Join-Path $PSScriptRoot 'write-obsolete-host-files-include.ps1') `
                -PreviousManifestPath $previousPath -CurrentManifestPath $currentPath -OutputPath $includePath
        }
        catch { $rejected = $true }
        if (-not $rejected) { throw "Unsafe obsolete path was accepted: $unsafe" }
    }
    Write-Host "Host payload manifest and obsolete-path contracts passed."
}
finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
