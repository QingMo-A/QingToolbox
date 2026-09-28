# QingToolbox module API v1

`apiVersion` is the compatibility version of the capabilities the Tauri host
offers to an out-of-process module. The current host supports integer version
**1**. New `module.json` and `qmod.json` files must both declare
`"apiVersion": 1`. The host reports its supported value as `apiVersion` in
`get_host_info` and marks an unsupported module invalid during discovery, before
execution. The two package identities must agree; malformed values are rejected.

For older *process-profile* modules installed before this field existed, an
absent `apiVersion` means v1. JSON `null`, strings, zero, and future versions do
not mean v1. The legacy WPF in-process profile is never made compatible by
omitting this field.

## What v1 includes

- The host owns process launch, per-start nonce authentication, load/unload,
  enable/disable, and shutdown. `module.hello.response` must advertise
  `lifecycleVersion: 1`; disabling stops background work without unloading.
- A module may expose only the operations and host-originated events declared
  in its manifest. The host forwards those through `module.invoke` and
  `module.event`, with matching request IDs and bounded payloads.
- The host resolves executable, Web UI, and icon paths from a validated
  manifest. The frontend cannot supply an arbitrary executable or filesystem
  path for the host to run.
- Module import and update preview the package API version. If it differs from
  the host, the user may cancel or explicitly store the package. A confirmed
  incompatible module remains visible as invalid, but cannot load, enable, or
  execute until a compatible host is available. Other manifest errors cannot
  be bypassed by this confirmation. Without confirmation, an incompatible
  update leaves the installed module intact.

The detailed message shapes and transport rules are in
[`module-protocol.v1.schema.json`](module-protocol.v1.schema.json) and
[`README.md`](README.md); lifecycle behavior is in
[`../docs/TAURI_MODULE_LIFECYCLE.md`](../docs/TAURI_MODULE_LIFECYCLE.md).

This number is **not** the module's release `version`, wire-envelope
`protocolVersion`, lifecycle handshake `lifecycleVersion`, Vue bridge protocol
version, or the legacy qmod package-profile string
`moduleApiVersion: "tauri-process-v1"`. Those values serve different purposes.
Future host API changes require a new versioned API document and explicit host
compatibility handling; they must not silently reinterpret v1 modules.
