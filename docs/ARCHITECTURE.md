# 架构

## 分层

- **Shell**：WPF 应用入口、窗口、导航和模块页面容器。
- **Abstractions**：Shell 与模块共享的最小接口、模型和契约。
- **Core**：模块注册、状态、配置、导航和服务抽象。
- **ModuleLoader**：读取 `module.json`；后续使用可回收的 `AssemblyLoadContext` 扫描、加载和卸载模块。
- **Modules**：独立工具模块，只依赖 Abstractions。

## 模块契约

`QingToolbox.Abstractions/Modules` 定义模块契约：

- `IToolModule` 是模块生命周期接口，包含加载、激活、停用、卸载和异步释放。
- `ModuleContext` 提供模块目录、数据目录和轻量属性。
- `ModuleManifest` 对应模块的 `module.json` 清单。
- 枚举类型描述模块运行方式、加载策略、状态和声明权限。

## 模块清单发现

第三阶段提供轻量的模块清单发现流程：

- `ModuleManifestReader` 读取 `module.json`，并支持字符串形式的枚举值。
- `ModuleManifestValidator` 验证必要字段和清单声明的入口文件。
- `ModuleManifestScanner` 只扫描模块根目录的一级子目录并返回 `DiscoveredModule`。
- 有效清单的状态为 `NotLoaded`，无效或无法读取的清单状态为 `Failed`。

发现流程不会加载模块 DLL，也不会创建 `IToolModule` 实例。

Shell 按顺序扫描程序目录下的 `Modules`（开发或随程序提供，只读用途）和
`%LOCALAPPDATA%\QingToolbox\Modules`（用户导入模块）。同 ID 冲突时开发目录优先。
模块运行数据位于 `%APPDATA%\QingToolbox\Data`，用户设置位于
`%APPDATA%\QingToolbox\settings.json`。扫描和刷新均不会加载模块 DLL。

Shell 仍然不能直接引用模块项目，模块只能依赖 Abstractions。

## 进程内加载基础设施

`InProcessModuleLoader` 可以在 collectible `AssemblyLoadContext` 中创建唯一的
`IToolModule` 实现并调用其加载生命周期。`LoadedModuleHandle` 负责调用卸载与异步
释放生命周期、清除模块引用并释放加载上下文。

`ModuleUnloadVerifier` 仅供开发与测试验证加载上下文是否被垃圾回收。当前 Shell
不会调用进程内加载器，仍然只执行 manifest discovery；UI 加载和卸载操作将在后续
阶段接入。

`QingToolbox.DevTools.ModuleLoadSmokeTest` 是独立的开发验证工具。它通过
`ModuleRuntimeManager` 验证 Hello 模块从 NotLoaded、Loaded、Running、Deactivated
到 Unloaded 的完整状态链，并确认 collectible 加载上下文能够被回收。它不被 Shell
引用，也不是应用运行时的一部分。

Smoke test 还覆盖 `CreateView()`：在 STA 线程创建模块 WPF View，显式释放 View
引用后卸载模块，并再次验证加载上下文回收。这用于防止 UI 引用意外阻止模块卸载。

## 运行时管理

Core 的 `ModuleRuntimeManager` 串行管理模块的 Load、Activate、Deactivate 和 Unload
生命周期，并持有活动的 `LoadedModuleHandle`。`ModuleRegistry` 仍只负责 manifest
discovery 结果，两者职责保持分离。

Shell 的模块卡片通过 `ModuleRuntimeManager` 提供 Load、Activate、Deactivate 和
Unload 操作。只有用户点击按钮才会触发生命周期；Shell 启动与 Refresh 仍只发现清单，
不会自动加载模块。

应用退出时，Shell 通过 `ModuleRuntimeManager.DisposeAsync` 清理所有活动模块。
Running 模块会先停用，再卸载并释放；单个模块失败不会阻止其余模块的卸载尝试，也不会
阻止应用关闭。

## 模块 View 承载

已加载模块可通过契约的 `CreateView()` 返回 UI 对象。`ModuleRuntimeManager` 负责受控
调用该方法且不缓存 View；Shell 只在用户点击 Open 后，把当前一个 View 放入
`ContentControl`。Open 不会隐式 Load，关闭、切换或卸载模块时会先清除 Shell 的 View
引用。Shell 不引用具体模块类型。

模块清单的可选 `icon` 字段表示相对于模块目录的 SVG 文件。清单展示层只解析并验证
图标路径，不加载模块 DLL；缺少有效图标时，Shell 使用内置 `modules.svg`。

## 核心原则

1. 本体不内置具体工具功能。
2. 模块默认不进入内存。
3. 模块按需加载。
4. 模块未来需要支持从内存卸载。
5. 重型模块未来可使用独立进程隔离。
6. UI 保持简约现代，但不能牺牲启动速度。
7. Shell 不直接引用任何模块项目。
8. 模块清单可以在不加载模块 DLL 的情况下读取。
