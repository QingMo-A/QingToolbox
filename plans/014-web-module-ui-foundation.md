# Plan 014 — Web module UI foundation

## Scope

- **014A Web hosting:** validate a module-relative `.html` entry, host it in an isolated WebView2 profile, and keep the existing WPF lifecycle path intact.
- **014B Bridge and presentation:** add only the host-owned local bridge plus appearance/language projection after 014A is accepted; no arbitrary host objects or routes.
- **014C QingTransfer migration:** migrate its view to Vue only after the canary is proven; old WPF modules remain supported.

Out-of-process Web modules use the existing ModuleHost lifecycle and window commands. Legacy manifests without `uiKind`/`webEntry` continue to use WPF compatibility. Web entries are relative `.html` files under the module root; absolute paths and traversal are rejected.

## Stop rule

Do not migrate a real module, add a general bridge/RPC system, or alter the frozen update/runtime protocol until 014A validation, navigation blocking, readiness, and suspend/restore/shutdown smoke tests pass.

## Cross-module adaptation notes

The following requirements apply to every Web module migration, not only the
QingTransfer canary:

- The host owns window chrome, the validated module SVG icon, the native loading
  surface, and the transition from loading to painted Web content. A navigation
  completion event must never be treated as rendered-page readiness.
- A module owns its backend entry, relative Web entry, compiled Web assets,
  localization, and handling of the host presentation context. It must not gain
  arbitrary file-system or host-object access to reproduce native UI behavior.
- A local or packaged installation is atomic at the payload level and lives at
  `<UserModulesRoot>/<moduleId>`. Deploying under a display name, copying only an
  assembly, or combining a stale manifest with new Web assets is invalid.
- Migration acceptance includes icon projection, the host loading transition,
  close/reopen behavior, appearance/language changes, reduced motion, narrow
  layouts, and package-content auditing in addition to backend lifecycle tests.
