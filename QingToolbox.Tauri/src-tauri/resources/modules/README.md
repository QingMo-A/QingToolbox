# Bundled modules

The Tauri host scans this directory read-only. Each bundled module must live
in its own directory and contain a `module.json` process-profile manifest.

Build the checked-in native handshake canary into this directory with:

```powershell
./scripts/build-tauri-canary.ps1
```

Generated binaries are intentionally not committed. The build scripts stage
the current native product modules here after checking their fixed inputs:

- `qing.launcher` — Launcher state and the private Everything runtime.
- `qing.pdf` — local PDF operations and the pinned qpdf runtime.
- `qing.qingtransfer` — local-network discovery and verified file transfer.
- `qing.texttools` — bounded local text conversion and backend-owned clipboard writes.
- `qing.windowtopmost` — visible-window enumeration and opaque-ID topmost control.

Each module remains an independent `Process`/`OutOfProcess` package. The host
does not load legacy .NET DLLs from this directory.
