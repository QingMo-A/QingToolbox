# 开发环境

## 当前主线：Rust + Tauri 2 + Vue 3

工具箱本体的新开发环境位于 `QingToolbox.Tauri`。它使用 Rust/Tauri 2
承载窗口、托盘、设置和模块进程监督，Vue 3 负责页面；新模块必须使用
`protocol/` 中的进程协议，不再引用旧 .NET/WPF ABI。

在 Windows 上安装 Rust stable、Node.js、WebView2 和 Tauri 所需的 C++
构建工具后，先运行完整门禁：

```powershell
pwsh ./scripts/verify-tauri.ps1
```

交互式开发可运行根目录的 `run-tauri-dev.bat`；Release 便携候选可运行
`run-tauri-production.bat`。生产脚本只写入 `artifacts/tauri-production/`，
不会覆盖现有 WPF 安装。

旧 WPF 章节仅用于维护历史宿主和尚未切换的安装器；它不是 Tauri 开发的
默认入口。

## 旧 WPF 宿主维护环境

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
QingToolbox.Shell/bin/Debug/net10.0-windows10.0.17763.0/Modules
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

## Preview publishing

Formal Windows distribution is installer-only. Build the supported `win-x64`
installer with `./scripts/build-installer.ps1`; it retains an internal publish
directory, payload audit, Host Payload Manifest, and Web asset binding check.
No portable ZIP is produced. `artifacts/` remains ignored by Git.
