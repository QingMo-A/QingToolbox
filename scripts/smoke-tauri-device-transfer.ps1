[CmdletBinding()]
param()

# File transfer is compiled into the host; there is no module executable.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifest = Join-Path $repoRoot 'QingToolbox.Tauri/src-tauri/Cargo.toml'
& cargo test --manifest-path $manifest --locked device_transfer::tests
if ($LASTEXITCODE -ne 0) { throw 'Host device-transfer smoke tests failed.' }
