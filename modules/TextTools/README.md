# Text Tools

Text Tools is a QingToolbox module for lightweight text conversion and
formatting.

## Features

- Format JSON
- Minify JSON
- Base64 encode/decode
- URL encode/decode
- Uppercase/lowercase
- Remove empty lines
- Copy the result to the clipboard
- Move the result back to input or swap both panes
- Clear input, output, and status

## Localization

Text Tools supports `en-US` and `zh-CN`. Both manifest metadata
(`module.name` / `module.description`) and internal UI text come from the
module's `i18n` JSON files.

The module receives the current QingToolbox language through
`ModuleContext.Localization`. Open Text Tools views implement
`ILocalizedModuleView`, so labels and buttons refresh when the toolbox language
changes. Existing input and output text are not cleared by localization refresh.

Long-running transformations execute away from the UI thread and are cancelled
when the module unloads.

## Build

```powershell
./build.ps1 -QingToolboxHostRoot "D:\Path\To\QingToolboxToolboxWorktree"
```

`QingToolboxHostRoot` must point to a `toolbox` branch worktree containing
`QingToolbox.Abstractions`. The default is `..\..\..\QingToolbox-toolbox`.

## Deploy to toolbox

```powershell
./deploy-to-toolbox.ps1 -QingToolboxHostRoot "D:\Path\To\QingToolboxToolboxWorktree"
```

Then run the Shell from the `toolbox` branch and click:

```text
Refresh Modules -> Load -> Open
```

## Package

Run `scripts/package-texttools.ps1` from the repository root. Version `0.1.1`
produces `qing.texttools-0.1.1.qmod` and its same-name `.sha256` sidecar.
