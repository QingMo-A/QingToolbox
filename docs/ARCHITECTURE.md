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

运行时模块目录未来可位于应用目录或用户数据目录；仓库根目录的 `Modules` 仅预留给本地开发模块包。

Shell 仍然不能直接引用模块项目，模块只能依赖 Abstractions。

## 进程内加载基础设施

`InProcessModuleLoader` 可以在 collectible `AssemblyLoadContext` 中创建唯一的
`IToolModule` 实现并调用其加载生命周期。`LoadedModuleHandle` 负责调用卸载与异步
释放生命周期、清除模块引用并释放加载上下文。

`ModuleUnloadVerifier` 仅供开发与测试验证加载上下文是否被垃圾回收。当前 Shell
不会调用进程内加载器，仍然只执行 manifest discovery；UI 加载和卸载操作将在后续
阶段接入。

`QingToolbox.DevTools.ModuleLoadSmokeTest` 是独立的开发验证工具。它扫描运行时
模块目录，加载 Hello 模块，调用激活与停用生命周期，然后释放句柄并验证 collectible
加载上下文能够被回收。它不被 Shell 引用，也不是应用运行时的一部分。

## 运行时管理

Core 的 `ModuleRuntimeManager` 串行管理模块的 Load、Activate、Deactivate 和 Unload
生命周期，并持有活动的 `LoadedModuleHandle`。`ModuleRegistry` 仍只负责 manifest
discovery 结果，两者职责保持分离。

Shell 目前只注册运行时管理服务，并未调用其生命周期方法，也没有 Load/Unload UI。
后续 UI 将通过 `ModuleRuntimeManager` 接入。

## 核心原则

1. 本体不内置具体工具功能。
2. 模块默认不进入内存。
3. 模块按需加载。
4. 模块未来需要支持从内存卸载。
5. 重型模块未来可使用独立进程隔离。
6. UI 保持简约现代，但不能牺牲启动速度。
7. Shell 不直接引用任何模块项目。
8. 模块清单可以在不加载模块 DLL 的情况下读取。
