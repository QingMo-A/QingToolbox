# Windows startup reliability

QingToolbox uses a current-user Task Scheduler 2.0 logon task as its preferred login-startup backend. HKCU `Run` remains a compatibility fallback because Windows may defer traditional startup applications while it manages sign-in load. Neither backend elevates privileges, stores a password, creates a service, or runs as SYSTEM.

## Registration contract

The preferred task is stored under `\QingToolbox` when the current user can create that folder. Its name is `Startup-<SID hash>`; the stable SHA256 prefix isolates users without exposing the full SID. If folder creation is unavailable, a clearly named current-user root task is used. The task uses a current-user `LogonTrigger`, `InteractiveToken`, least privilege, `IgnoreNew`, no artificial delay, no idle/network requirement, battery operation enabled, `PT0S` execution limit, and at most three Task Scheduler retries at one-minute intervals.

Enabling startup first writes and reads back the task definition. The old Registry Run value is removed only after verification succeeds. If Task Scheduler COM or policy is unavailable, QingToolbox explicitly records Registry compatibility mode and displays that Windows may delay it. Disabling startup removes and verifies both registrations. The two backends are never intentionally active together.

External user choices take priority. A task disabled in Task Scheduler is reported as `DisabledExternally` and is not silently re-enabled. A missing task is reported as `Missing`; neither state starts an infinite repair loop. Repair occurs only after the user requests it. The application never bypasses the Windows Startup Apps controls.

## Critical startup path

The visible critical path is `ProcessEntry → InstanceReady → MinimalServicesReady → NotificationAreaReady → PresentationReady`. A bounded settings reader obtains only language, login-startup preference, presentation mode, and close behavior with a 1 MiB limit and safe fallbacks. Manual launches fall back to the main window; automatic launches fall back to the floating badge.

Module directory scanning, module localization registration, complete payload SHA256 verification, authorized module restoration, update checks, qmod work, and network access begin only after `PresentationReady`. This preserves the existing startup-module fingerprint and TOCTOU checks while preventing module count or a slow disk from delaying the first accessible UI. A manual single-instance `Activate` request is handled by the pipe immediately; `StartupProbe` confirms an already-running instance without forcing its window open.

`PresentationReady` means a user-accessible presentation exists. `Ready` additionally means discovery and authorized-module restoration completed. Badge failure falls back to a recoverable main window. Notification-area initialization uses a bounded retry; if both background entry points fail, the main window remains visible. No infinite internal restart loop is used.

## Startup health journal

The local `Startup/startup-health.json` journal stores at most ten small records: attempt/source identifiers, UTC phase timestamps, monotonic elapsed durations, secondary-instance state, failure phase/code, and exit code. Writes are atomic, bounded, ignored on corruption, and never block visible presentation. It contains no password, token, full executable path, telemetry upload, or network operation.

An attempt with no `PresentationReady` is a visible-start failure; one with presentation but no `Ready` is incomplete; one with `Ready` is successful. Fatal pre-presentation startup uses a nonzero exit code so Task Scheduler can apply its finite retry policy. Module, update, journal, or other post-presentation auxiliary failures do not terminate the host.

## Ownership, transactions, and recovery

The owned identity contains both `\QingToolbox\Startup-<SID hash>` and the root-folder fallback `\QingToolbox-Startup-<SID hash>`. Root paths are split as folder `\` plus the task name, so fallback tasks can be read, run, and removed. No non-owned task is enumerated or deleted.

Presence and health are separate. `Absent` is a confirmed missing registration, `Present` is a confirmed task or Run value (including externally disabled or changed definitions), and `Unknown` means access or backend availability prevented a truthful answer. Unknown is never treated as registered or safely removed. When cleanup cannot be confirmed, settings retain `StartupRegistrationCleanupPending`; a later post-presentation reconcile retries only owned cleanup.

Task verification requires exactly one enabled current-user Logon trigger and exactly one Exec action, matching executable, arguments, and working directory, an InteractiveToken least-privilege principal, IgnoreNew, battery permission, no idle/network requirement, no wake request, demand start, `PT0S`, and the finite `3 × PT1M` retry policy. Account names and SID strings are normalized to a `SecurityIdentifier` before comparison. Extra actions or triggers are `DefinitionChanged` and are never silently overwritten.

Registration changes capture the real task definition, raw Registry Run command, and startup settings before mutation. Task creation is read back before Run removal. A failed verification, Run cleanup, or settings write restores the captured values; an incomplete rollback is reported rather than declared successful. Registry fallback remains in place until explicit Repair migrates it. Externally disabled tasks are never automatically re-enabled.

Each journal change creates an immutable snapshot under a lock and enters one serial write queue. Older snapshots cannot overwrite newer stages. Atomic replacement, stale-temp cleanup, the 256 KiB limit, and `FlushAsync` provide bounded durability; shutdown waits at most two seconds. Phase outcomes are `NotReached`, `Succeeded`, `Degraded`, or `Failed`. Notification initialization records `Degraded` without a false ready timestamp and becomes `Succeeded` only after a real retry succeeds.

The notification-area service listens for the registered `TaskbarCreated` broadcast. After Explorer restarts it replaces the stale NotifyIcon while preserving localized menus and callbacks, and performs no recovery after application exit begins.

Safe executable or working-directory drift on an otherwise owned, enabled, structurally valid task may be repaired after presentation. An externally disabled or structurally modified task remains untouched until explicit Repair. The installer invokes the headless `QingToolbox.StartupMaintenance --remove-owned-startup` tool before deleting files. It shares the owned identity implementation, removes both owned task paths and HKCU Run, performs no network or module loading, and preserves settings, user modules, and module data.

StartupTest is distinct from a normal login record and is not a security authorization. Real sign-in timing, five repeated logins, Explorer restart, external disable, root fallback, Registry fallback, moved-install upgrades, and post-uninstall inspection remain manual Windows acceptance work; no remote telemetry is uploaded.

## Environments and troubleshooting

Production may register the real task. Development and ModuleTest are isolated and cannot touch Task Scheduler, HKCU Run, production AppData, network update sources, or production modules. The Startup Reliability Smoke Test uses fake stores only.

To investigate “did not start,” open Settings → Startup health, refresh status, copy diagnostics, and open Task Scheduler. Check the current-user QingToolbox task, its enabled state, last run time/result, action path, working directory, and Windows Settings → Apps → Startup. Use **Test startup** for a run-on-demand probe and **Repair startup** only when you intend to restore the registration. Explorer restart, sign-out/login timing, battery behavior, external disable, moved executable paths, and repeated cold-start performance remain manual Windows acceptance tests; no startup telemetry is uploaded.
