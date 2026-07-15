# Changelog

## Unreleased

- Started current-user activation pipes before dependency injection and added bounded retry plus OK/ERROR acknowledgments.
- Made manual activation override pending minimized or floating-badge startup presentation.
- Split discovery, recoverable window presentation and cancellable startup-module restoration.
- Bound startup authorization to a deterministic SHA256 inventory of every module payload file.
- Legacy entry-only authorizations and changed dependencies/resources now require renewed confirmation.
- Startup presentation changes are durably saved or rolled back, and missing authorizations can be cleared.
- Added per-user single-instance activation with a restricted named-pipe protocol.
- Added opt-in HKCU Windows login startup with main-window, minimized and floating-badge presentation modes.
- Added explicit per-module startup authorization bound to manifest and entry-assembly SHA256 fingerprints.
- Startup initialization now loads and activates only matching authorizations without opening module views.
- Changed module files require renewed startup confirmation; Refresh and Import remain discovery-only.
- Uninstall removes QingToolbox's obsolete Run value while preserving settings and module data.
- Added an opt-in 68 DIP desktop floating badge from the MainWindow title bar.
- Preserved the existing Shell, module windows and module runtime state while switching modes.
- Added constrained, persisted badge placement plus localized Open and Exit controls.
- Serialized user settings updates and atomically persisted the complete settings document.
- Prevented language and floating badge position updates from overwriting one another.
- Preserved the selected monitor and monitor-local relative badge position across work-area changes.
- Serialized window-mode transitions and made Enter/Restore races with Exit safe.
- Removed the hidden MainWindow flash during Exit and prevented restoration during session shutdown.
- Changed the floating badge title-bar action to icon-only in compact windows.
- Hardened native maximize-button cancellation for capture loss, deactivation and non-client leave.
- Floating badge mode never starts automatically and does not include system-tray integration.
- Startup registration and automatic module loading remain future work.
- Centralized title-bar metrics and cached DPI-aware maximize-button hit targets.
- Added native icon single/right/double-click arbitration and capability-aware caption buttons.
- Added minimal non-client maximize click handling so `HTMAXBUTTON` executes maximize or restore exactly once.
- Open module windows now refresh localized titles without recreating views or loading assemblies.
- Empty title-bar action slots collapse, and the Shell provides a verified 500 DIP compact layout foundation.
- Desktop floating-badge mode remains future work.
- Replaced native title-bar visuals in the Shell and module host with one reusable, extensible WindowChrome title bar.
- Preserved standard window commands, resize, drag and system-menu behavior.
- Added DPI-aware `HTMAXBUTTON` handling for Windows 11 Snap Layout and an empty future action slot.
- Desktop floating-window behavior is not implemented in this change.

## 0.1.0-alpha

### Added

- Modular Shell with manual module discovery, loading, activation, deactivation and unload.
- In-process module loader with collectible `AssemblyLoadContext`.
- Module manifest discovery without loading DLLs.
- Module windows.
- Shell localization and module localization.
- Module templates with en-US and zh-CN resources.
- `.qmod` package import preview.
- TextTools, ScreenPin and WindowTopmost preview modules.
- Per-user Inno Setup installer and uninstaller.
- Start Menu shortcuts and an optional desktop shortcut.
- Uninstall behavior that preserves user modules, module data and settings.
- Localized installer tasks, shortcuts and post-install actions.
- Improved installer product and version metadata.
- Hardened self-contained installer payload validation.
- Windows CI validation for Preview release assets.
- Silent installer roundtrip coverage for uninstall data retention.
- SHA256 verification before uploading short-lived CI artifacts.
- Pinned SHA256 verification for the official Simplified Chinese Inno messages.
- Isolated no-shortcut installer roundtrip logs and failure diagnostics.
- Reusable Preview asset verification and optional CI preflight deduplication.
- Centralized Preview version, runtime, filenames, and artifact metadata.
- Immutable upstream pins for official Actions and Inno localization.
- Machine-readable release manifest with source commit, sizes, and SHA256 hashes.
- Clean-source and origin-synchronization gates for final Preview candidates.
- Schema-v2 release provenance with repository and clean-worktree assertions.
- One-command Preview candidate orchestration and manual release handoff guide.
- First-run empty toolbox onboarding across Home, Modules, and Running pages.
- Direct trusted `.qmod` import guidance and a user module folder shortcut.
- Clear in-product explanation that discovery and refresh do not load module DLLs.
- Successful imports now hand off to the selected module on the Modules page without loading DLLs.
- Empty dashboards no longer show meaningless zero statistics.
- First-run steps now use a constrained equal-width responsive layout.
- Refresh failures preserve the last consistent discovered-module state.
- Official geometric QingToolbox brand mark and nine-frame Windows icon.
- Unified Shell, taskbar, shortcut, settings, and installer branding.
- Standardized the user-visible product name as QingToolbox.

### Known Issues

- This is a Preview release, not a stable release.
- `.qmod` packages are not signed; only import modules from trusted sources.
- ScreenPin geometry, DPI and resize behavior still need refinement.
- Windows SmartScreen may warn because the binaries are unsigned.
