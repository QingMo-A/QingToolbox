# QingToolbox Inno Setup Installer

The Preview installer is built with Inno Setup 6. Install Inno Setup locally;
the compiler is not downloaded or committed to this repository.

Build the x64 self-contained installer:

```powershell
./scripts/build-installer.ps1
```

If `ISCC.exe` is installed elsewhere:

```powershell
./scripts/build-installer.ps1 `
  -IsccPath "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
```

The script reads `Version` and numeric `FileVersion` from
`Directory.Build.props`, builds and runs the smoke test, and always publishes a
self-contained host-only payload. It fails if the payload contains PDBs,
concrete modules, source/project files, or `bin`/`obj` directories, and verifies
the Shell executable, release documents, and localization JSON before writing
the installer plus SHA256 to `artifacts/installer/output`.

By default the command performs the Release build, development-module deploy,
and module smoke test before publishing. CI may pass `-SkipPreflight` only after
those steps have already succeeded; self-contained publish, payload validation,
Inno compilation, and SHA256 generation are never skipped.

## Installation and uninstall policy

- Installation is per-user at `%LOCALAPPDATA%\Programs\QingToolbox` and does
  not request administrator privileges.
- English and Simplified Chinese custom tasks, shortcuts, and post-install text
  follow the selected installer language (`ShowLanguageDialog=auto`).
- The installer creates a Start Menu application shortcut and localized
  uninstall shortcut. The desktop shortcut is optional and unchecked by default.
- Uninstall removes program files and shortcuts but preserves user modules,
  module data, and settings.
- Complete manual cleanup requires deleting `%LOCALAPPDATA%\QingToolbox` and
  `%APPDATA%\QingToolbox`.

The permanent AppId is:

```text
{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}
```

Do not change this AppId in future versions. Keeping it stable allows repair,
reinstall, and upgrade to use the same per-user installation record and path.
Future releases should update `Directory.Build.props` and reuse the same `.iss`.

## Installer roundtrip test

Run the generated installer through an isolated silent install/uninstall test:

```powershell
$env:LOCALAPPDATA = Join-Path $env:TEMP "QingToolboxProfile\LocalAppData"
$env:APPDATA = Join-Path $env:TEMP "QingToolboxProfile\AppData"
./scripts/test-installer-roundtrip.ps1 `
  -InstallerPath `
    ".\artifacts\installer\output\QingToolbox-0.1.0-alpha-win-x64-setup.exe"
```

The script installs under its own temporary root, validates the host-only
payload and version metadata, creates isolated user-data sentinels, uninstalls,
and verifies that modules, data, and settings are retained. It refuses to
overwrite an existing `settings.json`; use isolated `LOCALAPPDATA` and
`APPDATA` values for repeatable local and CI runs.

The Windows Preview validation workflow also builds both release assets,
recomputes their SHA256 checksums, performs this roundtrip, and uploads the four
assets for 10 days. CI installs the approved Chocolatey `innosetup` 6.7.1
package and obtains the Simplified Chinese message file from the official Inno
Setup translation endpoint. It does not publish a GitHub Release. A formal
QingToolbox application icon has not been provided yet.

The Simplified Chinese message file is downloaded from the official
`jrsoftware/issrc` source by `scripts/prepare-inno-setup.ps1`. Its expected
SHA256 is stored in `installer/dependencies.psd1`; updating the translation
requires reviewing the upstream file and explicitly updating that hash. The
script validates existing files without overwriting a different local copy.

The upstream translation is pinned to repository `jrsoftware/issrc`, commit
`683ee7eabfbce807f901c5da83fc5ff1a3ecb693`, path
`Files/Languages/ChineseSimplified.isl`, plus its SHA256. To update it, review a
specific upstream commit, recompute the file hash, and update all provenance
fields in `installer/dependencies.psd1` together.

Roundtrip installation passes `/NOICONS`, so automated tests create no Start
Menu or desktop shortcuts. With `-KeepTestFiles`, `install.log`, `uninstall.log`,
and failure diagnostics remain under `TestRoot`; CI uploads only these text logs
and then removes its isolated test/profile directories.

Release versioning and asset names come from
`scripts/get-preview-release-metadata.ps1`. After both assets are built,
`scripts/write-preview-manifest.ps1` writes a JSON manifest containing the
source commit and recomputed hashes; `scripts/verify-preview-assets.ps1`
validates the assets, checksum files, manifest, and current Git HEAD. Official
GitHub Actions are pinned to full release commit SHAs in the workflow and must
be upgraded explicitly.
