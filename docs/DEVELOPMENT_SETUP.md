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
