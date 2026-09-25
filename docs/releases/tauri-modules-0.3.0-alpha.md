# QingToolbox native modules for 0.3.0-alpha

These packages use the `tauri-process-v1` module API and require the Rust/Tauri QingToolbox host. They cannot be loaded by the older WPF host. Each GitHub pre-release contains one `.qmod` and its matching `.sha256` file; the host installer also bundles the same module generation.

| Module | Version | Release tag |
| --- | --- | --- |
| Qing Launcher | 0.3.0 | `modules-launcher-v0.3.0` |
| QingTransfer | 0.3.0 | `modules-qingtransfer-v0.3.0` |
| Screen Pin | 0.2.0 | `modules-screenpin-v0.2.0` |
| Window Topmost | 0.2.0 | `modules-windowtopmost-v0.2.0` |
| PowerGuard | 0.2.0 | `modules-powerguard-v0.2.0` |
| Text Tools | 0.2.0 | `modules-texttools-v0.2.0` |
| Qing PDF | 0.1.0 | `modules-pdf-v0.1.0` |
| Web Module Canary | 0.1.0 | `modules-canary-v0.1.0` |

The Canary is a compatibility test module, not a general-purpose user tool. The packages are unsigned alpha builds. The older WPF module update catalogs are intentionally unchanged because their packages use an incompatible API. Import/replace the native `.qmod` through a Tauri host; do not unpack it into a WPF module folder.
