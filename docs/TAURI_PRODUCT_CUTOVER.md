# Product identity cut-over

## Public alpha decision — 2026-09-25

The user has approved an unsigned alpha release without waiting for a code-signing certificate. The normal Tauri installer now uses the existing QingToolbox product AppId and default install identity rather than the isolated Tauri test identity. A first WPF migration backs up the registered installation and settings before copying Tauri files, and runs the old host's owned startup-registration cleanup before launching Tauri. The source release gate must still be clean and pass its candidate checks; the separately named `LegacyUpgradeTest` package remains a local-only test artifact and must never be uploaded.

The old WPF publishing BAT/workflow is not the Tauri publishing path. The published installer must have the `-tauri-setup.exe` asset identity and matching checksum, and the independent native `.qmod` releases must be clearly marked as Tauri-only. Signing and environment-specific real-user acceptance remain open risks, not claims of completion.

## Local acceptance — 2026-09-23

The local installed product has been updated to the unsigned **0.3.0-alpha**
Tauri candidate with **Qing Launcher 0.3.0**. The registered product directory
and existing shortcuts are retained. The installed Rust host, Launcher process
and UI, real settings, and login-start registration have been checked locally.

The obsolete WPF scheduled startup entry was backed up and removed. The old
installer payload manifest identified 500 unchanged legacy-only files (about
200 MiB), which were removed only after checking their hashes, verified rollback
copies and the new Tauri manifest. User settings, module data and installed user
module packages were not removed. The cleanup utility is
`scripts/remove-legacy-wpf-payload.ps1`; its default is a dry run, and it requires
the registered install directory plus its matching migration backup.

This retires the **local installed WPF payload**, not the historical WPF source
tree. Rollback remains under `%LOCALAPPDATA%\QingToolbox-MigrationBackups`.

The user explicitly requested waiting for a valid signing certificate/service
before public release. **No release or tag has been published.** The local
installer is still an unsigned, dirty-source upgrade-test artifact; do not
upload it or simply rename it as a signed release. The next public handoff must
commit the final source, complete the product installer/publishing cut-over,
sign and verify the final binaries/installer, regenerate hashes, and then publish.
The old WPF publishing BAT is not the Tauri publication entry point.

The first-migration test installer requires a legacy WPF executable as a guard.
It is therefore not the subsequent-update mechanism after WPF retirement.

## Historical first-migration handoff (0.1.0)

The local Tauri upgrade-test installer is an **unsigned test artifact**, not a
signed public release. It explicitly opts into the existing QingToolbox product
AppId (`{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}`) so an installed WPF copy can be
upgraded in place. The ordinary isolated Tauri installer remains separate until
the user accepts this migration.

Do not publish this test artifact. Its current Tauri version is not a new public
release version. Dirty-source test artifacts must retain their real manifest
provenance and must not be relabelled as clean production builds.

The Rust updater accepts the product AppId only together with matching Tauri
registry markers, executable, install path and a clean production manifest.
The old WPF uninstall record alone cannot enable an update handoff.

## Before retiring WPF

The user has already reported good long-running Tauri behaviour. No repeat of
the full performance or regression suite is required for this handoff. The
remaining acceptance is the actual product installation: the installed shortcut
opens Tauri, existing settings/data remain available, and Windows shows the
expected single product uninstall entry. Do not describe packaging success as
proof that this actual installation has completed.

Until that confirmation, retain the WPF source and the installation rollback
files. In particular, do not recursively delete user module or AppData trees.
Legacy DLL modules cannot be loaded by the Rust process-module runtime.

## Retirement scope after acceptance

- Replace the WPF preview publishing chain (`publish-preview-release.bat`,
  `scripts/publish-preview-release.ps1`, and
  `.github/workflows/preview-release-validation.yml`) with the Tauri product
  installer flow. Do not use the old publishing BAT to publish a Tauri build.
- Remove the WPF shell, in-process loader/ABI and dependent legacy-only projects
  after checking shared dependencies; retain shared Vue assets and module data.
- Retire `run-legacy-wpf.bat`, the old WPF build/installer scripts and their
  legacy-only CI jobs. Git history remains the source rollback path.
- Choose the public Tauri release version and complete signing with a valid
  publisher certificate or signing service. No usable code-signing certificate
  was found in the local user/machine certificate stores during this handoff.

No tag, release, remote push or production installation is performed by preparing
the test package.

## Running the local upgrade test

Build with `scripts/build-tauri-installer.ps1 -SkipBuild -LegacyUpgradeTest`
after building the current Tauri production directory. The installer and SHA256
sidecar are in `artifacts/tauri-installer/legacy-upgrade-test/`.

Exit both the installed WPF host and any development Tauri host completely,
including their tray icons, then run the installer as the same Windows user who
installed WPF. The installer refuses another `/DIR`, an absent WPF installation,
or a running host. It backs up the registered installation, `settings.json` and
the uninstall record below `%LOCALAPPDATA%\QingToolbox-MigrationBackups` before
copying the new payload. Failed backups stop installation. Reparse points are
not followed. Keep enough free space for a complete copy of the old installation.

Existing desktop shortcuts are retargeted even when no new desktop shortcut is
requested. Old WPF payloads and AppData are not removed by this first migration
package. Rollback is manual; preserve the backup and the old installer, and do
not treat uninstalling the new package as an automatic restoration of WPF.

This build retains Tauri's current `0.1.0` development version. It is not an
official downgrade/release, and its dirty-source manifest deliberately prevents
automatic production update handoff. Public versioning/signing and WPF source
retirement are still pending acceptance.
