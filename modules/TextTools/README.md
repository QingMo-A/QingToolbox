# Text Tools

Text Tools is a QingToolbox module for lightweight text conversion and formatting.

## Features

- Format JSON
- Minify JSON
- Base64 encode/decode
- URL encode/decode
- Uppercase/lowercase
- Remove empty lines
- Copy output back to input
- Clear input, output, and status

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
Refresh Modules → Load → Open
```
