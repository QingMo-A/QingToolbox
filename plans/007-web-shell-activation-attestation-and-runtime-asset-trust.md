# Plan 007: Web Shell Activation Attestation and Runtime Asset Trust

## Status

```text
Status: Engineering Complete
Track: UI Modernization / UI-1.2
Depends on: Plan 006
Post-freeze correctness correction: Plan 008
```

## Scope

Close the Development Web Shell activation and runtime trust boundary without changing Production,
ModuleTest, module lifecycle, or the read-only Bridge command surface.

## Activation attestation

Protocol v3 returns a cryptographically random, generation-scoped activation nonce and an
authoritative snapshot from `web.ready`. Vue validates the challenge and every snapshot field at
runtime, stores the nonce, and sends it with `app.ping`. The Host validates the nonce against the
current Generation and Session. Only that acknowledged ping marks the Shell Ready and permits WPF
to hide the native recovery workspace. Normal Development and Canary use the same path.

## Runtime asset trust

MSBuild generates an uncommitted `WebAssetBuildInfo.g.cs` after deterministic asset verification.
The compiled Shell contains the expected schema, asset build ID, and manifest SHA-256. Before
WebView2 creation, runtime validation compares the disk manifest to those compiled values, validates
the exact file set, size and SHA-256 of each output, and creates an immutable service allowlist.
Only allowlisted output files can be served; the manifest itself and extra files are denied.

## Generation and recovery

Process failure handlers capture Core, Generation, and cancellation state. Old Core events and
same-generation duplicates are ignored. The first current-generation failure runs one observed
recovery task; a real failure in the recovered generation selects native fallback. Disposal unbinds
navigation, resource, window, download, permission, process, and Bridge message handlers.

## Verification

Runtime validators, nonce rejection, host-anchored tamper cases, source identity inputs, frontend
activation, transport disposal, full Debug/Release builds, the non-Mock Development Canary,
portable/installer bindings, all frozen-core smoke tests, and exact final-HEAD CI form the closure
gate. No module side-effect command, Production Web Shell, tag, or release is introduced.
