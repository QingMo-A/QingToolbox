# Plan 005: Development-only Web Shell Foundation

## Status

```text
Status: Engineering Complete — Frozen
Integration Verification: Complete
Track: UI Modernization / UI-1
Depends on: Plan 003 and frozen Plan 004
Environment: Development only
Next: UI-2 Qing Design System, Surface System, and read-only module center
```

## Scope

Establish one Vue 3 application, a versioned read-only Qing Bridge, and a securely hosted WPF
WebView2 workspace. Development may select this workspace asynchronously; Production and
ModuleTest retain the native WPF workspace and never initialize WebView2.

## Non-goals

No module lifecycle commands, installation, update, deletion, settings mutation, file picker,
surface creation, module-center migration, production Web Shell, Qing Design System, Qing Surface
System, or replacement of WPF WindowChrome and module windows.

## Dependencies

Plan 003 defines the hybrid target architecture. Plan 004, B1, and B2.1 remain Engineering Complete
— Frozen. Vue 3, TypeScript, Vite, Pinia, Vue Router, and official WebView2 are the only foundations.

## Architecture

C# remains authoritative. `NativeWorkspace` and `DevelopmentWebWorkspace` are mutually exclusive
areas below the native title bar. Initialization follows critical native presentation and never
participates in recovery, discovery, or single-instance gates. Failure restores `NativeWorkspace`.

## Security boundary

Only the packaged local asset origin may navigate. New windows and downloads are cancelled,
permissions are denied, no host object is exposed, and the bridge only permits `web.ready`,
`app.ping`, and `app.getSnapshot`. Responses never expose paths, command lines, assemblies, journals,
settings, or service objects.

## Frontend structure

`QingToolbox.WebUI` contains app/router/store, transport/protocol/client bridge layers, contracts,
the Development diagnostics page, and local styles. Only `WebViewTransport` accesses
`window.chrome.webview`; browser development uses an explicitly labelled `MockTransport`.

## Bridge protocol

Protocol v2 defines request, response, event, snapshot, ready identity, and structured error envelopes. Requests
require a UUID and an allowlisted command. Vue rebuilds Pinia from C# snapshots after ready/reload.

## Build integration

Scripts run `npm ci`, type checking, tests, and production build. Release .NET builds reject missing
assets; verified assets are copied into `WebUI/` for build, publish, portable, and installer outputs.

## Fallback strategy

Missing runtime/assets, bounded initialization timeout or bridge failure, or repeated browser process failure
disposes WebView2 and restores WPF. A process failure may trigger only one controlled reload.

## Automated tests

Frontend tests cover mock requests, identity, protocol, events, timeout/unavailability. Native smoke
tests cover environments, allowlist, envelopes, safe errors, navigation denial, and bounded recovery.

## Manual observations

Development should show the local diagnostics page below the title bar. Production and ModuleTest
should remain unchanged. A local Development launch remained alive and the 12-second initialization
timeout restored native WPF with a structured diagnostic. Successful WebView rendering was not
manually observed on that machine and is not claimed.

## Commit rules

Do not commit generated assets, dependencies, binaries, profiles, logs, settings, or release assets.
Do not amend, force push, create a tag/release, or modify the modules branch.

## Definition of completion

Plan 006's real non-Mock canary passed; this foundation is frozen.
