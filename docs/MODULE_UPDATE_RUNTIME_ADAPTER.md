# Module update runtime adapter

Phase B2.1 connects the frozen B1 transaction contract to the existing Shell runtime. The adapter
is `QingToolbox.Shell.Services.ModuleUpdateRuntimeCoordinator`; it reuses `ModuleRuntimeManager`,
`ModuleWindowManager`, settings, manifest validation, localization, and the existing collectible
load context. Core does not reference WPF and Shell does not reference a concrete module.

The adapter reads window, loaded, active, and startup-authorization state without loading a DLL.
Window close is targeted, Dispatcher-bound, cancellable, bounded, and idempotent. Deactivate and
Unload reuse the existing lifecycle. Unload verification requires no runtime registration, no
active state, no module window, and a collected ALC weak reference; failure returns `false` and
prevents B1 from replacing the program directory.

Restore is idempotent and re-reads the installed manifest. An unloaded module stays unloaded; a
loaded inactive module is loaded only; an active module is loaded and activated. A single window is
restored only when requested. Startup authorization is never created, deleted, or re-signed by a
transaction. Diagnostics contain module ID, version, hashed directory identity, payload fingerprint,
and load-context generation—not private paths or module content.

## Startup recovery gate

After first presentation, Development and ModuleTest run transaction recovery before discovery and
startup-authorized DLL execution. Recovery-time runtime restores are deferred, so recovery itself
cannot execute module DLLs. The gate is published, discovery runs, and safe deferred intent is then
restored before normal startup authorization.

Known `RecoveryRequired` journals block only their module ID. An unattributed journal problem fails
closed for all module execution while keeping Shell visible. Lifecycle commands and the Development
transaction coordinator share the same per-module lease, closing the VerifyUnloaded-to-rename race.
Production registers the adapter and gate but does not register executable update transactions.

This is B2.1 infrastructure, not a Production update feature. There is no Production update button,
automatic qmod installation, background updater, host self-update, tag, or release.
