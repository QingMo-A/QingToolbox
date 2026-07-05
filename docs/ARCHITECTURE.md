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

发现流程不会加载模块 DLL，也不会创建 `IToolModule` 实例。运行时模块目录未来可位于
应用目录或用户数据目录；仓库根目录的 `Modules` 仅预留给本地开发模块包。

Shell 仍然不能直接引用模块项目，模块只能依赖 Abstractions。

## 核心原则

1. 本体不内置具体工具功能。
2. 模块默认不进入内存。
3. 模块按需加载。
4. 模块未来需要支持从内存卸载。
5. 重型模块未来可使用独立进程隔离。
6. UI 保持简约现代，但不能牺牲启动速度。

Shell 不直接引用任何模块项目。模块清单可以在不加载模块 DLL 的情况下读取。
