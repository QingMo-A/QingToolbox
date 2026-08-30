[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ModuleId,
    [switch]$SkipBuild,
    [switch]$Smoke,
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tauri-packaging-path.ps1')

$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$tauriRoot = Join-Path $repoRoot 'QingToolbox.Tauri'
$resourceRoot = [IO.Path]::GetFullPath((Join-Path $tauriRoot 'src-tauri/resources/modules'))
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/tauri-modules'))
} else { [IO.Path]::GetFullPath($OutputDirectory) }

if ($ModuleId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') {
    throw "Invalid Tauri module id: $ModuleId"
}

$buildScripts = @{
    'qing.canary' = 'build-tauri-canary.ps1'
    'qing.launcher' = 'build-tauri-launcher.ps1'
    'qing.pdf' = 'build-tauri-pdf.ps1'
    'qing.qingtransfer' = 'build-tauri-transfer.ps1'
    'qing.texttools' = 'build-tauri-texttools.ps1'
    'qing.windowtopmost' = 'build-tauri-windowtopmost.ps1'
    'qing.powerguard' = 'build-tauri-powerguard.ps1'
    'qing.screenpin' = 'build-tauri-screenpin.ps1'
}
if (-not $SkipBuild) {
    $scriptName = $buildScripts[$ModuleId]
    if ([string]::IsNullOrWhiteSpace($scriptName)) {
        throw "No repository build script is registered for Tauri module '$ModuleId'. Build it into resources/modules first and pass -SkipBuild."
    }
    $scriptPath = Join-Path $PSScriptRoot $scriptName
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        throw "Module build script is missing: $scriptPath"
    }
    & $scriptPath
    if ($LASTEXITCODE -ne 0) { throw "Module build failed with exit code $LASTEXITCODE." }
}

