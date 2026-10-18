# Third-party components

Built-in Rust dependencies are locked by Cargo.lock. No FFmpeg, LibreOffice or Microsoft Office executable is redistributed in this initial module.

- image 0.25.10 — MIT OR Apache-2.0; https://github.com/image-rs/image
- pdf-extract 0.12.1 — MIT; https://github.com/jrmuizel/pdf-extract
- docx-rs 0.4.22 — MIT; https://github.com/bokuweb/docx-rs
- lopdf, adobe-cmap-parser, cff-parser, type1-encoding-parser, postscript and other transitive parser/codec crates retain their upstream licenses.
- Vue, Vite and the Tauri JavaScript APIs retain their respective upstream licenses; shared QingToolbox UI remains part of QingToolbox.

This file is a component overview. The development packaging step also bundles LICENSES.txt containing the upstream Rust license texts. Configured external FFmpeg and LibreOffice installations remain separately licensed and managed by the user. Microsoft Word requires the user's existing installation and license.
