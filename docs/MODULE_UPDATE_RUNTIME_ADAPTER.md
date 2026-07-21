# Module update runtime adapter

Phase B2.1 uses a capability-declared hybrid runtime boundary. A verified manifest, rather than
probing a view at runtime, selects one of these paths:

| Runtime isolation | UI kind | Live update |
| --- | --- | --- |
| `InProcessCollectible` | `None` | Supported; collectible ALC reclamation must be proven |
| `OutOfProcess` | `Wpf` | Supported; the dedicated worker process must be proven exited |
| `InProcessCollectible` | `Wpf` | Rejected before disk mutation (`RuntimeIsolationUnsupported`) |
| legacy/unspecified | legacy WPF | Load-compatible, but update requires restart |

The common lifecycle contract does not reference WPF. `IInProcessServiceModule` cannot export a
plugin-defined UI object. `IModuleWpfViewFactory` is consumed only inside the trusted
`QingToolbox.ModuleHost` executable; Shell never receives a plugin `Type`, `UserControl`, resource
dictionary, data template, or instance across the process boundary.

## Out-of-process WPF runtime

`ModuleProcessBroker` starts exactly one trusted ModuleHost process per WPF module. Each session
uses a current-user-only named pipe, random session name, one-time 256-bit nonce, protocol version,
a fixed command allowlist, a 16 KiB message limit, cancellation, and bounded timeouts. Handshake
validation binds the process handle and PID to module ID, manifest version, Module API version, and
the exact program-tree identity. A PID by itself is never treated as identity.

ModuleHost loads only the path supplied by the trusted Shell startup contract, verifies the same
manifest and tree identity, invokes lifecycle methods, and owns the real top-level WPF window.
`OpenWindow` creates and activates the real module view inside that process. Shutdown closes the
window, deactivates and disposes the module, and exits. If graceful exit times out, Broker terminates
that module's process tree and waits for the real process handle. IPC disconnect alone is not proof
of unload. Process isolation is a lifecycle and crash boundary, not a permissions sandbox.

## Transaction restore identity

Promoted, previous, and deferred restore use a typed request containing the desired runtime intent,
manifest version, Module API version, exact file snapshot, aggregate program-tree identity, and
restore role. The adapter re-reads and validates the installed manifest and exact files while the
transaction retains its trusted tree lease. For out-of-process WPF, the lease remains held through
worker startup, module load, activation, optional real view creation, versioned handshake, and host
identity verification. Only then may the transaction perform its final disk verification.

Diagnostics distinguish manifest version, loaded assembly informational version, loaded module
type, runtime generation, and process state without recording private paths or module content.
Startup authorization is never created, removed, or re-signed by a transaction.

## Discovery, execution, and shutdown gate

Recovery is inspected before discovery or module execution. Discovery and module updates share a
global maintenance lease, while normal execution and updates also share the existing per-module
lease. Unrelated modules remain executable during a module-scoped recovery failure. Shutdown first
publishes a no-new-work boundary, then waits a bounded interval for maintenance; timeout fails
closed instead of racing runtime disposal with directory replacement.

Production update execution and automatic qmod installation remain disabled. This infrastructure
does not add a Production update button, host self-update, tag, or release.
