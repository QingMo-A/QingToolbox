# QingToolbox module protocol

This directory contains the host/module wire contract. It is intentionally
independent from Vue and Rust so that a module can be implemented in either
language without linking to the host.

## Transport

The desktop host uses a private, per-module local transport. The first Tauri
host milestone uses stdin/stdout pipes that are created and owned by the host;
the host never accepts a module-supplied command or path. A later transport
revision may use a named pipe when a module needs reconnectable sessions, but
that is not required by v1. Each frame is one UTF-8 JSON object followed by
`LF` (implementations should also accept `CRLF`). A frame is limited to 1 MiB
and a line must be fully received before it is parsed. The host closes the
connection after a protocol violation.

The transport is bound to an operating-system-RNG nonce generated for each
module start and passed to the module over its private process boundary. A
module must echo the nonce in its `hello` payload. The nonce is never exposed
to the Vue frontend.

## Envelope

The normative JSON Schema is
[`module-protocol.v1.schema.json`](module-protocol.v1.schema.json).
The Tauri module manifest profile is described by
[`module-manifest.tauri.v1.schema.json`](module-manifest.tauri.v1.schema.json).

The envelope uses `protocolVersion`, `messageType`, `requestId` and `payload`.
The `messageType` namespace identifies the direction and operation (for
example `module.hello.request`, `module.hello.response` or
`module.state.event`). This keeps the wire shape stable while allowing new
operations to be added without changing the parser.

Request:

```json
{"protocolVersion":1,"messageType":"module.hello.request","requestId":"1","payload":{"nonce":"..."}}
```

Successful response:

```json
{"protocolVersion":1,"messageType":"module.hello.response","requestId":"1","payload":{"moduleId":"qing.launcher","version":"0.1.0"}}
```

Error response:

```json
{"protocolVersion":1,"messageType":"module.invoke.response","requestId":"2","payload":null,"error":{"code":"unknown_operation","message":"Operation is not supported."}}
```

Event:

```json
{"protocolVersion":1,"messageType":"module.state.event","requestId":"event-3","payload":{"state":"running"}}
```

Invoke request/response:

```json
{"protocolVersion":1,"messageType":"module.invoke.request","requestId":"invoke-4","payload":{"method":"getState","payload":{}}}
{"protocolVersion":1,"messageType":"module.invoke.response","requestId":"invoke-4","payload":{"ok":true}}
```

## Lifecycle

The host owns the lifecycle. It sends `module.hello.request` first and only
marks a process ready after receiving a matching `module.hello.response` that
echoes the host nonce and module id. `activate`, `deactivate` and `shutdown`
are sent only after that handshake. `shutdown` is idempotent. A module must
stop emitting events after acknowledging shutdown; the host waits a bounded
grace period and then terminates an unresponsive child.

`invoke` is a module-specific operation. Its payload is an object and its
result is returned only to the host. Any path, process or window operation
must be represented by a host-defined command and an opaque ID; modules and
frontends must not turn user-provided strings into arbitrary OS calls.

The manifest may declare an `operations` array. The host forwards only an
operation present in that list; an omitted or empty list exposes no invoke
surface. Each request uses a fresh host-owned `requestId`, and the host waits
for the matching `module.invoke.response` before returning to the Web UI.

## Module Web window bridge

Web UI loaded from `qmod://` runs in a separate Tauri window labelled
`module-<moduleId>`. The page may call the host commands
`get_module_window_context`, `invoke_module_window` and
`hide_module_window` through the official Tauri API. The latter two commands
derive the module id from the window label; a page never supplies an arbitrary
module id or filesystem path. `invoke_module_window` still applies the
manifest `operations` allowlist before the Rust runtime forwards the JSON
request to the child process.

The context may include the validated manifest icon as a bounded `iconDataUrl`
for in-module branding. It never includes the module directory, executable
path or data directory.

Minimal TypeScript bridge:

```ts
import { invoke } from '@tauri-apps/api/core'

export const context = () => invoke('get_module_window_context')
export const call = (method: string, payload: Record<string, unknown> = {}) =>
  invoke('invoke_module_window', { method, payload })
```

The module window capability is scoped to the `module-*` label pattern. It
contains only Tauri core IPC; filesystem and process permissions remain
module-owned and are not granted to Vue.
