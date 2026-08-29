# Bundled qpdf runtime

Qing PDF bundles the official qpdf 12.4.1 MSVC 64-bit portable runtime for local PDF transformations.

- Project: https://github.com/qpdf/qpdf
- Release: https://github.com/qpdf/qpdf/releases/tag/v12.4.1
- Source archive: `qpdf-12.4.1-msvc64.zip`
- Source archive SHA-256: `3cd016cd433ef7232e42f4c13348a49cc14907a3c7278ef4f99120593126f7a6`
- qpdf license: Apache License 2.0; see `LICENSE.txt`.

Only the qpdf command-line executable, its qpdf library, and the runtime DLLs required by the official Windows build are included. They are kept together in this directory because Windows resolves the runtime DLLs beside `qpdf.exe`. The Microsoft Visual C++ runtime components remain subject to the applicable Microsoft Visual Studio licensing terms: https://visualstudio.microsoft.com/license-terms/

`SHA256SUMS` pins every shipped runtime file. The module verifies the bundled runtime before enabling PDF operations. Builds do not download a moving `latest` dependency.