$sourceRoot = [IO.Path]::GetFullPath((Join-Path $resourceRoot $ModuleId))
$resourcePrefix = $resourceRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $sourceRoot.StartsWith($resourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to package a module outside the bundled resource root: $sourceRoot"
}
if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
    throw "Built Tauri module directory was not found: $sourceRoot"
}
$sourceMetadata = Get-Item -LiteralPath $sourceRoot -Force
if (($sourceMetadata.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Refusing to package a reparse-point module directory: $sourceRoot"
}
$manifestPath = Join-Path $sourceRoot 'module.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Tauri module manifest is missing: $manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
foreach ($field in @('id', 'name', 'version', 'entry', 'runtimeType', 'runtimeIsolation', 'loadMode')) {
    if ([string]::IsNullOrWhiteSpace([string]$manifest.$field)) {
        throw "Tauri module manifest is missing '$field'."
    }
}
if ([string]$manifest.id -ne $ModuleId -or
    [string]$manifest.runtimeType -ne 'Process' -or
    [string]$manifest.runtimeIsolation -ne 'OutOfProcess') {
    throw "Tauri module manifest identity/runtime is invalid for '$ModuleId'."
}

# The version is presentation data in the manifest, but it also becomes part
# of the package filename. Keep it a bounded release token so a malformed
# manifest can never redirect the output outside the selected artifact root.
$version = ([string]$manifest.version).Trim()
if ($version -notmatch '^[A-Za-z0-9][A-Za-z0-9.+-]{0,63}$') {
    throw "Tauri module version is not a safe package token: $version"
}

$entryPath = [IO.Path]::GetFullPath((Join-Path $sourceRoot ([string]$manifest.entry)))
if (-not $entryPath.StartsWith($resourcePrefix + $ModuleId + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $entryPath -PathType Leaf)) {
    throw "Tauri module entry is missing or outside the module directory: $($manifest.entry)"
}
if ([string]$manifest.uiKind -eq 'Web') {
    if ([string]::IsNullOrWhiteSpace([string]$manifest.webEntry)) {
        throw 'Web Tauri modules must declare webEntry.'
    }
    $webEntryPath = [IO.Path]::GetFullPath((Join-Path $sourceRoot ([string]$manifest.webEntry)))
    if (-not $webEntryPath.StartsWith($resourcePrefix + $ModuleId + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $webEntryPath -PathType Leaf)) {
        throw "Tauri module webEntry is missing or outside the module directory: $($manifest.webEntry)"
    }
}

$files = @(Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | Sort-Object FullName)
if ($files.Count -eq 0) { throw "Tauri module directory is empty: $sourceRoot" }
$directories = @(Get-ChildItem -LiteralPath $sourceRoot -Directory -Recurse)
foreach ($directory in $directories) {
    if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to package a reparse-point directory: $($directory.FullName)"
    }
}
foreach ($file in $files) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to package a reparse-point file: $($file.FullName)"
    }
    $relative = (Get-TauriRelativePath -Root $sourceRoot -Path $file.FullName).Replace('\', '/')
    if ($relative -eq 'qmod.json') {
        throw 'The generated qmod.json must be the only package metadata file.'
    }
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$baseName = "$ModuleId-$version-tauri.qmod"
$outputPath = [IO.Path]::GetFullPath((Join-Path $outputRoot $baseName))
$outputPrefix = [IO.Path]::GetFullPath($outputRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $outputPath.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write a module package outside the selected artifact root: $outputPath"
}
$temporaryPath = "$outputPath.$PID.tmp"
if (Test-Path -LiteralPath $temporaryPath) { Remove-Item -LiteralPath $temporaryPath -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream = $null
$archive = $null
try {
    $stream = [IO.File]::Open($temporaryPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    $metadata = [ordered]@{
        schemaVersion = 1
        moduleId = [string]$manifest.id
        version = $version
        moduleApiVersion = 'tauri-process-v1'
        entryManifest = 'module.json'
    }
    $metadataBytes = [Text.Encoding]::UTF8.GetBytes(($metadata | ConvertTo-Json -Compress))
    $metadataEntry = $archive.CreateEntry('qmod.json', [IO.Compression.CompressionLevel]::Optimal)
    $metadataEntry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $metadataOutput = $metadataEntry.Open()
    try { $metadataOutput.Write($metadataBytes, 0, $metadataBytes.Length) } finally { $metadataOutput.Dispose() }

    foreach ($file in $files) {
        $relative = (Get-TauriRelativePath -Root $sourceRoot -Path $file.FullName).Replace('\', '/')
        $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $input = [IO.File]::OpenRead($file.FullName)
        $entryOutput = $entry.Open()
        try { $input.CopyTo($entryOutput) } finally { $entryOutput.Dispose(); $input.Dispose() }
    }
}
finally {
    if ($archive) { $archive.Dispose() }
    if ($stream) { $stream.Dispose() }
}

if (Test-Path -LiteralPath $outputPath) { Remove-Item -LiteralPath $outputPath -Force }
[IO.File]::Move($temporaryPath, $outputPath)
$hash = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToUpperInvariant()
"$hash  $baseName" | Set-Content -LiteralPath "$outputPath.sha256" -Encoding ASCII

if ($Smoke) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($outputPath)
    try {
        $entries = @($zip.Entries)
        if ($entries.Count -ne ($files.Count + 1)) {
            throw "Package entry count mismatch: expected $($files.Count + 1), got $($entries.Count)."
        }
        if ($entries[0].FullName -cne 'qmod.json' -or
            -not ($entries | Where-Object FullName -ceq 'module.json')) {
            throw 'Tauri module package metadata or module.json is missing.'
        }
        $metadataEntry = $entries | Where-Object FullName -ceq 'qmod.json'
        $reader = [IO.StreamReader]::new($metadataEntry.Open(), [Text.Encoding]::UTF8, $false)
        try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ([int]$metadata.schemaVersion -ne 1 -or
            [string]$metadata.moduleId -cne [string]$manifest.id -or
            [string]$metadata.version -cne $version -or
            [string]$metadata.moduleApiVersion -cne 'tauri-process-v1' -or
            [string]$metadata.entryManifest -cne 'module.json') {
            throw 'Generated qmod.json does not match the module manifest.'
        }
    } finally {
        $zip.Dispose()
    }
    Write-Host 'Package smoke: passed'
}

Write-Host "Tauri module package: $outputPath"
Write-Host "SHA256:              $hash"
Write-Host "Entries:              $($files.Count + 1)"
