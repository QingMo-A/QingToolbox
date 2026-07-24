# Development Web Shell Foundation

QingToolbox UI-1 through UI-1.3 is a Development-only local WebView2 + Vue projection. Production and
ModuleTest remain native WPF and never initialize this bridge.

## Readiness and fallback

The native workspace remains visible while the Web Shell is initializing, navigating, awaiting
activation, or recovering. Protocol v4 `web.ready` returns a one-use generation-scoped activation
nonce and authoritative snapshot. Vue validates both, exchanges the nonce for a 256-bit session
token, and proves two subsequent session pings before the Host activates the Web workspace.
The ready timeout is 12 seconds; Runtime/controller creation has a separate bounded timeout.

A first process failure cancels and drains the old handshake, Core, bridge session, and WebView
generation before one serialized rebuild. A second generation failure selects native fallback. Shutdown cancellation performs
cleanup without being reported as Runtime failure.

## Bridge boundary

The only commands are `web.ready`, `app.ping`, and `app.getSnapshot`. Requests are limited to 64 KiB
and require protocol 4, a UUID, an allowlisted command, an object payload, and exact allowed fields.
Ready identity requires `assetBuildId`, `documentReadyState: complete`, and `transportMode: WebView`;
the first ping requires the activation nonce, while later pings and snapshots require the current
generation session token. Generation, captured Core, and session cancellation are checked before handlers run and before any response affects the
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
that host anchor plus the exact file set before creating WebView2. It rejects reparse points, caps
assets at 256 files / 8 MiB each / 32 MiB total, snapshots verified bytes in memory, and serves only
that immutable snapshot. No request reopens disk files or falls through to a virtual-host mapping.
Portable, installer, and installed-payload tests verify the same host binding.

## Verification status

Implementation and deterministic tests pass locally. A real non-Mock canary passed controller
creation, exact local navigation, Vue execution, trusted ready, authoritative snapshot, and
activation and two repeated session pings. Plans 005, 006, 007, and 008 are Engineering Complete —
Frozen. UI-1 is frozen and UI-2 is next.
