# Tauri module lifecycle

Residency and activation are separate. Installing/removing packages is a third concern.

| Action | Result | Background work | Installed files |
| --- | --- | --- | --- |
| Load | Loaded; process and state resident | Off | Kept |
| Enable | Running; same process/generation | On | Kept |
| Disable | Deactivated; same process/generation | Off | Kept |
| Unload | Stopped; process and Web surface released | Off | Kept |
| Delete | Unload, then remove validated user-package directory | Off | Removed |

Opening the UI and explicit one-shot operations are available while resident;
they do not implicitly enable background work. An unloaded module cannot be
reloaded by stale UI calls, events, or queued hotkey callbacks. Startup-authorized
modules explicitly load **and** enable. Disable does not revoke a user's separate
startup authorization.

## Process contract

- A nonce-bound `module.hello.response` advertises `lifecycleVersion: 1`.
- Before activation, constructors/hello only initialize resident data. No background
  listeners, discovery or recurring monitor may start here.
- Host sends `module.lifecycle.request`, payload `{ "active": true/false }`.
- Module completes the transition, then replies `module.lifecycle.response` with
  the same request ID and `{ "active": true/false }`. Repeated transitions are safe.
- Disable cancels/drains continuous work and timers but retains settings, caches
  and other resident state. Operations that would restart continuous work must
  reject while inactive. Re-enable must work without restarting the process.
- Unload first deactivates, then sends `module.shutdown.request`; process exit
  also cleans up owned resources. Failed acknowledgements are never projected as
  successful disable. Protocol corruption/timeout is a visible failure, not disable.
- Legacy modules without the lifecycle contract fail clearly and require an update;
  the host must not pretend that terminating an old module is a disable operation.

Launcher registers its global hotkey only while enabled and stops only its owned
Everything client on disable (the separately installed Windows service is not
uninstalled). QingTransfer drains discovery/listener workers and disconnects;
PowerGuard joins its monitor and cancels countdowns. One-shot tools acknowledge the
same contract without creating a background worker just to be considered enabled.

## Regression checks

`TauriTransport.test.ts` covers independent command routing/action availability.
Host runtime tests use `QING_TAURI_CANARY_PATH` to check stable process ID/generation,
idempotent enable/disable, resident calls after disable, and no implicit reload.
`scripts/test-tauri-module-lifecycle.mjs` exercises the packaged native modules
using isolated temporary data; it never modifies the user's module configuration.
