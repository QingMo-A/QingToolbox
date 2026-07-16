# Changelog

## Unreleased

- Added project-local Development and ModuleTest profiles under `.qingtoolbox`.
- Isolated development instances, settings, modules, module data, and activation scopes from Production.
- Prevented sandbox environments from reading or modifying Production login-startup registration and user data.

- Hardened explicit background shutdown so module-window, module-runtime, activation-pipe, badge, and notification-area cleanup failures cannot block final application shutdown.
- Made notification-area initialization transactional and retryable, and observed dispatcher failures without permanently disabling tray actions.
- Preserved suspended module windows when switching directly from the notification area to the floating badge.

- Added a localized Windows notification-area icon with Open, Settings, Floating Badge and Exit actions.
- Added a persisted main-window close preference with an explicit first-close choice and an Ask-again option.
- Unified native close routes, secondary-instance activation, floating-badge exit and tray exit around recoverable window and clean shutdown coordination.
- Kept a visible recovery surface while running and removed the notification icon during clean exit.
- Contained startup cancellation during shutdown and stopped activation pipes before service disposal.
- Acknowledged accepted single-instance messages before UI activation and isolated handler/client failures.
- Enforced strictly bounded pipe reads and kept the server available after malformed or disconnected clients.
- Reduced startup restoration to one final full-payload validation immediately before each module load.
- Localized startup-authorization write failures and preserved authorization counts when cleanup fails.
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
- PowerGuard remains a separately distributed `.qmod` on the modules branch and is not bundled with the host release.
- Centralized title-bar metrics and cached DPI-aware maximize-button hit targets.
- Added native icon single/right/double-click arbitration and capability-aware caption buttons.
- Added minimal non-client maximize click handling so `HTMAXBUTTON` executes maximize or restore exactly once.
- Open module windows now refresh localized titles without recreating views or loading assemblies.
- Empty title-bar action slots collapse, and the Shell provides a verified 500 DIP compact layout foundation.
- Replaced native title-bar visuals in the Shell and module host with one reusable, extensible WindowChrome title bar.
- Preserved standard window commands, resize, drag and system-menu behavior.
- Added DPI-aware `HTMAXBUTTON` handling for Windows 11 Snap Layout and an empty future action slot.

## 0.1.0-alpha

### Added

- Modular Shell with discovery-only Refresh/Import, manual lifecycle controls, and explicit fingerprint-bound startup authorization.
- In-process module loader with collectible `AssemblyLoadContext`.
- Module manifest discovery without loading DLLs.
- Module windows.
- Shell localization and module localization.
- Module templates with en-US and zh-CN resources.
- `.qmod` package import preview.
- Separately distributed TextTools, ScreenPin and WindowTopmost preview modules; none are bundled with the host release.
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
