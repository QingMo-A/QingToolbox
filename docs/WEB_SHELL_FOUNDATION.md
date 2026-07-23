# Development Web Shell Foundation

QingToolbox UI-1 adds a local WebView2 + Vue workspace for explicit `Development` sessions only.
It does not migrate product pages or module operations and does not enable a Production Web UI.

## Environment enablement

- Development asynchronously attempts Web Shell initialization after native presentation.
- Production and ModuleTest remain native WPF and create no WebView2/bridge/profile.
- DevTools require Development plus the explicit `--web-devtools` option.

The decision uses `ApplicationExecutionEnvironment`, not environment variables or build symbols.

## WPF / WebView2 boundary

WPF retains MainWindow, WindowChrome, title/caption/system menus, Snap, notification area, floating
badge, recovery, shutdown, and module windows. WebView occupies only `DevelopmentWebWorkspace`; the
complete existing `NativeWorkspace` remains the fallback.

## Bridge Protocol v1

Requests contain protocol version, UUID request ID, command, and payload. Responses match version
and ID and return structured payload/error. Events are versioned. The only commands are `web.ready`,
`app.ping`, and `app.getSnapshot`; no side-effect commands or host objects exist.

## Snapshot fields

The authoritative C# snapshot contains `environmentKind`, `environmentDisplayName`, `hostVersion`,
`protocolVersion`, `totalModuleCount`, `validModuleCount`, `runningModuleCount`, and `generatedAt`.
Pinia is rebuilt from snapshots after ready/reload and never infers runtime state.

## Navigation security

Assets map read-only to `https://app.qingtoolbox.local/`. HTTP, file URLs, external HTTPS, new
windows, downloads, and permissions are denied. There are no runtime CDN resources and
`AddHostObjectToScript` is not used.

## User Data Folder isolation

Only Development creates data under its isolated local profile root and profile name. Profiles do
not share data. Production, ModuleTest, repository, module, module-data, and production-cache paths
are not used.

## Resource packaging

The restore/test/build scripts run `npm ci`, typecheck, tests, and Vite build. `dist` and
`node_modules` are ignored. Release builds reject missing verified assets, and the same `WebUI/`
output is consumed by portable and installer pipelines. Runtime does not require Node.

## Runtime and process fallback

Missing Runtime/assets, a 12-second initialization timeout, initialization/bridge/navigation failure, or repeated process failure logs
a redacted diagnostic, disposes WebView2, and restores native WPF without terminating QingToolbox.
One process failure may receive one controlled reload attempt.

Local manual observation confirmed that a Runtime initialization timeout leaves the Development
host running and restores native WPF. It did not confirm successful rendering on that machine.

## MockTransport and tests

Browser development explicitly reports `Mock` and never claims C# connectivity. Frontend tests
cover request/event behavior. `QingToolbox.DevTools.WebShellSmokeTest` verifies host policies without
screen-driven automation.

## Non-goals

No Production Web Shell, page/module-center migration, module side effects, settings, picker,
hybrid child window, Web module API, Qing Design System, or Qing Surface System is implemented.
