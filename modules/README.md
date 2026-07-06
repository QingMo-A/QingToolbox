# Modules

Available modules:

- `TextTools` — text conversion and formatting.
- `ScreenPin` — region capture with resizable floating image pins.
- `WindowTopmost` — visible-window enumeration and always-on-top control.

Each folder under this directory is intended to contain one standalone QingToolbox module.

Example:

```text
modules/
  TextTools/
  FileTools/
  ImageTools/
```

A module should include:

- its own `.csproj`
- `module.json`
- module implementation
- optional WPF view
- README

## Available modules

- [`TextTools`](TextTools/README.md): JSON, Base64, URL, case conversion, and line cleanup.
