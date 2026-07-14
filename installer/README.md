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

The script reads the version from `Directory.Build.props`, builds and runs the
smoke test, publishes a self-contained host-only payload, removes PDB files,
and writes the installer plus SHA256 to `artifacts/installer/output`.

## Installation and uninstall policy

- Installation is per-user at `%LOCALAPPDATA%\Programs\QingToolbox` and does
  not request administrator privileges.
- The installer creates a Start Menu application shortcut and uninstall
  shortcut. The desktop shortcut is optional and unchecked by default.
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
