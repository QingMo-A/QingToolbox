[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('Development', 'ModuleTest', 'Both')]
    [string]$TargetEnvironment = 'Both'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pinnedCommit = 'bc0e57b5a77e3526de157d92a3d300bf3d267e8b'
$canaryRef = "refs/qingtoolbox/canary/texttools/$pinnedCommit"
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$projectPath = Join-Path $repositoryRoot 'QingToolbox.DevTools.TextToolsModuleUpdateCanary\QingToolbox.DevTools.TextToolsModuleUpdateCanary.csproj'
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "QingToolbox-TextToolsCanary-$runId"

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($ArgumentList -join ' ')"
    }
}

function Write-CanaryMarker {
    param(
        [Parameter(Mandatory)]
        [string]$SourceDirectory,

        [Parameter(Mandatory)]
        [ValidateSet('v1', 'v2')]
        [string]$Variant
    )

    $marker = @"
using System.Reflection;

[assembly: AssemblyMetadata("QingToolbox.TextToolsCanary.Variant", "$Variant")]
[assembly: AssemblyMetadata("QingToolbox.TextToolsCanary.SourceCommit", "$pinnedCommit")]
"@
    [IO.File]::WriteAllText(
        (Join-Path $SourceDirectory 'CanaryMarker.g.cs'),
        $marker,
        [Text.UTF8Encoding]::new($false))

    $manifestPath = Join-Path $SourceDirectory 'module.json'
    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    $manifest | Add-Member -NotePropertyName 'runtimeIsolation' -NotePropertyValue 'OutOfProcess' -Force
    $manifest | Add-Member -NotePropertyName 'uiKind' -NotePropertyValue 'Wpf' -Force
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 16),
        [Text.UTF8Encoding]::new($false))
}

function Build-TextToolsVariant {
    param(
        [Parameter(Mandatory)]
        [string]$SourceDirectory
    )

    $project = Join-Path $SourceDirectory 'QingToolbox.Modules.TextTools.csproj'
    Invoke-Checked 'dotnet' @(
        'restore', $project,
        "-p:QingToolboxHostRoot=$repositoryRoot",
        '-p:RestoreProjectReferences=false'
    )
    Invoke-Checked 'dotnet' @(
        'build', $project,
        '-c', $Configuration,
        '--no-restore',
        "-p:QingToolboxHostRoot=$repositoryRoot",
        '-p:BuildProjectReferences=false'
    )

    $output = Join-Path $SourceDirectory "bin\$Configuration\net10.0-windows"
    foreach ($required in @('module.json', 'QingToolbox.Modules.TextTools.dll')) {
        $path = Join-Path $output $required
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "TextTools build output is missing: $path"
        }
    }
    return [IO.Path]::GetFullPath($output)
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'The real TextTools WPF canary requires Windows.'
    }
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Canary project is missing: $projectPath"
    }

    Invoke-Checked 'git' @('-C', $repositoryRoot, 'fetch', '--no-tags', '--depth=1',
        'origin', "+$pinnedCommit`:$canaryRef")
    Invoke-Checked 'git' @(
        '-C', $repositoryRoot,
        'cat-file', '-e', "$pinnedCommit`^{commit}"
    )

    [IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $archivePath = Join-Path $temporaryRoot 'TextTools.tar'
    Invoke-Checked 'git' @(
        '-C', $repositoryRoot,
        'archive', '--format=tar', "--output=$archivePath",
        $canaryRef, '--', 'modules/TextTools'
    )

    $v1Export = Join-Path $temporaryRoot 'source-v1'
    $v2Export = Join-Path $temporaryRoot 'source-v2'
    [IO.Directory]::CreateDirectory($v1Export) | Out-Null
    [IO.Directory]::CreateDirectory($v2Export) | Out-Null
    Invoke-Checked 'tar' @('-xf', $archivePath, '-C', $v1Export)
    Invoke-Checked 'tar' @('-xf', $archivePath, '-C', $v2Export)

    $v1Source = Join-Path $v1Export 'modules\TextTools'
    $v2Source = Join-Path $v2Export 'modules\TextTools'
    Write-CanaryMarker -SourceDirectory $v1Source -Variant 'v1'
    Write-CanaryMarker -SourceDirectory $v2Source -Variant 'v2'

    Invoke-Checked 'dotnet' @('build', $projectPath, '-c', $Configuration)
    $v1Output = Build-TextToolsVariant -SourceDirectory $v1Source
    $v2Output = Build-TextToolsVariant -SourceDirectory $v2Source

    $packageRoot = Join-Path $temporaryRoot 'packages'
    [IO.Directory]::CreateDirectory($packageRoot) | Out-Null
    $environments = if ($TargetEnvironment -eq 'Both') {
        @('Development', 'ModuleTest')
    }
    else {
        @($TargetEnvironment)
    }

    foreach ($environment in $environments) {
        Invoke-Checked 'dotnet' @(
            'run', '--project', $projectPath,
            '-c', $Configuration,
            '--no-build', '--',
            '--repository-root', $repositoryRoot,
            '--v1-output', $v1Output,
            '--v2-output', $v2Output,
            '--artifacts-root', $packageRoot,
            '--environment', $environment,
            '--run-id', $runId,
            '--source-commit', $pinnedCommit
        )
    }

    Write-Host "TextTools module-update canary completed for: $($environments -join ', ')."
}
finally {
    & git -C $repositoryRoot update-ref -d $canaryRef 2>$null
    $trimSeparators = [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $normalizedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd($trimSeparators)
    $normalizedWork = [IO.Path]::GetFullPath($temporaryRoot).TrimEnd($trimSeparators)
    $expectedPrefix = $normalizedTemp + [IO.Path]::DirectorySeparatorChar + 'QingToolbox-TextToolsCanary-'
    if (-not $normalizedWork.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected path: $normalizedWork"
    }
    if (Test-Path -LiteralPath $normalizedWork -PathType Container) {
        Remove-Item -LiteralPath $normalizedWork -Recurse -Force
    }
}
