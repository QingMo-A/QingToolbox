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
