[CmdletBinding()]
param(
    [string]$ModulesRoot
)

$ErrorActionPreference = "Stop"
$hasError = $false
$ModulesRoot = if ([string]::IsNullOrWhiteSpace($ModulesRoot)) {
    Join-Path $PSScriptRoot "..\modules"
}
else {
    $ModulesRoot
}
$resolvedModulesRoot = [System.IO.Path]::GetFullPath($ModulesRoot)

function Write-CheckError {
    param([string]$Message)

    Write-Error $Message -ErrorAction Continue
    $script:hasError = $true
}

function ConvertTo-Hashtable {
    param([Parameter(Mandatory = $true)][object]$Object)

    $table = @{}
    foreach ($property in $Object.PSObject.Properties) {
        $table[$property.Name] = $property.Value
    }

    return $table
}

function Read-JsonObject {
    param([Parameter(Mandatory = $true)][string]$Path)

    $json = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    return ConvertTo-Hashtable ($json | ConvertFrom-Json)
}

if (-not (Test-Path -LiteralPath $resolvedModulesRoot -PathType Container)) {
    Write-CheckError "Modules root not found: $resolvedModulesRoot"
    exit 1
}

$moduleDirectories = Get-ChildItem -LiteralPath $resolvedModulesRoot -Directory

foreach ($moduleDirectory in $moduleDirectories) {
    $moduleName = $moduleDirectory.Name
    $manifestPath = Join-Path $moduleDirectory.FullName "module.json"
    $resources = @{}

    $i18nPath = Join-Path $moduleDirectory.FullName "i18n"
    if (Test-Path -LiteralPath $i18nPath -PathType Container) {
        foreach ($file in Get-ChildItem -LiteralPath $i18nPath -Filter "*.json" -File) {
            try {
                $resources[$file.BaseName] = Read-JsonObject -Path $file.FullName
            }
            catch {
                Write-CheckError "Invalid JSON: $($file.FullName) - $($_.Exception.Message)"
                continue
            }

            foreach ($entry in $resources[$file.BaseName].GetEnumerator()) {
                if ($entry.Value -is [string] -and $entry.Value -match "\?{4,}") {
                    Write-CheckError "Corrupted text found: $($file.FullName) key '$($entry.Key)' value '$($entry.Value)'"
                }
            }
        }
    }

    if ($resources.ContainsKey("en-US") -and $resources.ContainsKey("zh-CN")) {
        $enKeys = @($resources["en-US"].Keys | Sort-Object)
        $zhKeys = @($resources["zh-CN"].Keys | Sort-Object)

        foreach ($item in Compare-Object -ReferenceObject $enKeys -DifferenceObject $zhKeys) {
            $message = if ($item.SideIndicator -eq "<=") {
                "Missing zh-CN key in $moduleName`: $($item.InputObject)"
            }
            else {
                "Extra zh-CN key in $moduleName`: $($item.InputObject)"
            }
            Write-CheckError $message
        }
    }

    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        continue
    }

    try {
        $manifest = Read-JsonObject -Path $manifestPath
    }
    catch {
        Write-CheckError "Invalid JSON: $manifestPath - $($_.Exception.Message)"
        continue
    }

    if (-not $manifest.ContainsKey("defaultLanguage") -or
        [string]::IsNullOrWhiteSpace([string]$manifest["defaultLanguage"])) {
        Write-CheckError "Missing defaultLanguage in $manifestPath"
    }
    elseif ($manifest["defaultLanguage"] -ne "en-US") {
        Write-CheckError "defaultLanguage should be 'en-US' in $manifestPath"
    }

    if (-not $manifest.ContainsKey("localization") -or $null -eq $manifest["localization"]) {
        Write-CheckError "Missing localization configuration in $manifestPath"
        continue
    }

    $localization = ConvertTo-Hashtable $manifest["localization"]
    if (-not $localization.ContainsKey("resources") -or $null -eq $localization["resources"]) {
        Write-CheckError "Missing localization.resources in $manifestPath"
        continue
    }

    $declaredResources = ConvertTo-Hashtable $localization["resources"]
    foreach ($culture in @("en-US", "zh-CN")) {
        if (-not $declaredResources.ContainsKey($culture) -or
            [string]::IsNullOrWhiteSpace([string]$declaredResources[$culture])) {
            Write-CheckError "Missing localization.resources.$culture in $manifestPath"
            continue
        }

        $resourcePath = Join-Path $moduleDirectory.FullName ([string]$declaredResources[$culture])
        if (-not (Test-Path -LiteralPath $resourcePath -PathType Leaf)) {
            Write-CheckError "Declared $culture resource not found: $resourcePath"
        }
    }
}

if ($hasError) {
    exit 1
}

Write-Host "Module i18n check passed: $resolvedModulesRoot"
