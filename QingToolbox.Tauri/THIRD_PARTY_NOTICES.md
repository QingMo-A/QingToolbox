# Third-party notices

The Tauri host uses the following direct third-party packages. Their complete
license texts and transitive dependency versions are fixed by
`src-tauri/Cargo.lock` and `package-lock.json`.

| Package | Version | License / source |
| --- | --- | --- |
| Tauri | 2.11.5 | Apache-2.0 OR MIT — https://github.com/tauri-apps/tauri |
| Tauri single-instance plugin | 2.4.3 | Apache-2.0 OR MIT — https://github.com/tauri-apps/plugins-workspace |
| getrandom | 0.3.4 | Apache-2.0 OR MIT — https://github.com/rust-random/getrandom |
| Vue | 3.5.42 | MIT — https://github.com/vuejs/core |
| Vite | 7.1.10 | MIT — https://github.com/vitejs/vite |

The native canary uses Serde and serde_json under their respective MIT or
Apache-2.0 licenses. No Everything, qpdf or other heavyweight runtime is
bundled in this foundation; a future module must record its own fixed runtime
and license before packaging it.
