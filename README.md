# QingToolbox

Shell icons use a selected subset of Nieobie Game Icon Pack under CC0 1.0.

QingToolbox 是一个面向 Windows 的轻量模块化工具箱。主程序只提供现代化 UI 外壳、模块清单管理、导航与按需加载基础设施；所有实际工具功能均由独立模块提供。

项目当前处于第三阶段：可以发现、读取和验证模块清单，但不会加载模块 DLL。
发现结果仅使用 `NotLoaded` 或 `Failed` 状态，Shell 会在窗口显示后扫描运行时模块目录。

开发时可运行 `./scripts/deploy-dev-modules.ps1`，将 Hello 模块清单和入口 DLL
复制到 Shell 的运行时模块目录。该流程只验证 `module.json` 和入口文件是否存在，
不会加载模块 DLL。

ModuleLoader 现提供基于 collectible `AssemblyLoadContext` 的进程内加载与卸载底层
能力。Shell 只会在用户点击模块卡片的生命周期按钮后调用该能力。

开发验证工具 `QingToolbox.DevTools.ModuleLoadSmokeTest` 可以在部署 Hello 模块后，
通过 `ModuleRuntimeManager` 验证清单扫描、Load、Activate、Deactivate、Unload
完整状态链和加载上下文卸载。它还会创建模块 WPF View、释放 View 引用并再次验证
collectible 加载上下文能够卸载。该工具不接入 Shell。

Core 中的 `ModuleRuntimeManager` 负责运行时加载句柄与 Load、Activate、Deactivate、
Unload 生命周期；`ModuleRegistry` 继续只保存发现结果。Shell 启动和 Refresh 不会加载模块。

Shell 退出时会通过 RuntimeManager 清理所有已加载模块；处于 Running 的模块会先
Deactivate，再执行 Unload 与 Dispose。退出清理不会改变启动或 Refresh 的发现行为。

已加载模块可以通过 `CreateView()` 提供 UI，Shell 使用 `ContentControl` 承载当前
打开的单个 View。Open 不会自动加载模块；关闭 View 或卸载对应模块会清除 UI 引用。

```powershell
dotnet build
dotnet run --project QingToolbox.Shell
```
