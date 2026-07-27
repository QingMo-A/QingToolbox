# Plan 012: Development Web Startup Health Parity

```text
Status: Planned
Track: UI Modernization / UI-4B
Environment: Development only
```

Depends on:

- UI-1 Engineering Complete — Frozen
- UI-4A / Plan 011 Implementation Complete
- Host-confirmed `settings.setLaunchAtLogin` implementation complete
- Native Windows startup registration, health journal, test, repair, and module startup authorization services

## Product goal

Restore the useful detail and explicit operations from the native WPF Startup health workspace in Development Web Settings without duplicating Windows startup logic or weakening environment isolation.

Vue remains a projection and command surface. The native ViewModel and existing startup services remain authoritative. Production and ModuleTest continue to use the native WPF workspace.

## Registered slices

### UI-4B1 Startup health detail projection

Project the existing native values into the complete Settings snapshot:

- Startup backend and current health
- Most recent automatic startup
- Presentation-ready duration
- Complete-ready duration
- Most recent failure phase
- Startup authorization count
- Missing startup authorization count

This slice is read-only. It must not expose local paths, task XML, registry values, exception text, usernames, SIDs, or raw private journal records.

### UI-4B2 Explicit startup diagnostic operations

Add separate, exact, activated-session commands for:

- Test startup
- Repair startup registration
- Refresh startup health
- Open Task Scheduler
- Copy safe startup diagnostics

Every command must reuse the existing `MainWindowViewModel` and startup services. No handler may access the registry, Task Scheduler COM implementation, clipboard, or process launching directly. Test and repair must be separate commands with independent busy state and safe errors. No generic startup command or arbitrary target is permitted.

Opening Task Scheduler and copying diagnostics are user-initiated local Shell actions. They must remain unavailable outside Development Web Shell registration and must not return paths, Task XML, or clipboard contents through the Bridge.

### UI-4B3 Missing startup authorization cleanup

Project the existing authorization totals and add one explicit host-confirmed cleanup command for stale authorizations whose modules are no longer installed.

The operation must reuse the native cleanup path, preserve valid authorizations, remain blocked by existing recovery/readiness gates, and return a complete refreshed Settings snapshot. It must not accept module IDs, paths, fingerprints, or arbitrary deletion targets from Vue.

## UX target

Settings → Startup retains the current Launch at login control and restores a detailed Startup health card matching the native WPF information hierarchy:

```text
Registration backend
Current health
Most recent automatic startup
Presentation-ready duration
Complete-ready duration
Most recent failure phase

Test startup
Repair startup
Refresh status
Open Task Scheduler
Copy startup diagnostics

Startup authorizations
Missing authorizations
Clean missing authorizations
```

Buttons must reflect host capability and busy state. Repair and cleanup remain disabled unless the authoritative host reports they are applicable. Failed operations preserve the last confirmed snapshot and show fixed safe messages.

## Security and architecture boundaries

- Preserve Bridge protocol version 4 unless a separately reviewed protocol change is required.
- Preserve activation nonce, session token, generation, asset validation, and native fallback unchanged.
- Do not create a second startup backend or registration service.
- Do not introduce Windows Service, elevation, Startup-folder shortcuts, automatic repair, automatic tests, or background polling.
- Do not expose registry paths, executable paths, task paths, XML, command lines, stack traces, SIDs, usernames, or journal files.
- Do not modify module runtime, module loading, module update transactions, or module fingerprint verification.
- Do not enable Production Web UI.

## Delivery order and gates

Each slice is a separate implementation task, review, commit, and push:

1. UI-4B1 must land with contract, provider, Mock, Vue, responsive, and safe-serialization tests.
2. UI-4B2 may start only after UI-4B1 is verified and must test each command independently.
3. UI-4B3 may start only after the native cleanup path and its ownership boundary are reconfirmed.

Each slice requires frontend typecheck/test/build, Debug and Release builds, WebShell Smoke Test, and the directly related startup reliability suites. No installer or release pipeline is required unless a slice changes packaging.

## Completion criteria

Plan 012 is complete when Development Web Settings provides the native Startup health information and user-initiated operations listed above, all results are host-confirmed, failure responses are safe, responsive light/dark layouts are verified, and Production/ModuleTest behavior remains unchanged.
