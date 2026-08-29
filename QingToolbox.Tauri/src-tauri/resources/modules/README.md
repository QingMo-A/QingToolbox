# Bundled modules

The Tauri host scans this directory read-only. Each bundled module must live
in its own directory and contain a `module.json` process-profile manifest.

Build the checked-in native handshake canary into this directory with:

```powershell
./scripts/build-tauri-canary.ps1
```

Generated binaries are intentionally not committed. Product modules will be
added here only after their process protocol and package provenance are fixed.
