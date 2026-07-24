# Development Web Shell Foundation

QingToolbox UI-1/UI-1.1 is a Development-only local WebView2 + Vue projection. Production and
ModuleTest remain native WPF and never initialize this bridge.

## Readiness and fallback

The native workspace remains visible while the Web Shell is initializing, navigating, awaiting
activation, or recovering. Protocol v3 `web.ready` returns a generation-scoped cryptographic nonce
and authoritative snapshot. Vue validates both at runtime and acknowledges them with a nonce-bound
`app.ping`; only that accepted ping activates the Web workspace.
The ready timeout is 12 seconds; Runtime/controller creation has a separate bounded timeout.

A first process failure disposes the old Core, bridge session, and WebView generation before one
rebuild. A second failure selects native fallback for the session. Shutdown cancellation performs
cleanup without being reported as Runtime failure.

## Bridge boundary

The only commands are `web.ready`, `app.ping`, and `app.getSnapshot`. Requests are limited to 64 KiB
and require protocol 3, a UUID, an allowlisted command, an object payload, and exact allowed fields.
Ready identity requires `assetBuildId`, `documentReadyState: complete`, and `transportMode: WebView`;
ping requires the current Session nonce.
Generation, captured Core, and session cancellation are checked before any response affects the
current page. C# remains authoritative and Vue rebuilds Pinia from snapshots.

## Browser and network boundary

Packaged assets require `WebViewTransport`; a missing host API is shown as unavailable. Browser Mock
is enabled only by explicit Vite mock mode and cannot satisfy the C# ready handler. CSP denies
network connections, remote scripts/fonts, objects, forms, and frames. WPF independently denies
external HTTP/HTTPS subresources, new windows, downloads, and permissions. No host object exists.

## Asset identity

`dist/qing-web-assets.json` records a deterministic asset build ID, package-lock SHA256, source-tree
SHA256, and exact output paths, sizes, and SHA256 values. It contains no timestamps, machine names,
users, or absolute paths. MSBuild compiles its ID and manifest SHA256 into the Shell. Runtime checks
that host anchor plus the exact file set before creating WebView2, and serves only registered files.
Portable, installer, and installed-payload tests verify the same host binding.

## Verification status

Implementation and deterministic tests pass locally. A real non-Mock canary passed controller
creation, exact local navigation, Vue execution, trusted ready, authoritative snapshot, and
ping/pong. Plans 005, 006, and 007 are Engineering Complete — Frozen. UI-1 is frozen and UI-2 is next.
