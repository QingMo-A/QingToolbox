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
