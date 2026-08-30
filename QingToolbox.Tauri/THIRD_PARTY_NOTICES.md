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
| mdns-sd | 0.21.0 | Apache-2.0 OR MIT — https://github.com/keeshastudios/mdns-sd |
| qpdf | 12.4.1 | Apache-2.0 — https://github.com/qpdf/qpdf |
| base64 | 0.22.1 | Apache-2.0 OR MIT — https://github.com/marshallpierce/rust-base64 |
| png | 0.17.16 | MIT OR Apache-2.0 — https://github.com/image-rs/image-png |

The native canary and native modules use Serde and serde_json under their
respective MIT or Apache-2.0 licenses. The Tauri host itself does not bundle
Everything, qpdf or another heavyweight runtime. Qing Launcher, Qing PDF and
QingTransfer are explicitly scoped module exceptions:
its package carries the fixed official Everything 1.4.1.1032 x64 portable
components, ES 1.1.0.37 and the SDK DLL under
`native-launcher/third-party/Everything/`, together with `LICENSE.txt` and
`NOTICE.md`. The pinned component hashes and official source URLs are recorded
in that notice; the build does not download a moving `latest` dependency. The
native Launcher also uses `sha2` 0.10.9 (MIT OR Apache-2.0) for asset and
instance identity checks and `windows-sys` 0.61.2 (MIT OR Apache-2.0) only for
Windows clipboard/elevation APIs. The ZIP importer uses the fixed `zip` crate
only for package extraction; it is not a general filesystem bridge.

Qing PDF carries the official qpdf 12.4.1 Windows runtime under
`native-pdf/third-party/qpdf/`, including its `LICENSE.txt`, `NOTICE.md` and
`SHA256SUMS`; the build script verifies every copied file against the pinned
hashes. QingTransfer uses the fixed `mdns-sd` 0.21.0 crate for DNS-SD discovery;
its transitive versions are recorded in `native-transfer/Cargo.lock`. No build
step downloads a moving `latest` runtime.

Text Tools uses the fixed `base64` 0.22.1 crate and `windows-sys` 0.61.2 for
bounded text conversion and Windows clipboard writes. Window Topmost reuses
`windows-sys` 0.61.2 only for its narrow window enumeration, cursor-pick and
`SetWindowPos` calls. PowerGuard uses only the Rust standard library for its
bounded TCP probes and system process invocation; it adds no third-party
runtime or network client.

Screen Pin uses the fixed `png` 0.17.16 crate (MIT OR Apache-2.0) for bounded
PNG encoding and reuses `windows-sys` 0.61.2 for its narrow GDI capture calls.
No image or screen-capture runtime is downloaded at build time.
