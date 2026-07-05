# QingToolbox

QingToolbox 是一个面向 Windows 的轻量模块化工具箱。主程序只提供现代化 UI
外壳、模块清单管理、导航与按需加载基础设施；所有实际工具功能均由独立模块提供。

项目当前处于第三阶段：可以发现、读取和验证模块清单，但不会加载模块 DLL。
发现结果仅使用 `NotLoaded` 或 `Failed` 状态，Shell 也不会在启动时扫描模块。

```powershell
dotnet build
dotnet run --project QingToolbox.Shell
```
