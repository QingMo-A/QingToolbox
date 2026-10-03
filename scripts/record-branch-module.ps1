[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ModuleId,
    [Parameter(Mandatory = $true)][string]$PackageCommit,
    [string]$MinimumHostVersion = '0.3.0-alpha',
    [Parameter(Mandatory = $true)][string]$NotesZh,
    [Parameter(Mandatory = $true)][string]$NotesEn
)
# Record an already-committed, versioned package. No Release, Tag or push.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ($ModuleId -notmatch '^qing\.[a-z0-9][a-z0-9.-]{0,100}$' -or $PackageCommit -cnotmatch '^[a-f0-9]{40}$') {
    throw 'Module identity and full immutable package commit are required.'
}
$index = Get-Content -LiteralPath (Join-Path $root 'modules/index.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$item = $index.modules.PSObject.Properties[$ModuleId]
if ($index.schemaVersion -ne 2 -or $null -eq $item) { throw 'Module is not registered in the native catalog.' }
$directory = ([string]$item.Value.moduleManifest) -replace '/module\.json$', ''
if ($directory -notmatch '^[A-Za-z0-9._/-]+$' -or @($directory.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count) {
    throw 'Unsafe module directory.'
}
$moduleRoot = Join-Path (Join-Path $root 'modules') $directory
$manifest = Get-Content -LiteralPath (Join-Path $moduleRoot 'module.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.id -cne $ModuleId -or $manifest.runtimeType -cne 'Process' -or $manifest.runtimeIsolation -cne 'OutOfProcess' -or $manifest.apiVersion -ne 1) {
    throw 'Module identity/API/runtime mismatch.'
}
$fileName = "$ModuleId-$($manifest.version)-tauri.qmod"
if ($fileName -notmatch '^[A-Za-z0-9._+-]+\.qmod$') { throw 'Unsafe package filename.' }
$relative = "modules/$directory/packages/$fileName"
$packagePath = Join-Path $root $relative
$size = (Get-Item -LiteralPath $packagePath).Length
if ($size -lt 1 -or $size -gt 256MB) { throw 'Package size is outside the supported bounds.' }
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$sidecar = (Get-Content -LiteralPath "$packagePath.sha256" -Raw).Trim()
if ($sidecar -cnotmatch ('^[a-fA-F0-9]{64}\s+' + [Regex]::Escape($fileName) + '$') -or $sidecar.Substring(0,64).ToLowerInvariant() -cne $hash) {
    throw 'Package checksum verification failed.'
}
$blob = (& git -C $root rev-parse "${PackageCommit}:$relative").Trim()
if ($LASTEXITCODE -ne 0) { throw 'Package is not present in the selected commit.' }
$localBlob = (& git -C $root hash-object -- $packagePath).Trim()
if ($LASTEXITCODE -ne 0 -or $blob -cne $localBlob) { throw 'Local package bytes differ from the committed package.' }
& git -C $root merge-base --is-ancestor $PackageCommit HEAD
if ($LASTEXITCODE -ne 0) { throw 'The package commit is not in this branch history.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $data = @{}
    foreach ($name in @('qmod.json', 'module.json')) {
        $entries = @($archive.Entries | Where-Object FullName -CEQ $name)
        if ($entries.Count -ne 1 -or $entries[0].Length -gt 65536) { throw "Invalid package identity: $name" }
        $reader = [IO.StreamReader]::new($entries[0].Open())
        try { $data[$name] = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    }
    $qmod = $data['qmod.json']; $packed = $data['module.json']
    if ($qmod.moduleId -cne $ModuleId -or $packed.id -cne $ModuleId -or
        $qmod.version -cne $manifest.version -or $packed.version -cne $manifest.version -or
        $qmod.moduleApiVersion -cne 'tauri-process-v1' -or $qmod.apiVersion -ne 1 -or $packed.apiVersion -ne 1 -or
        $packed.runtimeType -cne 'Process' -or $packed.runtimeIsolation -cne 'OutOfProcess') {
        throw 'Package identity, version or API mismatch.'
    }
} finally { $archive.Dispose() }
$entry = [ordered]@{
    version = [string]$manifest.version; channel = 'preview'; moduleProfile = 'tauri-process-v1'; apiVersion = 1
    platform = 'windows'; architecture = 'x64'
    minimumHostVersion = $MinimumHostVersion; maximumHostVersionExclusive = $null
    publishedAt = [DateTimeOffset]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
    package = [ordered]@{
        fileName = $fileName
        url = "https://raw.githubusercontent.com/QingMo-A/QingToolbox/$PackageCommit/$relative"
        size = $size; sha256 = $hash
    }
    releaseNotes = [ordered]@{ 'zh-CN' = $NotesZh; 'en-US' = $NotesEn }
}
$updatePath = Join-Path $moduleRoot 'update.json'
$current = Get-Content -LiteralPath $updatePath -Raw -Encoding UTF8 | ConvertFrom-Json
$existing = @($current.releases | Where-Object version -CEQ $entry.version)
if ($existing.Count -gt 0) {
    if ($existing.Count -ne 1 -or $existing[0].package.sha256 -cne $hash -or [long]$existing[0].package.size -ne $size -or $existing[0].package.url -cne $entry.package.url) {
        throw 'Refusing to replace a published version with different package bytes/source.'
    }
    Write-Host "Already recorded: $ModuleId $($entry.version)"
    return
}
$current.releases = @($entry) + @($current.releases)
$candidateRoot = Join-Path $root 'artifacts/branch-publication'
New-Item -ItemType Directory -Path $candidateRoot -Force | Out-Null
$candidate = Join-Path $candidateRoot "$ModuleId-update.json"
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($candidate, (($current | ConvertTo-Json -Depth 12) + "`n"), $utf8)
& node (Join-Path $PSScriptRoot 'module-catalog.mjs') --validate-update $candidate $ModuleId $directory
if ($LASTEXITCODE -ne 0) { throw 'Candidate update record is invalid.' }
[IO.File]::WriteAllText($updatePath, [IO.File]::ReadAllText($candidate), $utf8)
Write-Host "Recorded branch package: $ModuleId $($entry.version); $size bytes; SHA256 $hash; commit $PackageCommit"
