# Qing Launcher 0.3.0 — Tauri module

This is the Rust process-module generation of Qing Launcher. It requires the QingToolbox 0.3.0-alpha Tauri host and is **not compatible** with the former WPF host or its .NET in-process module loader. Its `.qmod` package and SHA256 sidecar are independent of the host installer.

## Changes

- Centered transparent launcher overlay with outside-click dismissal, desktop/custom/initial-letter views and recent items.
- Smooth drag ordering, desktop-to-custom additions, folders, item removal and folder-opening animation.
- High-resolution application and desktop icons loaded on demand.
- Native hotkey capture, including Alt+Space, and built-in Everything search with index/readiness feedback.
- Nonce-bound module handshake and explicit enable/disable behaviour; disabling stops the owned Everything client without uninstalling another user's service.

The repository-built archive contains `module.json` and `qmod.json` with `moduleApiVersion: tauri-process-v1`, plus the executable, Web UI and pinned third-party runtime assets. Install through the Tauri host's module import flow. Do not copy it into a WPF module directory or point the old `modules/Launcher/update.json` at this asset: that catalog describes the incompatible `experimental-0.1` package generation.

This unsigned alpha module is also bundled with the QingToolbox 0.3.0-alpha installer. Its separate Release is intended for users who need to import or replace the native module independently.
