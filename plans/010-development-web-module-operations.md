# Plan 010: Development Web Module Operations

## Status

```text
Status: In Progress
Track: UI Modernization / UI-3
Depends on:
- B2.1 Engineering Complete — Frozen
- UI-1 Engineering Complete — Frozen
- UI-2A / Plan 009 Implementation Complete
Environment:
- Development only
UI-3A: Implementation Complete
Next: UI-3B Open / Focus
```

## Product objective

> Turn the Development Web module center from a read-only viewer into a safe entry point for the
> existing host-confirmed module lifecycle.

Plan 010 defines four separately delivered implementation slices. It does not authorize completing
all of UI-3 in one task, and this planning commit registers no Bridge command or runtime behavior.

## Authority and lifecycle boundary

- C# is the only authoritative source of module state.
- Vue sends user intent only.
- An operation is successful only after C# confirms it.
- Pinia must not optimistically mutate `RuntimeState`.
- Every operation continues through the existing `ModuleExecutionGate`.
- Reuse the existing Runtime Manager, Process Broker, Runtime Coordinator, and frozen B2.1 boundary.
- Module content remains hosted by the WPF `ModuleWindow + UserControl` path.
- Vue provides entry points, projected status, progress, and feedback; it does not render module UI.
- Do not recreate the lifecycle state machine or add a second lifecycle service.
- RecoveryRequired and execution-blocked state remain fail-closed host facts.

The command surface must stay explicit and strongly typed. Expected command identities are:

```text
modules.load
modules.activate
modules.open
modules.deactivate
modules.unload
modules.setStartupAuthorization
```

Do not introduce a generic `modules.execute(actionName)`, arbitrary string action names, reflection,
or arbitrary routes, paths, window types, assemblies, or command lines. This plan names future
commands but does not register them.

## UI-3A: Load and Activate

### Scope

- Project host-authoritative `CanLoad`, `CanActivate`, `IsBusy`, and `IsExecutionBlocked` state.
- Send explicit Load and Activate user intents.
- Refresh the authoritative snapshot after host confirmation.
- Show bounded in-progress, success, failure, and execution-blocked feedback.
- Serialize conflicting operations at the existing host boundary.
- Keep list, details, and Running projections consistent with the confirmed snapshot.

Load and Activate may be implemented together because they share the first lifecycle entry boundary.

### Non-goals for UI-3A

```text
Open
Deactivate
Unload
Remove
Startup Authorization
```

## UI-3B: Open and Focus

### Scope

- Open only a module for which the host reports `CanOpen`.
- Reuse `MainWindowViewModel` and `ModuleWindowManager`.
- If the module window already exists, activate and focus that window instead of creating a duplicate.
- Return host-confirmed success or structured failure to Vue.
- Refresh projected state only from authoritative host results or snapshots.

Vue must not create a WPF window, select a window class, or supply arbitrary routes, owner handles,
TopMost state, ShowInTaskbar state, or other native window parameters.

## UI-3C: Deactivate and Unload

### Scope

- Send explicit Deactivate and Unload intents.
- Reuse current module-window closure, runtime coordination, unload verification, and recovery gates.
- Refresh the authoritative snapshot after C# confirms the final state.
- Surface busy, blocked, failure, and RecoveryRequired outcomes without inventing frontend state.
- Add the smallest confirmation interaction only where the existing lifecycle semantics genuinely
  require user confirmation.

Do not prebuild a general Surface System, Dialog framework, or Operation Queue. A focused reusable
confirmation component is permitted only when UI-3C has a real consumer.

## UI-3D: Module startup authorization

### Scope

- Project current startup authorization state.
- Allow the user to enable or disable “start with QingToolbox” through an explicit host command.
- Reuse the existing complete-load fingerprint and authorization logic.
- Refresh authorization and module state from the host after confirmation.
- Preserve fail-closed fingerprint verification during startup execution.

This slice must not weaken, replace, or bypass current fingerprint verification and startup recovery.

## Explicit non-goals

Plan 010 excludes:

```text
Module deletion
qmod import
Verified Package installation
Module update transactions
Automatic module update
Host self-update
Manual recovery-transaction controls
Arbitrary filesystem paths
Arbitrary command execution
Production Web Shell enablement
Removal of the native WPF fallback
Replacement of WPF module windows
Language settings
Launch at login
Startup repair and startup test
Hybrid Web Window
```

Preview 2 release work and UI modernization remain independent tracks. Plan 010 must not be used to
expand release scope or claim Production Web UI readiness.

## Implementation constraints

- Use explicit typed request and response DTOs per command.
- Validate exact payload properties and module identifiers at the Bridge boundary.
- Preserve protocol/session/generation/asset verification and native WPF fallback unchanged.
- Reuse existing ViewModels and runtime services through narrow adapters where necessary.
- Do not expose service objects, runtime objects, manifests, absolute paths, or implementation types.
- Do not optimistically change Pinia runtime or authorization state.
- Do not bypass `ModuleExecutionGate` or duplicate Runtime Manager state.
- Do not add infrastructure without a concrete consumer in the active slice.
- Production and ModuleTest must retain their current behavior.

## Delivery discipline

Each UI-3 slice is a separate Codex task, review, commit, and push. A task may implement only its
named slice and the minimum shared code required by that slice. Completing UI-3A does not authorize
starting UI-3B, UI-3C, or UI-3D in the same task.

Expected areas should remain narrowly scoped to the existing Web Shell command handlers, safe DTOs,
typed frontend client/store projection, the existing Modules or Running surfaces, and directly
related tests. A slice must not broadly refactor the frozen Bridge, activation, asset, lifecycle, or
fallback foundations.

## Verification strategy

Every implementation slice runs by default:

```text
Directly related frontend tests
Corresponding WebShell Smoke Test
Necessary Debug or Release build
Development Web Canary
git diff --check
```

When a slice changes a public lifecycle boundary, also run:

```text
Related B2.1 Smoke Tests
Debug Build
Release Build
```

Do not run by default for every small slice:

```text
Installer build
Installer roundtrip
Preview 1 to Preview 2 upgrade
Complete release gate
Every Canary
```

Run broader validation only when the actual changed path requires it.

## Acceptance criteria

- Each implemented command is explicit, typed, allowlisted, and Development-only.
- C# remains authoritative before, during, and after every operation.
- Vue shows pending and final feedback without optimistic lifecycle mutation.
- Successful operations end with a host-confirmed snapshot or response.
- Failed or blocked operations leave projected state consistent with C#.
- Existing execution, recovery, runtime, process, fingerprint, and module-window boundaries are reused.
- No arbitrary action, path, command, route, assembly, or window parameter crosses the Bridge.
- Production, ModuleTest, native fallback, and WPF module content remain unchanged.
- Each slice passes its targeted verification before being committed.

## Branch and Git safety

This plan applies only to `toolbox`. Do not modify or push `modules`.

Do not use:

```text
git reset --hard
git clean -fd
git restore .
git checkout -- .
git stash
git commit --amend
git push --force
git push --force-with-lease
--no-verify
```

Do not commit generated assets, dependencies, build output, logs, screenshots, profiles, user data,
or runtime journals.

## Definition of planning completion

Plan 010 planning is complete when the roadmap identifies UI-3 as the next Development-only stage,
the four slices and their non-goals are explicit, Plan 003 no longer treats B2.1 as a blocker, and no
runtime command or product behavior has been added.
