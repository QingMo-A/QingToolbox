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

## Required localization resources

Every new module must include `i18n/en-US.json` and `i18n/zh-CN.json`. Their
keys must match exactly and `module.json` must declare:

```json
"defaultLanguage": "en-US",
"localization": {
  "basePath": "i18n",
  "resources": {
    "en-US": "i18n/en-US.json",
    "zh-CN": "i18n/zh-CN.json"
  }
}
```

Use `module.name` and `module.description` for module-card metadata. Keep
module-internal UI strings under `view.*`, `actions.*`, `status.*`, and
`errors.*`. Obtain them with `ModuleContext.Localization.GetModuleString` and
prefer `ILocalizedModuleView` for open views that need language refresh.

Do not read the Shell `settings.json`, put module UI strings into Shell resources,
or reference Shell/Core from a module. `RefreshLocalization()` must only update
visible text; it must not clear input, reload data, or rerun module work.

Start from `templates/ModuleTemplate` to get the required manifest, resource
files, `ModuleContext` lifecycle, and localized view pattern.

## Verification before commit

Run the combined validation script before committing:

```powershell
./scripts/verify-modules.ps1 -QingToolboxHostRoot "D:\Path\To\QingToolboxToolboxWorktree"
```

It validates JSON, resource-key parity, corrupted placeholder text, manifest resource
paths, and then builds every module project. To run only the localization checks:

```powershell
./scripts/check-module-i18n.ps1
```
