[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModuleName,
    [string]$QingToolboxHostRoot = "..\QingToolbox-toolbox",
    [ValidateSet("Debug", "Release")][string]$Configuration = "Release",
    [string]$OutputDirectory,
    [string]$SmokeTestProject
)
$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$moduleRoot = Join-Path $repo "modules\$ModuleName"
$manifestPath = Join-Path $moduleRoot "module.json"
$hostRoot = if ([IO.Path]::IsPathRooted($QingToolboxHostRoot)) { [IO.Path]::GetFullPath($QingToolboxHostRoot) } else { [IO.Path]::GetFullPath((Join-Path $repo $QingToolboxHostRoot)) }
$output = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $repo "artifacts\modules" } else { [IO.Path]::GetFullPath($OutputDirectory) }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.id -notmatch '^[a-z0-9]+(?:[._-][a-z0-9]+)+$') { throw "Invalid module id: $($manifest.id)" }
if ($manifest.version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') { throw "Invalid SemVer: $($manifest.version)" }
$assemblyVersion = (($manifest.version -split '-')[0]) + ".0"
$project = Join-Path $moduleRoot "QingToolbox.Modules.$ModuleName.csproj"
$build = Join-Path $moduleRoot "bin\$Configuration\net10.0-windows"
$assemblyName = "QingToolbox.Modules.$ModuleName.dll"
$staging = Join-Path ([IO.Path]::GetTempPath()) ("QingToolbox-$ModuleName-" + [Guid]::NewGuid().ToString("N"))
$temporaryPackage = Join-Path ([IO.Path]::GetTempPath()) ("QingToolbox-package-" + [Guid]::NewGuid().ToString("N") + ".zip")
$qmod = Join-Path $output "$($manifest.id)-$($manifest.version).qmod"
$sidecar = "$qmod.sha256"
try {
    dotnet build $project -c $Configuration "/p:QingToolboxHostRoot=$hostRoot" "/p:Version=$($manifest.version)" "/p:AssemblyVersion=$assemblyVersion" "/p:FileVersion=$assemblyVersion"
    if ($LASTEXITCODE -ne 0) { throw "$ModuleName build failed." }
    $dllVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $build $assemblyName)).Version.ToString(3)
    if ($dllVersion -ne $manifest.version) { throw "DLL version $dllVersion does not match manifest $($manifest.version)." }
    New-Item -ItemType Directory -Force -Path (Join-Path $staging "i18n"),$output | Out-Null
    foreach ($name in @("module.json", "icon.svg", $assemblyName)) { Copy-Item -LiteralPath (Join-Path $build $name) -Destination (Join-Path $staging $name) }
    foreach ($culture in @("en-US", "zh-CN")) { Copy-Item -LiteralPath (Join-Path $build "i18n\$culture.json") -Destination (Join-Path $staging "i18n\$culture.json") }
    Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $temporaryPackage
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($temporaryPackage)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
        foreach ($required in @("module.json",$assemblyName,"icon.svg","i18n/en-US.json","i18n/zh-CN.json")) { if ($required -notin $entries) { throw "Missing package entry: $required" } }
        if ($entries | Where-Object { $_ -match '(^|/)(QingToolbox\.(Abstractions|Shell|Core).*|bin|obj)(/|$)|\.pdb$|\.cs$|\.csproj$|settings\.json$|\.log$' }) { throw "Forbidden package content detected." }
    } finally { $archive.Dispose() }
    Remove-Item -LiteralPath $qmod,$sidecar -Force -ErrorAction SilentlyContinue
    Move-Item -LiteralPath $temporaryPackage -Destination $qmod
    $hash = (Get-FileHash -LiteralPath $qmod -Algorithm SHA256).Hash
    Set-Content -LiteralPath $sidecar -Value "$hash  $(Split-Path $qmod -Leaf)" -Encoding ASCII
    if (-not [string]::IsNullOrWhiteSpace($SmokeTestProject)) {
        dotnet run --project (Join-Path $repo $SmokeTestProject) -c $Configuration "/p:QingToolboxHostRoot=$hostRoot" -- --package $qmod
        if ($LASTEXITCODE -ne 0) { throw "$ModuleName package smoke test failed." }
    }
    Write-Host "$ModuleName package: $qmod"; Write-Host "SHA256: $hash"
} catch {
    Remove-Item -LiteralPath $qmod,$sidecar -Force -ErrorAction SilentlyContinue
    throw
} finally {
    Remove-Item -LiteralPath $staging,$temporaryPackage -Recurse -Force -ErrorAction SilentlyContinue
}
