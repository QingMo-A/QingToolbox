# Plan 009: UI-2A Read-only Module Center and Visual Foundation

**Status:** Implementation Complete

**Track:** UI Modernization / UI-2A

**Depends on:** frozen UI-1

**Next:** UI-2B shared overlays and interaction surfaces

## Objective

Turn the Development-only Web Shell into a visible QingToolbox workspace with a small token-based
design foundation, Home, read-only Modules, a module details drawer, and retained diagnostics.

## Boundaries

- Production and ModuleTest remain on the native WPF workspace.
- WebView failure still falls back to the native WPF workspace.
- Protocol v4 activation, session, generation recovery, and immutable asset serving remain frozen.
- Module state is projected from the authoritative C# view model. The Web UI does not scan module
  directories, load assemblies, execute entries, or mutate runtime state.
- No paths, entry assembly identities, manifests, stack traces, or module operation commands cross
  the bridge.

## Deliverables

- `modules.getSnapshot`, an activated-session-only, empty-payload command with a safe typed DTO.
- Vue runtime validation, `ModuleClient`, and a read-only module store with search and state filters.
- Qing tokens and only the components used by Home, Modules, Diagnostics, Drawer, and Toast surfaces.
- Local system/light/dark preview with reduced-motion support and no host settings write.
- Focused C# and frontend tests, real Development WebView canary, and Debug/Release builds.

## Non-goals

Module lifecycle commands, installation, removal, updates, settings writes, Production Web Shell,
native title-bar changes, and the broader UI-2B surface registry are explicitly excluded.
