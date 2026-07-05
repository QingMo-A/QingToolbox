# Module Development

QingToolbox modules are developed separately from the host Shell.

The host application lives on the `toolbox` branch.

This branch is reserved for standalone module source code.

## Rules

1. Modules must not modify the Shell directly.
2. The Shell must not reference module projects.
3. Modules are discovered by `module.json`.
4. Modules are loaded manually by the user.
5. Modules should clean up resources when unloaded.
6. WPF modules may provide UI through `CreateView()`.

## Current status

The module SDK / NuGet packaging workflow is not finalized yet.

Until the SDK packaging is ready, module projects may need temporary local references
during development.

## External host contract reference

Standalone modules use the `QingToolboxHostRoot` MSBuild property to locate
`QingToolbox.Abstractions` in a separate `toolbox` worktree:

```powershell
dotnet build .\modules\TextTools\QingToolbox.Modules.TextTools.csproj `
  -p:QingToolboxHostRoot="D:\Path\To\QingToolboxToolboxWorktree"
```

TextTools includes build and deployment scripts. Deployment copies only the module DLL,
manifest, icon, and optional dependency manifest into the toolbox Shell output. The
Shell does not reference the TextTools project.
