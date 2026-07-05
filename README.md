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
docs/
```

Modules should be developed as independent projects and should not require changes to
the toolbox Shell.

The Shell must not directly reference module projects.

## Available modules

`modules/TextTools` is the first standalone module. It is developed on this branch and
uses a configurable `QingToolboxHostRoot` to reference the host's Abstractions project
without copying toolbox source into this branch.
