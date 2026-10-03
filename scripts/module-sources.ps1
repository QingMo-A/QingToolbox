function Resolve-QingModuleSource {
    param([Parameter(Mandatory = $true)][string]$Scope)
    $resolver = Join-Path $PSScriptRoot 'module-sources.mjs'
    $resolved = & node $resolver $Scope
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($resolved)) {
        throw "Cannot locate the Rust/Tauri module source for $Scope."
    }
    return [IO.Path]::GetFullPath([string]$resolved)
}

function Restore-QingModuleUiDependencies {
    param([Parameter(Mandatory = $true)][string]$UiRoot)
    & node (Join-Path $PSScriptRoot 'module-ui-dependencies.mjs') $UiRoot
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Reusing locked UI dependencies: $UiRoot"
        return
    }
    # npm ci deletes node_modules first. Do not damage a live preview if its
    # dependency tree needs restoring; tell the user which process holds it.
    $dependencyRoot = [IO.Path]::GetFullPath((Join-Path $UiRoot 'node_modules')) + [IO.Path]::DirectorySeparatorChar
    $busy = @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($dependencyRoot, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($busy.Count -gt 0) {
        throw "UI dependencies changed while a preview is running (PID $($busy.ProcessId -join ', ')). Stop that module preview before restoring dependencies: $UiRoot"
    }
    Push-Location $UiRoot
    try {
        npm ci --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "UI dependency install failed with exit code ${LASTEXITCODE}: $UiRoot" }
    } finally { Pop-Location }
}
