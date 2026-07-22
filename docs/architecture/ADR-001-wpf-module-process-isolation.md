# ADR-001: WPF module process isolation

Status: Accepted

## Context and evidence

The real pinned TextTools canary created its actual WPF `UserControl` in a real `ModuleHostWindow`.
After the window closed, Content and DataContext were cleared, the Dispatcher reached idle, lifecycle
cleanup completed, and repeated collections ran. WPF/BAML still retained plugin-defined types, so the
collectible load context remained alive. The transaction correctly returned `ModuleStillLoaded` and
rolled back without replacing files used by that runtime.

## Options considered

1. Clear private WPF/BAML/TypeDescriptor caches with reflection. Rejected: private implementation
   details are process-global, unsupported, and unsafe around unrelated windows.
2. Treat logical unload or repeated GC as physical unload. Rejected: it weakens the verified unload
   boundary and permits replacement while executable code may remain live.
3. Run plugin-defined WPF UI in a trusted, one-module-per-process host. Accepted: process termination
   is an OS-verifiable lifetime boundary and isolates crashes without sharing a visual tree.

## Decision

`InProcessCollectible + UiKind.None` is eligible for live transactions only after bounded ALC
collection. `OutOfProcess + UiKind.Wpf` is eligible only after its authenticated ModuleHost process
exits. Legacy in-process WPF modules remain load-compatible but require restart and cannot enter a
live program-directory transaction.

The Shell receives only versioned IPC state; it never receives plugin `Type`, `UserControl`,
`FrameworkElement`, templates, dictionaries, or instances. ModuleHost creates an independent top-level
window. One worker hosts exactly one module. The broker binds a process handle, random session ID,
one-time nonce, manifest/API/tree identity, and protocol handshake; a PID alone is never identity.
The same complete identity is required on every state response. Process-exit observation is bound to
the exact session object, generation, and process identity so a delayed old-worker callback cannot
remove a replacement worker. Window suspend/restore is an explicit allowlisted lifecycle operation
that hides and shows the existing worker-owned WPF window without changing module activation.

ModuleHost is lifecycle and crash isolation, not a permissions sandbox. It currently runs with the
same user authority as QingToolbox.

## Migration

Old manifests resolve to `LegacyInProcess` and `RestartRequired`. WPF module publishers can opt into
the explicit OutOfProcess/Wpf manifest capability. Service-only modules migrate to
InProcessCollectible/None and must not export plugin-defined UI objects.
Partial capability declarations and unsupported isolation/UI combinations are rejected during
discovery rather than deferred until load.
