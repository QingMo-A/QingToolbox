# QingToolbox Modules

Available modules include TextTools, ScreenPin, WindowTopmost, and PowerGuard. PowerGuard provides opt-in offline monitoring and a cancellable normal-shutdown countdown; see [docs/POWERGUARD.md](docs/POWERGUARD.md).

PowerGuard enforces state-based operation capabilities, invalidates stale final probes after cancellation, recovery, or extension, and restarts monitoring faults through a fresh grace period. Its localized event refresh is coalesced. Module CI uses fake network and power adapters: it performs no live connectivity probe and no real shutdown.

Countdown presentation is validated against its session before and after window creation, settings mutations either commit consistently or retain the prior runtime state, and module unload never disposes synchronization resources while owned tasks remain alive. Failed cleanup can be retried safely.

This branch contains standalone module source code for QingToolbox.

The toolbox host application is maintained on the `toolbox` branch.

## Branch layout

- `toolbox`: host application, module contracts, loader, shell UI, development tools.
- `modules`: standalone module projects, module templates, and module development docs.

## Planned module layout

```text
modules/
  TextTools/
  FileTools/
  ImageTools/
  JsonTools/
templates/
  ModuleTemplate/
docs/
```

Modules should be developed as independent projects and should not require changes to
the toolbox Shell.

The Shell must not directly reference module projects.

## Module template and localization

Start new WPF in-process modules from `templates/ModuleTemplate`. The template
includes a manifest, icon, and matching `i18n/en-US.json` and `i18n/zh-CN.json`
resources. After copying it, change the placeholder `id`, `entry`, `name`, and
`description` before building.

Every module must declare `defaultLanguage: "en-US"` and both localization
resources in `module.json`. Use `module.name` and `module.description` for the
module card; use `view.*`, `actions.*`, `status.*`, and `errors.*` for module UI.

Before committing module changes, run:

```powershell
./scripts/verify-modules.ps1 -QingToolboxHostRoot "D:\Path\To\QingToolboxToolboxWorktree"
```

## Available modules

`modules/TextTools` is the first standalone module. It is developed on this branch and
uses a configurable `QingToolboxHostRoot` to reference the host's Abstractions project
without copying toolbox source into this branch.

PowerGuard release-candidate duration decisions use monotonic `TimeProvider` timestamps; UTC remains limited to UI and event records. Its automated tests use deterministic virtual time, and successful probe events are rate-limited without hiding transitions. Before deployment, complete [the manual PowerGuard acceptance checklist](docs/POWERGUARD-ACCEPTANCE.md). UPS is not supported, and protection depends on QingToolbox remaining active.

Every official module directory contains an `update.json`, while `modules/index.json` is only a lightweight moduleId-to-manifest mapping. An empty `releases` array means the module has not yet been published through the official update source; it does not mean the module is absent. Changing a source `module.json` version does not expose that version to clients. See [the module update protocol](docs/MODULE_UPDATE_PROTOCOL.md).

Validate the protocol and current metadata with:

```powershell
dotnet run --project tools/QingToolbox.ModuleUpdateMetadataValidator -c Release -- --self-test
dotnet run --project tools/QingToolbox.ModuleUpdateMetadataValidator -c Release -- --modules-root modules
```
