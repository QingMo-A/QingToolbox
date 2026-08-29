# Third-party notices

The Tauri host uses the following direct third-party packages. Their complete
license texts and transitive dependency versions are fixed by
`src-tauri/Cargo.lock` and `package-lock.json`.

| Package | Version | License / source |
| --- | --- | --- |
| Tauri | 2.11.5 | Apache-2.0 OR MIT — https://github.com/tauri-apps/tauri |
| Tauri single-instance plugin | 2.4.3 | Apache-2.0 OR MIT — https://github.com/tauri-apps/plugins-workspace |
| Tauri dialog plugin | 2.7.2 | Apache-2.0 OR MIT — https://github.com/tauri-apps/plugins-workspace |
| Tauri global-shortcut plugin | 2.3.2 | Apache-2.0 OR MIT — https://github.com/tauri-apps/plugins-workspace |
| Tauri autostart plugin | 2.5.1 | Apache-2.0 OR MIT — https://github.com/tauri-apps/plugins-workspace |
| zip | 2.4.2 | MIT — https://github.com/zip-rs/zip2 |
| getrandom | 0.3.4 | Apache-2.0 OR MIT — https://github.com/rust-random/getrandom |
| Vue | 3.5.42 | MIT — https://github.com/vuejs/core |
| Vite | 7.1.10 | MIT — https://github.com/vitejs/vite |

The native canary uses Serde and serde_json under their respective MIT or
Apache-2.0 licenses. No Everything, qpdf or other heavyweight runtime is
bundled in this foundation; a future module must record its own fixed runtime
and license before packaging it. The ZIP importer uses the fixed `zip` crate
only for package extraction; it is not a general filesystem bridge.
