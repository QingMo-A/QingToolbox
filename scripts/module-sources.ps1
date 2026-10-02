function Resolve-QingModuleSource {
    param([Parameter(Mandatory = $true)][string]$Scope)
    $resolver = Join-Path $PSScriptRoot 'module-sources.mjs'
    $resolved = & node $resolver $Scope
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($resolved)) {
        throw "Cannot locate the Rust/Tauri module source for $Scope."
    }
    return [IO.Path]::GetFullPath([string]$resolved)
}
