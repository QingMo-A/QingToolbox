# QingToolbox Modules

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
