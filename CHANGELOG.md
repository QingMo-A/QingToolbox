# Changelog

本文件按时间倒序记录 QingToolbox 的版本变更：最新版本在最上方。

- 各版本的完整发布说明见 [`docs/releases/`](docs/releases/)。
- Tag、安装包与 SHA256 sidecar 见 [GitHub Releases](https://github.com/QingMo-A/QingToolbox/releases)。
- 当前发行线状态与路线见 [`docs/DEVELOPMENT_PLAN.md`](docs/DEVELOPMENT_PLAN.md)。

## Unreleased

### 0.3.1-alpha（开发中）

- 宿主版本前进到 `0.3.1-alpha`，开始 0.3.0-alpha 之后的迭代。
- 修复 Windows 签出时的固定 qpdf 校验字节比较，避免换行符转换导致校验失败。
- 在发布验证中恢复 Tauri 宿主依赖，确保 Release 校验链完整。
- CI 发布烟测改为非交互执行，避免流水线等待输入。
- 恢复独立的 Tauri 模块交付路径。
- Real-user installation, upgrade, Repair, uninstall, signature/SmartScreen, and representative-environment acceptance remain unrecorded or CI-only until the acceptance checklist is completed.

## 0.3.0-alpha - 2026-09-25

首个 Rust/Tauri 2/Vue 3 宿主预览，在原有 QingToolbox Windows 产品身份下取代 WPF 宿主。**未签名**，Windows 可能提示未知发布者或触发 SmartScreen。

- 模块改为进程隔离：每个模块运行在独立 Rust 进程中，通过版本化 JSON 行协议与宿主通信；旧 WPF DLL 模块不兼容。
- 提供独立的 Load / Enable / Disable / Unload / Delete 动作；打开界面与一次性操作不会隐式启用后台工作。
- 全新 Vue 工作区：自定义标题栏、无边框悬浮标，以及改进的字体应用与模块窗口行为。
- 随包交付原生模块：Qing Launcher 0.3.0、QingTransfer 0.3.0、Screen Pin 0.2.0、Window Topmost 0.2.0、PowerGuard 0.2.0、Text Tools 0.2.0、Qing PDF 0.1.0，以及用于验证的 Web Module Canary 0.1.0。
- Qing Launcher 0.3.0：居中透明浮层、桌面/自定义/首字母视图、拖拽排序与文件夹、原生图标加载、快捷键录制与内置 Everything 搜索反馈。
- 沿用既有 QingToolbox 产品 AppId，因此可原地覆盖已有的 WPF 或 Tauri 安装，而不是产生第二条卸载记录。首次从 WPF 迁移时，安装器会先备份已注册安装与共享设置，并退役旧宿主的自启动注册。
- 用户设置与模块数据保留。安装器保留 Tauri 载荷之外的旧 WPF 文件以便回滚；备份位于 `%LOCALAPPDATA%\QingToolbox-MigrationBackups`，确认迁移结果前请勿删除。
- 安装包：`QingToolbox-0.3.0-alpha-win-x64-tauri-setup.exe` 及同名 SHA256。
- 已知限制：未代码签名；真实用户 WPF 升级、修复安装、卸载与 SmartScreen 行为可能因环境而异。旧 WPF 模块更新目录不重定向到这些不兼容的原生包。

## 0.2.9-alpha - 2026-08-21

继已发布 `v0.2.8-alpha` 之后的宿主独立预览版本。重点降低长时间后台运行后恢复主面板、悬浮标与 Web 模块时的卡顿，并在 Production 中开放由宿主完整验证、用户明确确认的官方模块覆盖更新。

- 通过挂起隐藏的 Shell 与模块 WebView2 表面、暂停隐藏的原生动画、合并延迟的模块事件，并限制模块窗口生命周期的并发，减少前台恢复卡顿。
- 允许在 Production 与 Development 中，依据宿主授权的官方元数据对已安装模块执行用户确认更新：包含大小与 SHA256 校验、严格 `qmod.json` 暂存、运行时停用、原子替换、回滚与冷启动恢复。
- 接受省略或显式为 null 的最大宿主版本约束，同时继续严格拒绝畸形边界。
- 沿用针对上一公开版本 `v0.2.8-alpha` 的宿主自有资源精确清理；用户设置、模块、数据、缓存与未知文件不在清理范围内。
- 宿主 Release 继续保持安装器独占；具体模块（含 Qing Launcher 0.2.2）继续以 `.qmod` 独立交付，不随宿主安装器或宿主 Release 捆绑。
- 发布证据：预交接源代码提交 `d4b30d5d9672fc54f0b08446157d2456406c59d1`；Preview 验证运行 `32462342007` 通过，覆盖安装器往返、Repair、Web Ready 握手、用户状态保留与 `v0.2.8-alpha → v0.2.9-alpha` 原地升级。已验证安装器 `QingToolbox-0.2.9-alpha-win-x64-setup.exe`，58,299,475 字节，SHA256 `F5496DF3479322D6F46544BEBE78B22DF5DBFED39FD8BE5FE22EFF9212142CC7`。
- 已知限制：仍未签名，SmartScreen 可能告警；官方模块更新由用户发起，不支持自动安装或同 ID 本地 `.qmod` 覆盖。

## 0.2.8-alpha - 2026-08-16

- Added optional Web Module host window presentation and controlled external file-drop hooks.
- Preserved Standard Web module windows while enabling transparent Overlay modules without changing the Web bridge protocol.
- Preserved the host-owned WebUI asset cleanup and acknowledged Ready handshake introduced in `v0.2.7-alpha`.
- Kept concrete modules independently distributed as `.qmod` packages outside the host installer and host Release.
- 已知限制：仍未签名；模块自动安装与宿主安装器无关，需独立进行。

## 0.2.7-alpha - 2026-08-15

- 修复安装器升级遗留陈旧哈希 WebUI 资源、进而触发原生 WPF 回退的问题。安装器现在拥有并维护不可变的 WebUI 资源树，只清理上一宿主 payload manifest 中记录的废弃文件。
- 升级验证要求 Web Shell 完成既有的 Ready 握手；仅显示可响应的原生回退不算升级成功。
- 用户设置、模块、模块数据、缓存与未知用户文件不在宿主自有清理范围内。
- 具体模块不随宿主安装器或宿主 Release 捆绑，继续以 `.qmod` 独立交付。
- 已知限制：仍未签名；Production 模块自动安装尚不可用；Web 资源确实损坏或不可用时，按设计仍回退到原生 WPF 工作区。

## 0.2.6-alpha - 2026-08-15

> 该版本没有独立的发布说明文件（`docs/releases/` 中缺失 `0.2.6-alpha.md`）。以下条目依据 `v0.2.5-alpha..v0.2.6-alpha` 提交区间整理。

- 建立 Web 模块 UI 宿主基础：模块后端桥、加载表面与图标、呈现上下文同步、关闭后重开，以及事件编组到 UI 线程。
- 新增 Web 模块窗口的 overlay 呈现方式并保持置顶，同时不改动普通 Web 模块窗口行为与 Web 桥协议。
- 修复隐藏 WebView 的加载死锁，以及预览升级后 Web 工作区未能保持就绪的问题。
- 新增已安装 Web Shell 的回退诊断，并按最新结果归类其日志。
- 修复本地启动器自修复行为。
- 记录 Web 模块宿主版本边界与模块适配检查，并加入模块计划索引与 Android 移动端壳层路线文档。
- 已知限制：仍未签名；Production 模块自动安装尚不可用。

## 0.2.5-alpha - 2026-08-11

- Added five composable appearance presets with distinct shared-control states across light and dark modes.
- Added searchable system-font selection, managed font-file imports, a cached installed-font catalog, and an explicit refresh action.
- Synchronized font and appearance changes with the native title bar and retained a reliable Restore default action.
- Projected validated module SVG icons into the Vue workspace with safe fallbacks.
- Refined sidebar icon alignment, caption-button interactions, window corners, and floating-badge placement restoration.
- Added a double-confirmed GitHub Actions release hand-off and hardened it for prerelease discovery, clean worktrees, native stderr, and `v`-prefixed tags.
- Updated the installer upgrade gate and obsolete-host cleanup baseline for `v0.2.4-alpha` to `0.2.5-alpha`.

## 0.2.4-alpha - 2026-08-01

- Allowed Windows Restart Manager maintenance shutdowns to bypass the normal close-to-tray preference and run the existing orderly exit pipeline.
- Added a controlled installer force-close fallback for older QingToolbox versions that cannot yet recognize maintenance shutdown messages.
- Expanded installer in-use detection to the Shell, ModuleHost, and StartupMaintenance host executables.

## 0.2.3-alpha - 2026-08-01

- Synchronized the native WPF title bar with the Vue workspace light, dark, and system appearance modes.
- Kept theme notifications constrained to the verified local WebView session without changing Bridge protocol v4.
- Added focused Web and host smoke coverage for title-bar theme notification validation.
- Updated installer cleanup for the published 0.2.2-alpha payload during in-place upgrades.

## 0.2.2-alpha - 2026-07-31

- Enabled the verified Vue workspace for Production while retaining the native WPF workspace as the safe initialization and process-failure fallback.
- Kept Development Diagnostics and verified module-update installation out of the Production Web workspace.
- Preserved the host-owned self-update flow through a Production Vue banner and Settings panel without duplicating installer verification in the Web layer.

## 0.2.1-alpha - 2026-07-31

- Prepared the unpublished 0.2.1-alpha installer-only release candidate and synchronized host version metadata.
- Documented the candidate's safe host update flow, genuine recent-module shortcuts, and starter module set.
- Restored the published `v0.1.0-alpha` installer as the real upgrade baseline; `0.2.0-alpha` remains an unpublished internal Preview 2 target.
- Clarified that TextTools, PowerGuard, and WindowTopmost are separately prepared `.qmod` candidates and are not bundled with the host installer or host Release.

- Updated the native home to show up to five genuinely recent modules with quick Open and Details actions.
- Recent modules on the native home can now load, activate, and open in one explicit launch action.
- Added Production-native discovery of higher official QingToolbox GitHub Releases with SemVer channel selection, installer-only asset validation, conditional caching, and a non-blocking update banner.
- Added explicit Production installer download and SHA256 verification with bounded streaming, cancellation, cache revalidation, and a native ready state.
- Added installed-deployment verification, final installer revalidation, user confirmation, and safe `/SILENT /NORESTART` handoff to the existing Inno Setup in-place upgrade path.
- Retired portable ZIP distribution; future Windows releases publish only the installer and its same-name SHA256 sidecar while retaining audited installer payload generation.
- Fixed host self-update identity checks to fail closed on malformed installation records and replaced installer or checksum assets.

- Localized the Development Web Home, Session Logs, and Diagnostics workspaces in English and Simplified Chinese.
- Localized the Development Web Modules and Running workspaces in English and Simplified Chinese.
- Added reactive English and Simplified Chinese localization to the Development Web shell, navigation, and Quick Open.
- Completed English and Simplified Chinese localization for the Development Web workspace and synchronized localized module metadata after language changes.
- Exposed all host-confirmed lifecycle actions directly on Development Web module cards.
- Added host-confirmed language selection to the Development Web Settings workspace.

- Fixed Development Web module summaries and filters to classify unloaded, deactivated, failed, invalid, and issue-bearing modules consistently.

- Fixed the Development Web module workspaces to preserve last confirmed snapshots and show safe errors during host communication failures.

- Added a Ctrl+K Quick Open palette for navigating the Development Web workspace and opening module details.

- Added a host-confirmed repair action for unhealthy Windows startup registrations in Development Web Settings.
- Added a host-confirmed Launch at login control to the Development Web Settings workspace.
- Improved the Development Web diagnostics workspace with clearer host state, safe checks, and stale-snapshot handling.
- Improved the Development Web session logs workspace with severity summaries, local filtering, and stale-snapshot handling.
- Reorganized the Development Web settings workspace into focused General, Window, Startup, and About sections while preserving existing host-backed settings.
- Added an actionable Development Web home dashboard with module health, running-module previews, and workspace navigation.
- Added fingerprint-bound module startup authorization to the Development Web module details workspace.
- Added a host-confirmed Open action that opens or focuses native module windows from the Development Web workspace.
- Added host-confirmed Deactivate and Unload actions to the Development Web module center and running-module view.
- Added host-confirmed Load and Activate controls to the Development Web module center.
- Enabled the Development Settings workspace to persist the startup presentation preference through the host.
- Enabled the Development Settings workspace to persist the main window close behavior through the host.
- Enabled the Development Settings workspace to persist the Logs navigation preference through the host.
- Added a read-only Settings workspace that projects the current host configuration and mirrors the native Logs navigation preference.
- Added an activated-session, read-only Session Logs workspace backed only by the current in-memory log entries.
- Added a read-only Running workspace that mirrors the native Shell layout and links active modules to their details.
- Added a modern Development Web Shell workspace with Home, collapsible navigation, retained
  diagnostics, local system/light/dark preview, and a focused token-based Qing component foundation.
- Added an activated-session-only `modules.getSnapshot` projection and read-only module list,
  search, state filters, safe details drawer, refresh feedback, loading, empty, and error states.
- Kept the module projection free of paths and lifecycle commands and added focused frontend and C#
  coverage for validation, filtering, repeated refresh, accessible drawer behavior, and bridge gates.

- Upgrade the Development Web Shell to protocol v4 with one-use activation nonces and
  generation-scoped session tokens shared by the Host and Mock state machines.
- Serialize WebView recovery and serve verified Web assets exclusively from a bounded immutable
  memory snapshot with explicit reparse-point rejection.
- Extend repeated-ping, stale-handler, asset TOCTOU, resource-limit, and PowerShell generator tests.

- Require a protocol-v3, generation-scoped activation challenge and nonce-bound ping before the
  Development Web Shell can replace the native recovery workspace.
- Compile the verified Web asset manifest identity into the Shell and reject runtime manifest,
  file-set, file-hash, extra-file, or self-consistent replacement tampering before WebView creation.
- Bind process-failure recovery to Core/Generation/Session and coalesce duplicate stale failures.

- Gate the Development Web Shell on protocol-v2 ready identity, current WebView generation,
  authoritative snapshot, and ping instead of source assignment or raw JSON text matching.
- Add explicit transport disposal, opt-in browser Mock, strict CSP/network denial, deterministic Web
  asset manifests, and package-time integrity checks.
- Add a bounded real Development WebView2 canary and verify non-Mock navigation, trusted ready,
  authoritative snapshot, and ping/pong locally.

- Added a Development-only WebView2 + Vue 3 workspace while preserving native WPF for Production,
  ModuleTest, and initialization/process failure recovery.
- Established Qing Bridge protocol v1 with three read-only commands, C# authoritative snapshots,
  strict local navigation/capability denial, isolated profiles, and explicit mock mode.
- Added deterministic Web/native tests and build, portable, installer, and CI integration without
  committing generated assets or weakening frozen B1/B2.1 gates.

- Closed the ModuleHost publication race with publish-then-observe registration, immediate process
  verification, a state round-trip, single-fire exit cleanup, and deterministic early-exit canaries.
- Made multi-worker suspend/restore exhaustive and unified notification-area and floating-badge
  compensation so the main Shell remains a recovery surface after partial failure.
- Reordered module removal to delete program files before startup authorization and added typed
  complete, program-failure, and authorization-cleanup partial-success outcomes.

- Hardened post-freeze out-of-process host integration with complete response identity checks,
  generation-safe crash cleanup, safe running-module removal, and non-recreating window suspend/restore.
- Rejected partial or unsupported manifest runtime capability declarations during discovery while
  retaining legacy manifest compatibility.
- Extended the real TextTools canary to cover worker-window suspend/restore and unexpected-exit restart.

- Added a manifest-declared hybrid module runtime: collectible in-process services and dedicated
  trusted ModuleHost processes for real WPF views.
- Bound ModuleHost IPC to protocol, nonce, process handle, module/API/tree identity, command
  allowlisting, bounded messages, graceful exit, and process-tree kill fallback.
- Extended the real TextTools canary across distinct v1/v2 processes, real WPF windows, success,
  rollback, RecoveryRequired, trusted tree leases, and Development/ModuleTest profiles.
- Serialized discovery, update, and shutdown maintenance while preserving unrelated module
  execution and rejecting in-process WPF live updates before disk mutation.

- Started B2.1 with a real Shell module-update runtime adapter, per-module recovery gate, deferred cold-start runtime intent, and version-aware ALC diagnostics.
- Added real pinned TextTools Development/ModuleTest commit, rollback, and RecoveryRequired canaries without enabling Production installation.
- Added module-attributed recovery issues so known failures block only their module while unattributed journals fail closed globally.

- Bound every transaction destination to a leased parent handle plus validated relative leaf, including same-handle Journal temp replacement, with no path-move fallback.
- Added bounded secure tree leases, same-read manifest capture, post-snapshot mutation rejection, and immediate exact post-rename verification around the Windows descendant-handle rename constraint.
- Upgraded transaction journals to schema 4 with a strict legacy schema 3 migration path and fail-closed handling for ambiguous shapes and sites.
- Preserved process-local promoted-runtime side-effect knowledge across progress persistence faults and quiesced partial/successful v2 restores from actual runtime state before disk rollback.
- Extended required Windows smoke coverage for destination ancestors, Journal temp replacement, installed/candidate/backup lease races, runtime fault windows, legacy migration, and genuine mid-copy FailFast recovery.
- Marked the Development/ModuleTest-only B1 transaction core Engineering Complete — Frozen; Production lifecycle integration and the TextTools canary remain B2 work.
- Replaced all security-critical transaction directory moves with identity-attested, handle-bound Win32 renames and stable destination-parent leases.
- Split promoted-runtime and previous-runtime recovery progress so pre-commit rollback quiesces v2 before restoring v1 on disk and in memory.
- Added double-pass exact tree snapshots, live lock/journal parent replacement tests, deterministic rename races, post-restore cancellation, and a candidate-copy crash window.
- Bound Development/ModuleTest update transactions to the host-configured Verified Staging attestor, added directory identities to the then-current schema 3 journals, and rejected rogue staging roots.
- Added Windows volume/File-ID ownership across candidate promotion and backup recovery, stable-handle journal I/O, physical LocksRoot binding, and shared no-follow tree traversal.
- Added rogue-root, directory replacement, journal/lock junction, pre-lifecycle reparse, and corrupt-promoted-candidate crash recovery coverage.

## Historical development log (untagged)

> 以下条目在 0.2.x 发行线被拆分并打 Tag 之前累积，原样保留以便追溯。它们不属于任何已发布版本。

- Preserved installed ownership markers and backups until the committed journal is durable, and made every post-commit cleanup failure non-rollbackable and recoverable.
- Unified staging and transaction locks on crash-recoverable exclusive Windows handles, added stable-handle staging-to-candidate copying, strict tree verification, reserved module identities, and physically isolated roots.
- Isolated strict journals per physical root/environment/module, replaced fixed temp files and recursive deletion, and covered five real copy/promotion/commit crash windows plus hostile ownership and journal cases.
- Added the Development/ModuleTest-only recoverable module update transaction core with strict durable journals, physical-root locks, same-volume candidates, atomic directory promotion, rollback, and crash recovery.
- Added immutable Verified Staging attestations bound to the physical Verified root and official release identity, with exact re-attestation before transaction mutation.
- Added deterministic lifecycle/filesystem failure coverage and a real child-process crash recovery smoke test required by Windows CI.
- Added strict offline `.qmod` validation and environment-isolated atomic Verified Staging without installing, replacing, loading, or activating modules.
- Bound staging work to complete package identity, serialized publication per module/version across service instances, and added strict metadata/tree tamper detection without automatic deletion or overwrite.
- Replaced named staging semaphores with crash-recoverable exclusive file handles, added real child-process contention/crash tests, stable-handle path attestation, Release identity binding, and disposal/capacity scheduling contracts.
- Made the atomic Verified directory move the sole publication commit point: Incoming candidates now pass full stable-handle attestation before the move, while post-commit cancellation and diagnostic lock-marker cleanup cannot rewrite success.
- Linearized caller cancellation with the synchronous Verified move, bound publication locks to the attested physical Staging root, and added distinct unsafe user-module-root configuration failures.
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
