# QingToolbox

QingToolbox 是一个面向 Windows 的轻量模块化工具箱。主程序只提供现代化 UI 外壳、模块清单管理、导航与按需加载基础设施；所有实际工具功能均由独立模块提供。

项目当前处于第三阶段：可以发现、读取和验证模块清单，但不会加载模块 DLL。
发现结果仅使用 `NotLoaded` 或 `Failed` 状态，Shell 会在窗口显示后扫描运行时模块目录。

开发时可运行 `./scripts/deploy-dev-modules.ps1`，将 Hello 模块清单和入口 DLL
复制到 Shell 的运行时模块目录。该流程只验证 `module.json` 和入口文件是否存在，
不会加载模块 DLL。

```powershell
dotnet build
dotnet run --project QingToolbox.Shell
```
