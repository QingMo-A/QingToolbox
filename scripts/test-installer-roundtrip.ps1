[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [string]$TestRoot,
    [switch]$KeepTestFiles
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot "get-preview-release-metadata.ps1")
$metadata = Get-PreviewReleaseMetadata
$createdTestRoot = [string]::IsNullOrWhiteSpace($TestRoot)
$testSucceeded = $false
$failure = $null
$installerProcess = $null
$uninstallerProcess = $null
$moduleSentinel = $null
$dataSentinel = $null
$settingsSentinel = $null
$resolvedTestRoot = $null
$installDirectory = $null
$installLog = $null
$uninstallLog = $null
$shellExe = $null
$currentStage = "Initialize"
$createdUserDirectories = [System.Collections.Generic.List[string]]::new()

function Write-Stage {
    param([Parameter(Mandatory = $true)][string]$Name)

    $script:currentStage = $Name
    Write-Host "`n==> $Name"
}

function Assert-NoFiles {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.IO.FileInfo[]]$Files,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ($Files.Count -gt 0) {
        throw "$Description found: $($Files.FullName -join ', ')"
    }
}

function New-TrackedDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
        $createdUserDirectories.Add($Path)
    }
}

function Invoke-Uninstaller {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$LogPath
    )

    $process = Start-Process -FilePath $Path -ArgumentList @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART",
        "/LOG=`"$LogPath`""
    ) -Wait -PassThru
    Write-Host "Uninstaller exit code: $($process.ExitCode)"
    if ($process.ExitCode -ne 0) {
        throw "Uninstaller failed with exit code $($process.ExitCode)."
    }

    return $process
}

try {
    Write-Stage "Validate inputs"
    $resolvedInstallerPath = [System.IO.Path]::GetFullPath($InstallerPath)
    if (-not (Test-Path -LiteralPath $resolvedInstallerPath -PathType Leaf)) {
        throw "Installer does not exist: $resolvedInstallerPath"
    }
    if ([System.IO.Path]::GetExtension($resolvedInstallerPath) -ne ".exe") {
        throw "Installer must be an .exe file: $resolvedInstallerPath"
    }

    if ($createdTestRoot) {
        $TestRoot = Join-Path $env:TEMP (
            "QingToolbox-installer-test-{0}" -f [guid]::NewGuid().ToString("N"))
    }
    $resolvedTestRoot = [System.IO.Path]::GetFullPath($TestRoot)
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        throw "TestRoot must not already exist: $resolvedTestRoot"
    }
    $installDirectory = Join-Path $resolvedTestRoot "Install"
    $installLog = Join-Path $resolvedTestRoot "install.log"
    $uninstallLog = Join-Path $resolvedTestRoot "uninstall.log"
    New-Item -ItemType Directory -Path $resolvedTestRoot -Force | Out-Null

    $realProgramsDirectory = [System.IO.Path]::GetFullPath(
        (Join-Path $env:LOCALAPPDATA "Programs\QingToolbox"))
    if ($installDirectory.Equals(
        $realProgramsDirectory,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The test installation directory must not be the real user install directory."
    }

    Write-Host "Installer:         $resolvedInstallerPath"
    Write-Host "Test root:         $resolvedTestRoot"
    Write-Host "Install directory: $installDirectory"

    Write-Stage "Install QingToolbox silently"
    $installerProcess = Start-Process -FilePath $resolvedInstallerPath -ArgumentList @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART",
        "/NOICONS",
        "/LANG=english",
        "/DIR=`"$installDirectory`"",
        "/LOG=`"$installLog`""
    ) -Wait -PassThru
    Write-Host "Installer exit code: $($installerProcess.ExitCode)"
    if ($installerProcess.ExitCode -ne 0) {
        throw "Installer failed with exit code $($installerProcess.ExitCode)."
    }
    if (-not (Test-Path -LiteralPath $installLog -PathType Leaf)) {
        throw "Installer did not create its log: $installLog"
    }

    Write-Stage "Validate installed payload"
    $shellExe = Join-Path $installDirectory "QingToolbox.Shell.exe"
    $requiredFiles = @(
        $shellExe,
        (Join-Path $installDirectory "LICENSE"),
        (Join-Path $installDirectory "CHANGELOG.md"),
        (Join-Path $installDirectory "docs\QMOD_FORMAT.md"),
        (Join-Path $installDirectory "docs\releases\$($metadata.Version).md"),
        (Join-Path $installDirectory "docs\sdk\README.md"),
        (Join-Path $installDirectory "Resources\Localization\en-US.json"),
        (Join-Path $installDirectory "Resources\Localization\zh-CN.json")
    )
    foreach ($requiredFile in $requiredFiles) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
            throw "Installed payload is missing required file: $requiredFile"
        }
    }

    $uninstaller = Get-ChildItem -LiteralPath $installDirectory `
        -Filter "unins*.exe" -File | Select-Object -First 1
    if ($null -eq $uninstaller) {
        throw "Installed payload does not contain an Inno Setup uninstaller."
    }

    $installedFiles = @(Get-ChildItem -LiteralPath $installDirectory -Recurse -File)
    Assert-NoFiles -Files @($installedFiles | Where-Object Extension -EQ ".pdb") `
        -Description "PDB files"
    Assert-NoFiles -Files @($installedFiles | Where-Object {
        $_.Extension -In @(".cs", ".csproj", ".sln")
    }) -Description "Source or project files"
    Assert-NoFiles -Files @($installedFiles | Where-Object {
        $_.Name -like "QingToolbox.Modules.*" -or
        $_.Name -match "^(TextTools|ScreenPin|WindowTopmost|PowerGuard)(\.|$)"
    }) -Description "Concrete module files"
    Assert-NoFiles -Files @($installedFiles | Where-Object {
        $_.Name -eq "stop-qingtoolbox.bat" -or
        $_.Name -match '^settings(\.corrupt-[^.]*)?\.json$'
    }) -Description "Forbidden support or user files"

    $buildDirectories = @(Get-ChildItem -LiteralPath $installDirectory `
        -Recurse -Directory | Where-Object Name -In @(
            "Modules", "modules", "tests", "bin", "obj", ".git"))
    if ($buildDirectories.Count -gt 0) {
        throw "Installed payload contains bin/obj directories: " +
              ($buildDirectories.FullName -join ", ")
    }

    $moduleDlls = @(Get-ChildItem -LiteralPath $installDirectory `
        -Recurse -Filter "*.dll" -File | Where-Object FullName -Match "[\\/]Modules[\\/]")
    Assert-NoFiles -Files $moduleDlls -Description "User or concrete module DLLs"

    $shellVersionInfo = (Get-Item -LiteralPath $shellExe).VersionInfo
    $shellFileVersion = $shellVersionInfo.FileVersion.Trim()
    if ($shellFileVersion -ne $metadata.FileVersion) {
        throw "Unexpected Shell file version: $($shellVersionInfo.FileVersion)"
    }
    Write-Host "Shell FileVersion:  $shellFileVersion"
    Write-Host "Shell ProductName:  $($shellVersionInfo.ProductName)"
    Write-Host "Shell CompanyName:  $($shellVersionInfo.CompanyName)"

    $installerVersionInfo = (Get-Item -LiteralPath $resolvedInstallerPath).VersionInfo
    $installerFileVersion = $installerVersionInfo.FileVersion.Trim()
    $installerProductName = $installerVersionInfo.ProductName.Trim()
    $installerCompanyName = $installerVersionInfo.CompanyName.Trim()
    if ($installerFileVersion -ne $metadata.FileVersion) {
        throw "Unexpected installer FileVersion: $($installerVersionInfo.FileVersion)"
    }
    if ($installerProductName -ne "QingToolbox") {
        throw "Unexpected installer ProductName: $($installerVersionInfo.ProductName)"
    }
    if ($installerCompanyName -ne "QingMo-A") {
        throw "Unexpected installer CompanyName: $($installerVersionInfo.CompanyName)"
    }
    Write-Host "Installer FileVersion: $installerFileVersion"
    Write-Host "Installer ProductName: $installerProductName"
    Write-Host "Installer CompanyName: $installerCompanyName"

    Write-Stage "Create isolated user-data sentinels"
    $sentinelId = [guid]::NewGuid().ToString("N")
    $localUserRoot = Join-Path $env:LOCALAPPDATA "QingToolbox"
    $roamingUserRoot = Join-Path $env:APPDATA "QingToolbox"
    $modulesDirectory = Join-Path $localUserRoot "Modules"
    $dataDirectory = Join-Path $roamingUserRoot "Data"
    $settingsSentinel = Join-Path $roamingUserRoot "settings.json"
    $moduleSentinel = Join-Path $modulesDirectory `
        "installer-test-module-$sentinelId.txt"
    $dataSentinel = Join-Path $dataDirectory `
        "installer-test-data-$sentinelId.txt"

    foreach ($sentinel in @($moduleSentinel, $dataSentinel, $settingsSentinel)) {
        if (Test-Path -LiteralPath $sentinel) {
            throw "Refusing to overwrite an existing user file: $sentinel"
        }
    }

    New-TrackedDirectory -Path $localUserRoot
    New-TrackedDirectory -Path $modulesDirectory
    New-TrackedDirectory -Path $roamingUserRoot
    New-TrackedDirectory -Path $dataDirectory
    Set-Content -LiteralPath $moduleSentinel -Value "QingToolbox installer roundtrip" `
        -Encoding UTF8
    Set-Content -LiteralPath $dataSentinel -Value "QingToolbox installer roundtrip" `
        -Encoding UTF8
    @{
        language = "en-US"
        installerRoundtripSentinel = $true
    } | ConvertTo-Json | Set-Content -LiteralPath $settingsSentinel -Encoding UTF8

    Write-Stage "Uninstall QingToolbox silently"
    $uninstallerProcess = Invoke-Uninstaller -Path $uninstaller.FullName `
        -LogPath $uninstallLog
    if (-not (Test-Path -LiteralPath $uninstallLog -PathType Leaf)) {
        throw "Uninstaller did not create its log: $uninstallLog"
    }

    Write-Stage "Validate uninstall and retained user data"
    $installRemoved = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        if (-not (Test-Path -LiteralPath $shellExe) -and
            -not (Test-Path -LiteralPath $installDirectory)) {
            $installRemoved = $true
            break
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $installRemoved) {
        $remaining = if (Test-Path -LiteralPath $installDirectory) {
            @(Get-ChildItem -LiteralPath $installDirectory -Recurse -Force |
                Select-Object -ExpandProperty FullName)
        }
        else {
            @()
        }
        throw "Installation directory was not removed after uninstall. Remaining: " +
              ($remaining -join ", ")
    }

    foreach ($sentinel in @($moduleSentinel, $dataSentinel, $settingsSentinel)) {
        if (-not (Test-Path -LiteralPath $sentinel -PathType Leaf)) {
            throw "Uninstall removed user-data sentinel: $sentinel"
        }
    }

    Write-Host "Module sentinel retained:   $moduleSentinel"
    Write-Host "Data sentinel retained:     $dataSentinel"
    Write-Host "Settings sentinel retained: $settingsSentinel"
    $testSucceeded = $true
    Write-Host "`nInstaller roundtrip test passed."
}
catch {
    $failure = $_
    if ($null -ne $resolvedTestRoot -and (Test-Path -LiteralPath $resolvedTestRoot)) {
        $diagnosticsPath = Join-Path $resolvedTestRoot "diagnostics.txt"
        @(
            "Stage: $currentStage",
            "Installer: $InstallerPath",
            "Install directory: $installDirectory",
            "Error: $($_.Exception.Message)"
        ) | Set-Content -LiteralPath $diagnosticsPath -Encoding UTF8
    }
}
finally {
    foreach ($sentinel in @($moduleSentinel, $dataSentinel, $settingsSentinel)) {
        if ($null -ne $sentinel -and (Test-Path -LiteralPath $sentinel -PathType Leaf)) {
            Remove-Item -LiteralPath $sentinel -Force
        }
    }

    foreach ($directory in @($createdUserDirectories | Sort-Object Length -Descending)) {
        if (Test-Path -LiteralPath $directory -PathType Container) {
            $contents = @(Get-ChildItem -LiteralPath $directory -Force)
            if ($contents.Count -eq 0) {
                Remove-Item -LiteralPath $directory
            }
        }
    }

    if (-not $testSucceeded -and $null -ne $installDirectory -and
        (Test-Path -LiteralPath $installDirectory)) {
        $cleanupUninstaller = Get-ChildItem -LiteralPath $installDirectory `
            -Filter "unins*.exe" -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $cleanupUninstaller) {
            try {
                $cleanupLog = if ($null -ne $uninstallLog) {
                    $uninstallLog
                }
                else {
                    Join-Path $resolvedTestRoot "uninstall.log"
                }
                Invoke-Uninstaller -Path $cleanupUninstaller.FullName `
                    -LogPath $cleanupLog | Out-Null
            }
            catch {
                Write-Warning "Cleanup uninstall failed: $_"
            }
        }
    }

    if ($KeepTestFiles -or $null -ne $failure) {
        if ($null -ne $resolvedTestRoot) {
            Write-Host "Test files retained at: $resolvedTestRoot"
        }
    }
    elseif ($null -ne $resolvedTestRoot -and (Test-Path -LiteralPath $resolvedTestRoot)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}

if ($null -ne $failure) {
    Write-Error -ErrorRecord $failure
    exit 1
}
