# QingToolbox

QingToolbox 是一个面向 Windows 的轻量模块化工具箱。主程序只提供现代化 UI
外壳、模块清单管理、导航与按需加载基础设施；所有实际工具功能均由独立模块提供。

当前阶段建立最小可构建骨架，不会在应用启动时加载模块 DLL。

```powershell
dotnet build
dotnet run --project QingToolbox.Shell
```
