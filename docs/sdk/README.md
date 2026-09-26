# QingToolbox Module API

## 状态

**Experimental — 不保证 0.x 二进制兼容。**

| 项目 | 状态 |
| --- | --- |
| Module API 契约 | 未冻结 |
| 独立 NuGet SDK | 未发布 |
| `dotnet new` 模块模板 | 未发布 |
| `qtool` 验证与打包 CLI | 未发布 |
| 稳定兼容性承诺 | 无 |

外部开发者暂时不应依赖 0.x 的二进制兼容性。

## 当前可用的两条模块路径

### 1. Tauri 进程模块（当前主线，推荐）

宿主为 `QingToolbox.Tauri`（Rust + Tauri 2 + Vue 3）。模块是独立操作系统进程，通过版本化 JSON 行协议通信，
打包为 `.qmod`。这条路径当前**已经可以实际开发**，只是契约尚未冻结：

| 主题 | 文档 |
| --- | --- |
| 通信契约与 JSON Schema | [`../../protocol/README.md`](../../protocol/README.md) |
| Load / Enable / Disable / Unload / Delete 语义 | [`../TAURI_MODULE_LIFECYCLE.md`](../TAURI_MODULE_LIFECYCLE.md) |
| 模块职责、清单字段、打包与部署 | [`../MODULE_DEVELOPMENT.md`](../MODULE_DEVELOPMENT.md) |
| 最小可复现样例 | [`../../QingToolbox.Tauri/native-module-canary/`](../../QingToolbox.Tauri/native-module-canary/) |

工具链现状：模块由仓库脚本打包（`scripts/package-tauri-module.ps1`），尚无独立 SDK 或项目模板。
Rust 模块模板是已知缺口，见 [`../README.md`](../README.md) 的「已知文档缺口」。

### 2. WPF 进程内模块（历史路径）

依赖 `QingToolbox.Abstractions` 的 .NET 模块属于历史 WPF 宿主，**当前 Tauri 宿主不会加载它们**。
源码、模板与模块文档位于独立的 [`modules` 分支](https://github.com/QingMo-A/QingToolbox/tree/modules)。
该分支的开发流程仍可能需要设置 `QingToolboxHostRoot` 环境变量。

## 权限模型的重要说明

`permissions` 字段（`FileRead`、`FileWrite`、`ProcessStart`、`Network`、`Clipboard`、`WindowControl`）
仅用于**声明与用户告知**，不构成强制沙箱。

- Tauri 模块在独立进程中以当前用户权限运行，其行为不受宿主强制约束。
- 宿主保证的是**接口收窄**：模块只能调用清单 `operations` / `events` 白名单中的操作，
  Web 界面拿不到任意路径、句柄或进程能力。
- 因此 `.qmod` 只应导入可信来源的模块。

## 路线

尚未排期，方向为：

- 冻结稳定 Module API 契约与版本策略。
- 发布独立 NuGet SDK。
- 提供官方模块模板（Rust 与托管两种）。
- 提供 `qtool` 验证与打包 CLI。
- 提供版本化测试宿主。

这些能力目前均未实现，也未承诺具体版本。
