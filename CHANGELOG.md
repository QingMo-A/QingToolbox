# Changelog

## Unreleased

- Added strict offline `.qmod` validation and environment-isolated atomic Verified Staging without installing, replacing, loading, or activating modules.
- Bound staging work to complete package identity, serialized publication per module/version across service instances, and added strict metadata/tree tamper detection without automatic deletion or overwrite.
- Replaced named staging semaphores with crash-recoverable exclusive file handles, added real child-process contention/crash tests, stable-handle path attestation, Release identity binding, post-move quarantine, and disposal/capacity scheduling contracts.
- Added hostile archive, ZIP bomb, manifest identity, concurrency, cancellation, and no-DLL-execution staging smoke coverage.
- Added a lightweight per-session log viewer with persistent, privacy-conscious log files and environment-aware sidebar visibility.

- Prepared the unified 0.2.0-alpha Preview 2 product and installer metadata.
- Added deterministic host payload ownership manifests and exact obsolete-file cleanup inputs.
- Added in-place Preview upgrade, same-version repair, and SemVer downgrade-guard infrastructure.
- Reused validated custom installation directories without `/DIR`, closed the matching old Shell through Restart Manager, and restored it after successful upgrades.
- Honored legal explicit `/DIR` selections over conflicting discovered records while rejecting empty, relative, remote, and protected-root destinations without fallback.
- Revalidated the directory selected by the installer wizard immediately before installation and restored only a Shell running from that final target.
- Prevented missing Task Scheduler entries and failed registration refreshes from escaping the startup-settings command and terminating the Shell.
- Restricted production-AppId installer roundtrip tests to disposable GitHub Actions Windows profiles so local tests cannot overwrite a real uninstall registration.
- Preserved settings, user modules, module data, caches, startup authorizations, and unknown install files across upgrades.

- Serialized startup registration mutations and made rollback cancellation-safe.
- Preserved exact owned Task Scheduler definitions during failed transactions.
- Distinguished startup-test failures from timeouts and cleaned partial test tasks.
- Reported module discovery degradation truthfully and moved authorization hashing off the UI thread.

- Moved activation IPC ahead of settings and service initialization.
- Prevented duplicate preferred and fallback login tasks.
- Added correlated on-demand startup tests based on visible readiness.
- Isolated module discovery from the WPF dispatcher and auxiliary startup failures.

- Fixed root-folder Task Scheduler fallback discovery and execution.
- Made startup registration changes transactional across Task Scheduler, Registry Run and settings.
- Added truthful phase outcomes and durable startup journal flushing.
- Removed owned startup tasks during uninstall and repaired safe path drift after upgrades.

- Added resilient per-user Task Scheduler login startup with registry fallback.
- Moved visible startup presentation ahead of module discovery and restoration.
- Added startup health journaling, repair and test diagnostics.
- Preserved external user disable decisions and least-privilege execution.

- Corrected shared-download cancellation semantics and complete package identity binding.
- Preserved committed verified packages across auxiliary record failures.
- Added transfer inactivity timeout and prevented stale results from attaching after refresh.

- Added verified manual module package downloads with fresh metadata confirmation.
- Added streaming size and SHA256 validation with environment-isolated verified package storage.
- Kept downloaded packages staged only: no extraction, import, installation, or replacement occurs in this phase.

- Selected the highest compatible module release before reporting compatibility blockers.
- Propagated stale index provenance into module results.
- Decoupled cache persistence failures from valid metadata responses.
- Counted only version-matched update results in the UI.

- Serialized automatic and manual update checks and bound results to the checked local module version.
- Replaced split cache files with transactional cache envelopes and refreshed freshness timestamps after HTTP 304 responses.
- Added an explicit maximum-host incompatibility status and rejected encoded metadata path bypasses.

- Added read-only official module update detection with isolated ETag and Last-Modified caches.
- Added module compatibility and availability states without downloading or installing packages.
- Disabled real update checks and cache creation in ModuleTest environments.

- Bound Development and ModuleTest sandboxes to an explicitly validated QingToolbox repository root.

- Hardened local environment parsing, enforced repository-local sandbox layouts, rejected reparse-point escapes, and preserved `WhatIf` during forced Profile resets.

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
