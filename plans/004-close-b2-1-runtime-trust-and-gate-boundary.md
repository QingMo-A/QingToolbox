# Plan 004 — Close the B2.1 Runtime Trust and Gate Boundary

## Scope

- Bind promoted, rollback, and deferred runtime restore to the B1-persisted manifest version, module
  API version, exact program-file snapshot, aggregate tree identity, and restore role.
- Hold the verified installed-tree lease across load, activation, view creation, and immediate identity
  verification.
- Serialize discovery and update replacement, and publish bounded fail-closed shutdown before runtime
  disposal.
- Exercise real pinned TextTools windows through the gated coordinator without touching Production or
  `origin/modules`.
- Declare a hybrid capability model: service-only modules retain collectible ALC; real WPF modules
  use one trusted out-of-process ModuleHost per module.

## Non-goals

Production update UI, automatic qmod installation, host self-update, WebView2, Vue, Node, Qing Design
System, and Qing Surface System are excluded. Preview 2 manual acceptance remains `Not Run`.

## Verification

Debug/Release builds, existing update/package/startup/module-load smoke tests, runtime identity and ALC
checks, Development/ModuleTest TextTools success/rollback/RecoveryRequired canaries, discovery/update
and shutdown/update races, protocol/nonce/process identity, command allowlisting, process-exit kill
fallback, worker tree-lease startup, packaging, and local environment contracts.

## Architecture evidence and decision

The real TextTools view closed, cleared Content/DataContext, drained Dispatcher work, and completed
Deactivate/Unload, yet WPF/BAML retained plugin types and the collectible ALC remained alive. The
transaction correctly returned `ModuleStillLoaded` and rolled back. This is preserved as fail-closed
evidence, not bypassed with private WPF cache reflection or weaker unload checks. ADR-001 selects a
dedicated process boundary for WPF views while preserving strict collectible ALC verification for
UI-free services and restart-only compatibility for legacy in-process WPF modules.

## Completion condition

B1 remains **Engineering Complete — Frozen**. B2.1 becomes **Engineering Complete — Frozen** only
after the complete local matrix and exact final-HEAD workflow pass. Next is
**UI-1 Development-only Web Shell Foundation**.
