[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ModuleId,
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$MinimumHostVersion = '0.3.0-alpha',
    [Parameter(Mandatory = $true)][string]$NotesZh,
    [Parameter(Mandatory = $true)][string]$NotesEn
)
# Read-only GitHub operations: this records an already-published artifact;
# it does not upload files, create a release, commit, or push anything.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$index = Get-Content -LiteralPath (Join-Path $root 'modules/index.json') -Raw | ConvertFrom-Json
if ($index.schemaVersion -ne 2 -or $ModuleId -notmatch '^qing\.[a-z0-9][a-z0-9.-]{0,100}$' -or $Tag -notmatch '^modules-[a-z0-9-]+-v[A-Za-z0-9.+-]+$') { throw 'Invalid catalog, module id or release tag.' }
$item = $index.modules.PSObject.Properties[$ModuleId]
if ($null -eq $item) { throw 'Module is not registered in the official index.' }
$updatePath = [IO.Path]::GetFullPath((Join-Path (Join-Path $root 'modules') $item.Value.updateManifest))
if (-not $updatePath.StartsWith((Join-Path $root 'modules') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe update metadata path.' }
$release = (& gh api "repos/QingMo-A/QingToolbox/releases/tags/$Tag" | Out-String) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $release.draft -or $release.tag_name -cne $Tag -or $null -eq $release.published_at) { throw 'Published GitHub release not found.' }
$assets = @($release.assets | Where-Object { $_.name -match ('^' + [Regex]::Escape($ModuleId) + '-[A-Za-z0-9.+-]+-tauri\.qmod$') })
if ($assets.Count -ne 1) { throw 'Exactly one matching native module package is required.' }
$asset = $assets[0]
$fileName = [string]$asset.name
$url = [string]$asset.browser_download_url
if ($url -cne "https://github.com/QingMo-A/QingToolbox/releases/download/$Tag/$fileName") { throw 'Unexpected package URL.' }
$directory = Join-Path $root ('artifacts/release-verification/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory -Force | Out-Null
& gh release download $Tag --repo QingMo-A/QingToolbox --pattern $fileName --pattern "$fileName.sha256" --dir $directory
if ($LASTEXITCODE -ne 0) { throw 'Unable to download the published package and checksum.' }
$packagePath = Join-Path $directory $fileName
$size = (Get-Item -LiteralPath $packagePath).Length
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$sidecar = (Get-Content -LiteralPath "$packagePath.sha256" -Raw).Trim()
if ($size -ne [long]$asset.size -or $sidecar -cnotmatch ('^[a-fA-F0-9]{64}\s+\*?' + [Regex]::Escape($fileName) + '$') -or $sidecar.Substring(0,64).ToLowerInvariant() -cne $hash) { throw 'Published package size/checksum verification failed.' }
if ($asset.digest -and [string]$asset.digest -cne "sha256:$hash") { throw 'GitHub digest differs from downloaded package.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $data = @{}
    foreach ($name in @('qmod.json','module.json')) {
        $entries = @($archive.Entries | Where-Object FullName -CEQ $name)
        if ($entries.Count -ne 1 -or $entries[0].Length -gt 65536) { throw "Invalid package identity file: $name" }
        $reader = [IO.StreamReader]::new($entries[0].Open())
        try { $data[$name] = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    }
    $qmod = $data['qmod.json']; $module = $data['module.json']
    if ($qmod.moduleId -cne $ModuleId -or $module.id -cne $ModuleId -or $qmod.version -cne $module.version -or $qmod.moduleApiVersion -cne 'tauri-process-v1' -or $module.runtimeType -cne 'Process' -or $module.runtimeIsolation -cne 'OutOfProcess' -or $fileName -cne "$ModuleId-$($module.version)-tauri.qmod") { throw 'The published package is not the requested native module.' }
    # Native v1 packages published before apiVersion was explicit imply API 1,
    # matching the Rust importer's backwards-compatible default.
    $api = if ($module.PSObject.Properties['apiVersion']) { [int]$module.apiVersion } else { 1 }
    if ($api -lt 1 -or ($qmod.PSObject.Properties['apiVersion'] -and [int]$qmod.apiVersion -ne $api)) { throw 'Package API declarations differ.' }
} finally { $archive.Dispose() }
$entry = [ordered]@{
    version = [string]$module.version
    channel = if ($release.prerelease) { 'preview' } else { 'stable' }
    moduleProfile = 'tauri-process-v1'; apiVersion = $api
    platform = 'windows'; architecture = 'x64'
    minimumHostVersion = $MinimumHostVersion; maximumHostVersionExclusive = $null
    publishedAt = ([DateTimeOffset]$release.published_at).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", [Globalization.CultureInfo]::InvariantCulture)
    package = [ordered]@{ fileName = $fileName; url = $url; size = $size; sha256 = $hash }
    releaseNotes = [ordered]@{ 'zh-CN' = $NotesZh; 'en-US' = $NotesEn }
}
$current = Get-Content -LiteralPath $updatePath -Raw | ConvertFrom-Json
$existing = @($current.releases | Where-Object version -CEQ $entry.version)
if ($existing.Count -gt 0) {
    if ($existing.Count -ne 1 -or $existing[0].package.sha256 -cne $hash -or [long]$existing[0].package.size -ne $size -or $existing[0].package.url -cne $url) { throw 'Refusing to replace an existing version with different package bytes.' }
    Write-Host "Already recorded and reverified: $ModuleId $($entry.version)"
    return
}
$current.releases = @($entry) + @($current.releases)
# Validate the candidate before replacing the real metadata file.
$candidate = Join-Path $directory 'update.json'
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($candidate, (($current | ConvertTo-Json -Depth 12) + "`n"), $utf8)
& node --input-type=module -e 'import { readFileSync, writeFileSync } from "node:fs"; import { pathToFileURL } from "node:url"; const [path,id,validator] = process.argv.slice(1); const {validateUpdate,compareVersions} = await import(pathToFileURL(validator)); const data=JSON.parse(readFileSync(path,"utf8")); data.releases.sort((a,b)=>compareVersions(b.version,a.version)); validateUpdate(data,id); writeFileSync(path,JSON.stringify(data,null,2)+"\n");' $candidate $ModuleId (Join-Path $PSScriptRoot 'module-catalog.mjs')
if ($LASTEXITCODE -ne 0) { throw 'Candidate publication record is invalid.' }
[IO.File]::WriteAllText($updatePath, [IO.File]::ReadAllText($candidate), $utf8)
Write-Host "Verified and recorded: $ModuleId $($entry.version); $size bytes; SHA256 $hash"
