# Plan 006: Web Shell Readiness and Asset Integrity Gate

## Status

```text
Status: Engineering Complete — Frozen
Track: UI Modernization / UI-1.1
Depends on: Plan 005
Post-review activation and runtime trust follow-up: Plan 007 — Engineering Complete — Frozen
Next: UI-2
```

## Scope

Harden the Development-only Web Shell from WPF controller creation through local navigation, Vue
execution, a trusted protocol-v2 `web.ready`, authoritative snapshot response, and ping/pong. Keep
the native workspace visible until the complete handshake succeeds.

## Implemented engineering boundary

- Explicit `Native`, `Initializing`, `Navigating`, `AwaitingReady`, `Ready`, `Recovering`, and
  fallback/disposal states replace the early ready Boolean.
- Ready requires the current Core/Generation/Session, an exact allowlisted command, a matching
  deterministic asset build ID, a complete document, and WebView transport.
- Bridge input is capped at 64 KiB with UUID, protocol, command, object payload, and exact-property
  validation. Detach cancels the session and stale asynchronous responses are discarded.
- Packaged browser mode never silently uses Mock. Mock is enabled only by the explicit Vite mock
  mode. Transport disposal removes listeners, timers, pending requests, and event subscribers.
- A deterministic `qing-web-assets.json` binds source, package lock, output paths, sizes, and SHA256.
  Release, portable, installer, and installed-payload checks require and verify it.
- CSP denies network use and the WPF host denies external HTTP/HTTPS resources, windows, downloads,
  permissions, and host objects.

## Verification evidence

Frontend type checking, tests, deterministic production build, asset verification, Debug/Release
builds, and the host smoke test pass locally. The real Development WebView2 canary passed exact
local navigation, trusted ready, authoritative snapshot, and ping/pong with Mock disabled.

## Completion gate

Plan 006 is Engineering Complete after the non-Mock Development canary passed. Plan 005 is frozen;
the exact pushed-HEAD workflow remains the final remote gate before UI-2 begins.
