[CmdletBinding()]
param(
    [string]$QingToolboxHostRoot = "..\QingToolbox-toolbox",
    [ValidateSet("Debug", "Release")][string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$moduleRoot = Join-Path $repoRoot "modules\WindowTopmost"
$hostRoot = if ([IO.Path]::IsPathRooted($QingToolboxHostRoot)) {
    [IO.Path]::GetFullPath($QingToolboxHostRoot)
} else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $QingToolboxHostRoot))
}
$project = Join-Path $moduleRoot "QingToolbox.Modules.WindowTopmost.csproj"
$output = Join-Path $moduleRoot "bin\$Configuration\net10.0-windows"
$artifacts = Join-Path $repoRoot "artifacts\modules"
$staging = Join-Path $env:TEMP ("QingToolbox-WindowTopmost-" + [guid]::NewGuid().ToString("N"))
$qmod = Join-Path $artifacts "QingToolbox.WindowTopmost-0.1.0.qmod"
$zip = [IO.Path]::ChangeExtension($qmod, ".zip")

try {
    dotnet build $project -c $Configuration "-p:QingToolboxHostRoot=$hostRoot"
    if ($LASTEXITCODE -ne 0) { throw "WindowTopmost build failed." }
    New-Item -ItemType Directory -Path $staging,$artifacts -Force | Out-Null
    foreach ($name in @("QingToolbox.Modules.WindowTopmost.dll", "module.json", "icon.svg")) {
        $source = Join-Path $output $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing package input: $name" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $staging $name)
    }
    Copy-Item -LiteralPath (Join-Path $output "i18n") -Destination (Join-Path $staging "i18n") -Recurse
    Remove-Item -LiteralPath $zip,$qmod,"$qmod.sha256" -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zip
    Move-Item -LiteralPath $zip -Destination $qmod
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($qmod)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
        foreach ($required in @("module.json", "QingToolbox.Modules.WindowTopmost.dll", "icon.svg", "i18n/en-US.json", "i18n/zh-CN.json")) {
            if ($required -notin $entries) { throw "Missing package entry: $required" }
        }
        if ($entries | Where-Object { $_ -match '(^|/)QingToolbox\.Abstractions|\.pdb$|\.cs$|\.csproj$|settings\.json$' }) {
            throw "Forbidden development or host content detected in package."
        }
    } finally { $archive.Dispose() }
    $hash = (Get-FileHash -LiteralPath $qmod -Algorithm SHA256).Hash
    Set-Content -LiteralPath "$qmod.sha256" -Value "$hash  $(Split-Path $qmod -Leaf)" -Encoding ASCII
    Write-Host "WindowTopmost package: $qmod"
    Write-Host "SHA256: $hash"
} finally {
    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
}
