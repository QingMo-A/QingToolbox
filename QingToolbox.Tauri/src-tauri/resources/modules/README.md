# Module package build staging

Each official module lives in its own directory with a `module.json`
process-profile manifest. This is a repository build/staging directory for
independent `.qmod` packages, not part of the production host installer.
Development hosts may scan it read-only; production hosts scan user-installed
modules only.

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
- `qing.powerguard` — bounded connectivity monitoring with guarded shutdown actions.
- `qing.screenpin` — bounded native screen capture with an in-session pin board.

Each module remains an independent `Process`/`OutOfProcess` package. The host
does not load legacy .NET DLLs from this directory.
