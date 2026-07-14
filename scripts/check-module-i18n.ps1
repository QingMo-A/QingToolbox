param(
    [string]$ModulesRoot = "modules"
)

$ErrorActionPreference = "Stop"
$hasError = $false

function Write-CheckError {
    param(
        [string]$Message
    )

    Write-Error $Message -ErrorAction Continue
    $script:hasError = $true
}

function ConvertTo-Hashtable {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Object
    )

    $table = @{}

    foreach ($property in $Object.PSObject.Properties) {
        $table[$property.Name] = $property.Value
    }

    return $table
}

if (-not (Test-Path -LiteralPath $ModulesRoot)) {
    Write-CheckError "Modules root not found: $ModulesRoot"
    exit 1
}

$i18nDirectories = Get-ChildItem -LiteralPath $ModulesRoot -Directory |
    ForEach-Object {
        $path = Join-Path $_.FullName "i18n"
        if (Test-Path -LiteralPath $path) {
            Get-Item -LiteralPath $path
        }
    }

foreach ($directory in $i18nDirectories) {
    $moduleName = Split-Path -Leaf (Split-Path -Parent $directory.FullName)
    $files = Get-ChildItem -LiteralPath $directory.FullName -Filter "*.json" -File
    $resources = @{}

    foreach ($file in $files) {
        try {
            $json = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
            $resources[$file.BaseName] = ConvertTo-Hashtable ($json | ConvertFrom-Json)
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

    if ($resources.ContainsKey("en-US") -and $resources.ContainsKey("zh-CN")) {
        $enKeys = @($resources["en-US"].Keys | Sort-Object)
        $zhKeys = @($resources["zh-CN"].Keys | Sort-Object)

        $missingInZh = Compare-Object -ReferenceObject $enKeys -DifferenceObject $zhKeys |
            Where-Object SideIndicator -eq "<="
        $extraInZh = Compare-Object -ReferenceObject $enKeys -DifferenceObject $zhKeys |
            Where-Object SideIndicator -eq "=>"

        foreach ($item in $missingInZh) {
            Write-CheckError "Missing zh-CN key in $moduleName`: $($item.InputObject)"
        }

        foreach ($item in $extraInZh) {
            Write-CheckError "Extra zh-CN key in $moduleName`: $($item.InputObject)"
        }
    }
}

if ($hasError) {
    exit 1
}

Write-Host "Module i18n check passed."
