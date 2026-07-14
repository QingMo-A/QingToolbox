# 开发环境

- Visual Studio 2026
- .NET 10 SDK
- WPF
- C#
- Windows

## 构建和运行

```powershell
dotnet build
dotnet run --project QingToolbox.Shell
```

## Git commit messages

Commit messages must describe the actual scope of the change. Use one tagged
line for each logical change instead of compressing unrelated work into one
generic summary line. When a commit contains multiple kinds of work, include
multiple tagged lines.

Allowed tags:

```text
[+]         Added functionality, files, or modules
[-]         Removed functionality, files, or dependencies
[fix]       Bug, build, or reference fixes
[refactor]  Internal restructuring without behavior changes
[docs]      Documentation changes
[style]     UI, styling, animation, or formatting changes
[chore]     Engineering configuration, dependencies, scripts, or maintenance
[test]      Test changes
```

Example:

```text
[style] redesign shell dashboard
[+] add qmod package import
[docs] document module packaging workflow
```

The first line should summarize the primary change. Add further tagged lines
for other meaningful changes included in the same commit.

## Deploy development modules

The Shell scans runtime modules from:

```text
QingToolbox.Shell/bin/Debug/net10.0-windows/Modules
```

To build the solution and deploy the Hello development module:

```powershell
./scripts/deploy-dev-modules.ps1
```

If local execution policy blocks scripts, use a process-scoped bypass:

```powershell
powershell -ExecutionPolicy Bypass -File ./scripts/deploy-dev-modules.ps1
```

Then run:

```powershell
dotnet run --project QingToolbox.Shell
```

The Hello module should appear as `NotLoaded`. This confirms manifest discovery and
entry-file validation without loading the module DLL.

## In-process loader status

`QingToolbox.ModuleLoader` contains the collectible in-process loading and unloading
infrastructure. It is not connected to the Shell UI or startup flow yet. The Shell
continues to perform manifest discovery only.

## Run the module load smoke test

First deploy the Hello development module:

```powershell
./scripts/deploy-dev-modules.ps1
```

Then run:

```powershell
dotnet run --project QingToolbox.DevTools.ModuleLoadSmokeTest
```

Custom runtime and data directories can be supplied without an additional parser:

```powershell
dotnet run --project QingToolbox.DevTools.ModuleLoadSmokeTest -- `
  --modules "path/to/Modules" `
  --data "path/to/UserData/modules"
```

The smoke test uses `ModuleRuntimeManager` to validate manifest scanning and the
`NotLoaded -> Loaded -> Running -> Deactivated -> Unloaded` state chain, then confirms
collectible load-context unloading.

It also runs a `CreateView` scenario on an STA thread: the module view is created,
released, and followed by module unload and a second collectible-context verification.

`ModuleRuntimeManager` owns active module handles and coordinates lifecycle state,
while `ModuleRegistry` remains the manifest-discovery store. Shell module cards invoke
the manager only after an explicit user action; startup and Refresh Modules remain
discovery-only operations.

When the Shell exits, it asks `ModuleRuntimeManager` to deactivate and unload all active
modules before disposing application services. Shutdown cleanup errors are written to
the debug output and do not prevent the process from closing.

After deploying Hello, launch the Shell and click Load followed by Open to display the
module-provided WPF view. Open never loads a module implicitly. Close clears the hosted
view, and Unload clears the view before releasing the module.

The deployment script also copies the optional module `icon.svg` and dependency manifest.
The Shell resolves module icon paths from `module.json`; missing icons use the packaged
default icon and do not cause module assemblies to load.
