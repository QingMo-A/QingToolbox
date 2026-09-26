<div align="center">

<img src="QingToolbox.Shell/Assets/Branding/QingToolbox.Mark.svg" alt="QingToolbox" width="112" height="112" />

# QingToolbox

**面向 Windows 的轻量模块化工具箱 · Rust + Tauri 2 + Vue 3**

宿主保持最小，工具按需以独立进程模块交付。

[![Host](https://img.shields.io/badge/host-0.3.1--alpha-blue?style=flat-square)](#版本状态)
[![Release](https://img.shields.io/badge/release-v0.3.0--alpha-blue?style=flat-square)](https://github.com/QingMo-A/QingToolbox/releases)
[![License](https://img.shields.io/github/license/QingMo-A/QingToolbox?style=flat-square&color=green)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?style=flat-square&logo=windows11&logoColor=white)](#项目简介)

[![Rust](https://img.shields.io/badge/Rust-stable-000000?style=flat-square&logo=rust&logoColor=white)](https://www.rust-lang.org/)
[![Tauri](https://img.shields.io/badge/Tauri-2.11-24C8DB?style=flat-square&logo=tauri&logoColor=white)](https://tauri.app/)
[![Vue](https://img.shields.io/badge/Vue-3.5-4FC08D?style=flat-square&logo=vuedotjs&logoColor=white)](https://vuejs.org/)
[![TypeScript](https://img.shields.io/badge/TypeScript-5.9-3178C6?style=flat-square&logo=typescript&logoColor=white)](https://www.typescriptlang.org/)

[![Tauri validation](https://github.com/QingMo-A/QingToolbox/actions/workflows/tauri-validation.yml/badge.svg)](https://github.com/QingMo-A/QingToolbox/actions/workflows/tauri-validation.yml)
[![Preview release validation](https://github.com/QingMo-A/QingToolbox/actions/workflows/preview-release-validation.yml/badge.svg)](https://github.com/QingMo-A/QingToolbox/actions/workflows/preview-release-validation.yml)

[![Last commit](https://img.shields.io/github/last-commit/QingMo-A/QingToolbox/toolbox?style=flat-square)](https://github.com/QingMo-A/QingToolbox/commits/toolbox)
[![Stars](https://img.shields.io/github/stars/QingMo-A/QingToolbox?style=flat-square)](https://github.com/QingMo-A/QingToolbox/stargazers)
[![Issues](https://img.shields.io/github/issues/QingMo-A/QingToolbox?style=flat-square)](https://github.com/QingMo-A/QingToolbox/issues)

[![Typing SVG](https://readme-typing-svg.demolab.com?font=JetBrains+Mono&weight=500&size=16&pause=1400&color=185FA5&center=true&vCenter=true&width=620&lines=Minimal+host%2C+independent+tool+modules.;Process-isolated+modules+on+a+versioned+JSON+protocol.;Rust+core+%2B+Tauri+2+%2B+Vue+3.)](https://github.com/QingMo-A/QingToolbox)

</div>

> **Alpha 状态提示**
>
> 当前发布为 Alpha。安装器与模块包均未数字签名，Windows 可能提示“未知发布者”或触发 SmartScreen。
> 请只从本仓库的 [Release 页面](https://github.com/QingMo-A/QingToolbox/releases) 下载，并校验同名的 `.sha256` 文件。

---

## 目录

- [项目简介](#项目简介)
- [设计原则](#设计原则)
- [功能特性](#功能特性)
- [架构总览](#架构总览)
- [快速开始](#快速开始)
- [模块体系](#模块体系)
- [项目结构](#项目结构)
- [版本状态](#版本状态)
- [文档地图](#文档地图)
- [安全模型](#安全模型)
- [参与贡献](#参与贡献)
- [License](#license)

---

## 项目简介

QingToolbox 是一个面向 Windows 的模块化桌面工具箱。宿主（Shell）不内置任何具体工具功能，只负责窗口、托盘、设置、模块发现与生命周期管理；具体能力由独立模块按需交付。

与常见的“一个进程里堆满插件”的做法不同，QingToolbox 的每个模块都运行在**独立的操作系统进程**中，通过一条版本化的 JSON 行协议与宿主通信。宿主在 Rust 侧持有路径、进程与全部安全决策，Vue 前端只接收不透明的 ID 和序列化快照，无法向操作系统提交任意路径或命令。

- 目标平台：Windows 10 / 11（x64）
- 宿主技术栈：Rust 核心 + Tauri 2 + Vue 3
- 模块形态：独立 Rust 进程 + Vue 页面，打包为 `.qmod`
- 安装方式：每用户安装，不需要管理员权限，不触发 UAC

## 设计原则

1. **宿主不内置工具。** 宿主只提供壳、生命周期与安全边界。
2. **模块默认不进入内存。** 扫描与刷新只读取清单，不加载、不反射、不执行模块代码。
3. **启动与驻留相分离。** Load / Enable / Disable / Unload / Delete 是五个独立动作，不会互相隐式触发。
4. **显式授权。** “随工具箱启动”必须由用户明确开启，并绑定完整载荷指纹。
5. **窄接口。** 模块只能调用清单 `operations` 白名单里的操作；不声明即无调用面。
6. **失败隔离。** 单个模块崩溃、超时或协议错误都不会终止宿主。
7. **诚实的状态。** 未执行的人工验收一律标记 `Not Run`，不以自动化结果替代。

## 功能特性

**宿主**

- 单实例运行，二次启动通过本地管道激活已有窗口。
- 原生无边框标题栏、通知区域图标与桌面悬浮标，窗口形态可随时切换。
- 关闭行为可选：询问、最小化到通知区域或完整退出。
- 全局快捷键（默认 `Ctrl+Alt+Space`）可切换主窗口显示状态。
- 宿主设置（语言、外观、字体、启动显示模式、最近模块）由 Rust 持有并原子写入 `settings.json`，损坏文件保留为有限数量的备份。
- 内置宿主自更新：校验官方 Release 资产身份与 SHA256 后，交给 Inno Setup 完成原地覆盖，应用不自行替换程序文件。
- 简体中文与英文界面。

**模块运行时**

- 版本化协议信封：单行 UTF-8 JSON 帧，1 MiB 上限，超限或畸形帧直接拒绝。
- 每次模块启动由系统 RNG 生成 nonce，`hello` 必须回显该 nonce 才会被标记就绪。
- 进程由 Rust 监督器管理，退出时先优雅收拢，超时后强制结束，且**只**处理本宿主创建的进程。
- 模块窗口通过 `module-<moduleId>` 标签推导身份，页面无法指定其他模块或提交可执行路径。

**模块分发**

- `.qmod` 为 ZIP 容器，导入前校验条目数、单文件大小、总展开大小与压缩比，拒绝绝对路径、路径穿越、重复项、符号链接与加密条目。
- 通过校验后在用户模块根下的随机临时目录解压，再以同卷原子重命名发布；已存在的同 ID 目录不会被覆盖。
- 官方模块更新由宿主校验版本、包大小与 SHA256 后，按“停用运行时 → 原子替换 → 启动验证 → 失败回滚 → 冷启动恢复”执行，且始终由用户明确确认。

## 架构总览

```text
Tauri 应用
├─ Rust 核心
│   ├─ 单实例 / 托盘 / 窗口 / 全局快捷键
│   ├─ 设置 / 权限边界 / 模块索引
│   ├─ 模块进程监督器
│   └─ 更新器与包校验
└─ Vue 界面（QingToolbox.WebUI）
    ├─ 壳导航与模块界面
    └─ 无直接文件系统或进程访问能力

模块进程（每个活动模块一个）
├─ Rust 后端（模块自有状态机与系统能力）
├─ Vue 资源（经受控 qmod:// 协议加载）
└─ 与宿主之间：版本化 JSON 行协议，走宿主创建的私有本地传输
```

数据流的关键约束：

- Rust 侧拥有路径、进程创建、模块状态和**全部**安全决策。
- Vue 只接收不透明 ID 与类型化快照，永远不向操作系统操作提交任意路径。
- 模块窗口的 IPC 权限仅匹配 `module-*` 标签，且仍需通过清单 `operations` 白名单二次校验。

## 快速开始

### 使用者

从 [Release 页面](https://github.com/QingMo-A/QingToolbox/releases) 下载 `QingToolbox-<version>-win-x64-tauri-setup.exe` 及其同名 `.sha256`，校验后安装。安装器只为当前用户安装，不需要管理员权限。

安装完成后启动 QingToolbox，进入“模块”页面导入 `.qmod` 模块包。导入与刷新只会发现并校验清单，**只有**用户主动点击“加载”后模块才会运行。

> 从历史 WPF 版本升级时，安装器会先把旧版随包模块迁往用户模块目录、备份旧宿主自有文件，再删除过期资源。备份位于 `%LOCALAPPDATA%\QingToolbox-MigrationBackups`，在确认迁移结果前请勿删除。

### 宿主开发

需要 Rust stable、Node.js、Windows WebView2 运行时，以及 Tauri 所需的 C++ 构建工具。

```powershell
# 完整门禁（协议、Rust 测试、Vue typecheck/build、资源暂存）
pwsh ./scripts/verify-tauri.ps1

# 连同桌面可执行文件与单实例启动烟测一起验证
pwsh ./scripts/verify-tauri.ps1 -BuildDesktop -SmokeDesktop

# 交互式开发启动
run-tauri-dev.bat
```

常用入口脚本：

| 脚本 | 用途 |
| --- | --- |
| `run-latest.bat` | 构建并启动 Tauri Release 宿主（默认入口） |
| `run-tauri-dev.bat` | 交互式开发启动（Vite + Tauri） |
| `run-tauri-production.bat` | 构建生产候选目录到 `artifacts/tauri-production/QingToolbox/` |
| `run-tauri-portable.bat` | 生成可体验的 portable 预览目录（含逐文件 SHA256 manifest） |
| `run-tauri-installer.bat` | 构建并烟测安装器候选 |
| `run-tauri-module-packages.bat` | 打包 `resources/modules` 中的固定 Rust 进程模块 |
| `run-legacy-wpf.bat` | 显式进入历史 WPF 宿主维护路径 |

这些入口都只作用于新 Tauri 宿主，不会替换或改写已有的 WPF 安装。

需要开发宿主与调试模块时，请使用项目本地隔离 Profile，不要把开发数据写入正式安装目录：

```powershell
pwsh ./scripts/start-dev-host.ps1 -Profile Shell2      # Development 环境
pwsh ./scripts/start-module-test-host.ps1 -Profile Demo # ModuleTest 环境
pwsh ./scripts/reset-local-profile.ps1 -Environment Development -Profile Shell2
```

沙箱实例的窗口标题会显示 `QingToolbox [DEV: <Profile>]` 或 `QingToolbox [MODULE TEST: <Profile>]`，避免与正式安装混淆。详见 [`docs/DEVELOPMENT_ENVIRONMENTS.md`](docs/DEVELOPMENT_ENVIRONMENTS.md)。

### 模块开发

当前主线是 **Tauri 进程模块**，开发前建议按以下顺序阅读：

1. [`protocol/README.md`](protocol/README.md) —— 宿主/模块通信契约与 JSON Schema。
2. [`docs/TAURI_MODULE_LIFECYCLE.md`](docs/TAURI_MODULE_LIFECYCLE.md) —— Load / Enable / Disable / Unload / Delete 语义。
3. [`docs/MODULE_DEVELOPMENT.md`](docs/MODULE_DEVELOPMENT.md) —— 模块职责、本地部署约束与排查清单。
4. `QingToolbox.Tauri/native-module-canary/` —— 可复现的最小 Rust 进程模块样例。

打包单模块或全部官方模块：

```powershell
pwsh ./scripts/package-tauri-module.ps1 -ModuleId qing.launcher -Smoke
pwsh ./scripts/package-tauri-modules.ps1 -SkipBuild -Smoke
```

产物与 SHA256 sidecar 位于 `artifacts/tauri-modules/`。

> `.NET` / WPF 进程内模块（依赖 `QingToolbox.Abstractions`）属于**历史维护路径**，当前 Tauri 宿主不会加载它们。相关源码、模板与文档位于独立的 [`modules` 分支](https://github.com/QingMo-A/QingToolbox/tree/modules)。

## 模块体系

官方原生模块随 0.3.0-alpha 一同交付，各自独立进程、独立能力边界：

| 模块 | 模块 ID | 版本 | 能力边界 |
| --- | --- | --- | --- |
| Qing Launcher | `qing.launcher` | 0.3.0 | 私有 Everything 运行时，仅返回不透明 `resultId` |
| QingTransfer | `qing.qingtransfer` | 0.3.0 | DNS-SD 发现 + nonce 绑定 TCP 探测 + SHA-256 校验传输 |
| Screen Pin | `qing.screenpin` | 0.2.0 | 有界 GDI 截图，仅向界面发放会话内 data URL |
| Window Topmost | `qing.windowtopmost` | 0.2.0 | 枚举可见窗口，HWND 保留在进程内并逐次复核 |
| PowerGuard | `qing.powerguard` | 0.2.0 | 连通性探测与关机倒计时，默认关闭，关机需确认令牌 |
| Text Tools | `qing.texttools` | 0.2.0 | 文本转换与剪贴板写入，2 MiB / 4 MiB 输入输出边界 |
| Qing PDF | `qing.pdf` | 0.1.0 | 固定 qpdf 运行时，合并 / 均分 / 提取 / 旋转 |
| Web Module Canary | `qing.canary` | 0.1.0 | 兼容性验证用测试模块，非通用工具 |

模块目录约定：

- `QingToolbox.exe` 同目录下的 `resources/modules`：随程序提供的模块（只读）。
- `%LOCALAPPDATA%\QingToolbox\Modules\<moduleId>`：用户导入模块。
- `%APPDATA%\QingToolbox\Data`：模块运行数据。
- `%APPDATA%\QingToolbox\settings.json`：用户设置。

模块 ID 必须与安装目录名**精确一致**。开发目录优先于用户目录。

## 项目结构

```text
QingToolbox/
├─ QingToolbox.Tauri/            # 新宿主：Rust 核心 + Tauri 2
│  ├─ src-tauri/src/             # 路径、设置、清单、协议、运行时、Web 资源
│  ├─ native-launcher/           # 各原生模块：独立 Rust 进程 + Vue 界面
│  ├─ native-pdf/  native-transfer/  native-texttools/
│  ├─ native-windowtopmost/  native-powerguard/  native-screenpin/
│  └─ native-module-canary/      # 最小可复现进程模块样例
├─ QingToolbox.WebUI/            # 正式 Vue 3 前端（设计系统 + 全部工作区）
├─ protocol/                     # 宿主/模块协议定义与 JSON Schema
├─ docs/                         # 开发文档（入口见本文「文档地图」）
├─ plans/                        # 编号实现计划（见 plans/README.md 索引）
├─ scripts/                      # 构建、验证、打包、发布脚本
├─ installer/                    # Inno Setup 安装器定义
├─ QingToolbox.Shell/  QingToolbox.Core/  QingToolbox.Abstractions/
├─ QingToolbox.ModuleHost/  QingToolbox.ModuleLoader/
└─ QingToolbox.DevTools.*/       # 独立开发验证工具（非应用运行时组成部分）
```

`QingToolbox.Shell` 及其后的项目属于**历史 WPF 宿主**，仅用于维护历史安装链与数据迁移，新宿主不会引用或加载它们。

## 版本状态

| 发行线 | 当前版本 | 状态 |
| --- | --- | --- |
| Tauri 宿主（主线） | `0.3.1-alpha`（开发中） | 最新已发布 `v0.3.0-alpha`（2026-09-25） |
| WPF 宿主（历史） | `0.2.9-alpha` | 已冻结，仅维护历史安装链 |

`0.3.0-alpha` 是首个 Rust/Tauri 2/Vue 3 宿主预览，沿用原有 QingToolbox 产品 AppId，因此可以就地覆盖既有的 WPF 或 Tauri 安装，不会产生第二条卸载记录。

仍未完成、也**不应**被当作已完成的能力：

- 代码签名与真实用户升级验收。
- 稳定版 Module API（当前为 Experimental，不保证 0.x 二进制兼容）。
- 独立 NuGet SDK、`dotnet new` 模板与 `qtool` CLI。
- WPF 宿主的正式退役（需先完成基线指标对比与模块一致性检查）。

版本明细与发布说明见 [`CHANGELOG.md`](CHANGELOG.md) 与 [`docs/releases/`](docs/releases/)。

## 文档地图

下面这张表就是全部文档的入口。每篇都标注了状态，阅读前先看这一格：

| 标记 | 含义 |
| --- | --- |
| **当前** | 描述当前主线的真实状态，可以作为开发依据。 |
| **冻结** | 已完成并封板。除 P0/P1 缺陷外不再扩展。 |
| **历史** | 已退役或仅用于维护历史路径，**不要**作为新开发的依据。 |
| **参考** | 背景资料，不构成行为约束。 |

> 当前主线是 `QingToolbox.Tauri`（Rust + Tauri 2 + Vue 3），模块是独立 Rust 进程。
> 文档中出现 `.NET`、WPF、`IToolModule`、`AssemblyLoadContext` 等内容时，均属**历史 WPF 路径**，新宿主不会加载旧 DLL 模块。

### 按目标找入口

| 你的目标 | 建议阅读顺序 |
| --- | --- |
| 了解项目全貌 | [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) → [`docs/TAURI_MIGRATION.md`](docs/TAURI_MIGRATION.md) |
| 参与宿主开发 | [`docs/DEVELOPMENT_SETUP.md`](docs/DEVELOPMENT_SETUP.md) → [`docs/DEVELOPMENT_ENVIRONMENTS.md`](docs/DEVELOPMENT_ENVIRONMENTS.md) → [`docs/DEVELOPMENT_PLAN.md`](docs/DEVELOPMENT_PLAN.md) |
| 开发一个模块 | [`protocol/README.md`](protocol/README.md) → [`docs/TAURI_MODULE_LIFECYCLE.md`](docs/TAURI_MODULE_LIFECYCLE.md) → [`docs/MODULE_DEVELOPMENT.md`](docs/MODULE_DEVELOPMENT.md) |
| 打包与分发模块 | [`docs/QMOD_FORMAT.md`](docs/QMOD_FORMAT.md) → [`docs/QMOD_STAGING_SECURITY.md`](docs/QMOD_STAGING_SECURITY.md) |
| 发布新版本 | [`docs/PREVIEW_RELEASE_PROCESS.md`](docs/PREVIEW_RELEASE_PROCESS.md) → [`installer/README.md`](installer/README.md) |
| 接手项目上下文 | [`docs/DEVELOPMENT_PLAN.md`](docs/DEVELOPMENT_PLAN.md) 第 10 节「项目上下文恢复提示词」 |

### 入门与架构

| 文档 | 状态 | 说明 |
| --- | --- | --- |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | 当前 | 分层结构、Rust 核心职责、Vue 边界、模块清单发现。含历史 WPF 契约章节。 |
| [`docs/TAURI_MIGRATION.md`](docs/TAURI_MIGRATION.md) | 当前 | 迁移目标与边界、版本化模块协议、包策略、M0–M4 阶段与验收门。**含未完成项清单。** |
| [`docs/DEVELOPMENT_PLAN.md`](docs/DEVELOPMENT_PLAN.md) | 当前 | 开发路线、发布边界、不可突破的架构约束、项目交接提示词。 |
| [`protocol/README.md`](protocol/README.md) | 当前 | 宿主/模块通信契约：传输、信封、生命周期、`operations` / `events` 白名单、模块窗口桥。 |

`protocol/` 同时提供机器可校验的 JSON Schema：

- [`protocol/module-protocol.v1.schema.json`](protocol/module-protocol.v1.schema.json)
- [`protocol/module-manifest.tauri.v1.schema.json`](protocol/module-manifest.tauri.v1.schema.json)
- [`protocol/qmod.tauri.v1.schema.json`](protocol/qmod.tauri.v1.schema.json)

### 开发环境与流程

| 文档 | 状态 | 说明 |
| --- | --- | --- |
| [`docs/DEVELOPMENT_SETUP.md`](docs/DEVELOPMENT_SETUP.md) | 当前 | 工具链要求、`verify-tauri.ps1`、常用脚本入口、开发模块部署。含历史 WPF 章节。 |
| [`docs/DEVELOPMENT_ENVIRONMENTS.md`](docs/DEVELOPMENT_ENVIRONMENTS.md) | 当前 | Production / Development / ModuleTest 三环境隔离契约、沙箱路径派生、Profile 重置。 |
| [`CONTRIBUTING.md`](CONTRIBUTING.md) | 当前 | 分支纪律、提交信息规范、验证与发布流程。 |

### 模块开发

| 文档 | 状态 | 说明 |
| --- | --- | --- |
| [`docs/TAURI_MODULE_LIFECYCLE.md`](docs/TAURI_MODULE_LIFECYCLE.md) | 当前 | Load / Enable / Disable / Unload / Delete 的独立语义、进程契约与回归检查。 |
| [`docs/MODULE_DEVELOPMENT.md`](docs/MODULE_DEVELOPMENT.md) | 当前（Tauri） | 模块职责、Web 模块适配清单、本地部署约定与常见故障诊断。后半部分为历史 WPF API。 |
| [`docs/sdk/README.md`](docs/sdk/README.md) | 当前 | Module API 现状（Experimental）与 SDK 路线。 |
| [`docs/LOCALIZATION.md`](docs/LOCALIZATION.md) | 历史 | 清单本地化契约仍然有效；View 本地化示例为历史 WPF 路径。 |
| [`docs/WEB_SHELL_FOUNDATION.md`](docs/WEB_SHELL_FOUNDATION.md) | 历史 | WPF 宿主 Development Web Shell（protocol v4、桥边界、资源身份）。已由 Tauri WebUI 取代。 |
| [`plans/README.md`](plans/README.md) | 当前 | 编号实现计划索引（计划 001–016），含 Android 移动端壳层路线。 |
| [`docs/plans/PLAN_013_HOST_SELF_UPDATE.md`](docs/plans/PLAN_013_HOST_SELF_UPDATE.md) | 冻结 | 宿主应用内自更新计划全文。 |

### 模块分发与更新

| 文档 | 状态 | 说明 |
| --- | --- | --- |
| [`docs/QMOD_FORMAT.md`](docs/QMOD_FORMAT.md) | 当前 | `.qmod` 容器格式与校验规则。 |
| [`docs/QMOD_STAGING_SECURITY.md`](docs/QMOD_STAGING_SECURITY.md) | 当前 | 安全暂存边界：路径、ZIP Bomb、Manifest 与原子发布。 |
| [`docs/MODULE_PACKAGE_DOWNLOAD.md`](docs/MODULE_PACKAGE_DOWNLOAD.md) | 当前 | 官方模块包的下载、完整校验与暂存工作流。 |
| [`docs/MODULE_UPDATE_DETECTION.md`](docs/MODULE_UPDATE_DETECTION.md) | 当前 | 只读更新检测：条件请求缓存与官方元数据。 |
| [`docs/MODULE_UPDATE_TRANSACTION.md`](docs/MODULE_UPDATE_TRANSACTION.md) | 冻结 | B1 可恢复模块更新事务核心（仅 Development / ModuleTest）。 |
| [`docs/MODULE_UPDATE_RUNTIME_ADAPTER.md`](docs/MODULE_UPDATE_RUNTIME_ADAPTER.md) | 冻结 | B2.1 生命周期适配器、启动恢复门禁与运行时边界。 |
| [`docs/TEXTTOOLS_UPDATE_CANARY.md`](docs/TEXTTOOLS_UPDATE_CANARY.md) | 冻结 | 固定来源的 TextTools 更新金丝雀验证。 |

### 发布与验收

| 文档 | 状态 | 说明 |
| --- | --- | --- |
| [`docs/PREVIEW_RELEASE_PROCESS.md`](docs/PREVIEW_RELEASE_PROCESS.md) | 当前 | 候选构建门禁、产物溯源与人工发布交接清单。 |
| [`docs/PREVIEW_2_ACCEPTANCE_CHECKLIST.md`](docs/PREVIEW_2_ACCEPTANCE_CHECKLIST.md) | 历史 | Preview 2 人工验收清单。**多数项目为 `Not Run`，不代表已通过。** |
| [`docs/TAURI_PORTABLE_PREVIEW.md`](docs/TAURI_PORTABLE_PREVIEW.md) | 当前 | Tauri portable / production 候选目录的生成与内容说明。 |
| [`docs/TAURI_PRODUCT_CUTOVER.md`](docs/TAURI_PRODUCT_CUTOVER.md) | 当前 | 产品 AppId 切换、WPF 迁移与回滚边界。 |
| [`installer/README.md`](installer/README.md) | 当前 | Inno Setup 安装器定义与构建说明。 |
| [`docs/releases/`](docs/releases/) | 当前 | 逐版本发布说明（见下表）。 |

### Windows 平台细节与决策记录

| 文档 | 状态 | 说明 |
| --- | --- | --- |
| [`docs/WINDOWS_STARTUP_RELIABILITY.md`](docs/WINDOWS_STARTUP_RELIABILITY.md) | 历史 | 历史 WPF 宿主的登录自启动：Task Scheduler、注册表降级、启动路径与诊断。 |
| [`docs/ICON_ASSETS.md`](docs/ICON_ASSETS.md) | 参考 | 历史 WPF Shell 的图标来源映射（Nieobie Game Icon Pack, CC0）。 |
| [`docs/architecture/ADR-001-wpf-module-process-isolation.md`](docs/architecture/ADR-001-wpf-module-process-isolation.md) | 历史 | 记录旧 WPF 模块运行时的进程隔离决策。Tauri 主线已采用全面进程隔离。 |

### 逐版本发布说明

| 版本 | 发行线 | 日期 |
| --- | --- | --- |
| [`0.3.0-alpha`](docs/releases/0.3.0-alpha.md) | Tauri 宿主 | 2026-09-25 |
| [`tauri-modules-0.3.0-alpha`](docs/releases/tauri-modules-0.3.0-alpha.md) | 原生模块包清单 | 2026-09-25 |
| [`launcher-0.3.0`](docs/releases/launcher-0.3.0.md) | Qing Launcher 模块 | 2026-09-25 |
| [`0.2.9-alpha`](docs/releases/0.2.9-alpha.md) | WPF 宿主 | 2026-08-21 |
| [`0.2.8-alpha`](docs/releases/0.2.8-alpha.md) | WPF 宿主 | 2026-08-16 |
| [`0.2.7-alpha`](docs/releases/0.2.7-alpha.md) | WPF 宿主 | 2026-08-15 |
| `0.2.6-alpha` | — | **缺失**：该版本没有独立发布说明文件 |
| [`0.2.5-alpha`](docs/releases/0.2.5-alpha.md) | WPF 宿主 | 2026-08-11 |
| [`0.2.4-alpha`](docs/releases/0.2.4-alpha.md) | WPF 宿主 | 2026-08-01 |
| [`0.2.3-alpha`](docs/releases/0.2.3-alpha.md) | WPF 宿主 | 2026-08-01 |
| [`0.2.2-alpha`](docs/releases/0.2.2-alpha.md) | WPF 宿主 | 2026-07-31 |
| [`0.2.1-alpha`](docs/releases/0.2.1-alpha.md) | WPF 宿主 | 2026-07-31 |
| [`0.2.0-alpha`](docs/releases/0.2.0-alpha.md) | 内部目标（未发布） | 2026-07-29 |
| [`0.1.0-alpha`](docs/releases/0.1.0-alpha.md) | WPF 宿主 | 2026-07-16 |

完整版本变更记录见 [`CHANGELOG.md`](CHANGELOG.md)。

> 文档维护备注与**已知文档缺口**清单见 [`docs/README.md`](docs/README.md)。新增或改名文档时，请同步更新本节表格。

## 安全模型

**已实现**

- 模块在独立进程中以当前用户权限运行，宿主不建立通用文件系统或进程桥。
- 模块清单的 `operations` / `events` 数组是白名单；不声明即无调用面。
- `.qmod` 导入执行 ZIP 结构与路径安全校验，并以同卷原子重命名发布。
- 官方模块更新与宿主自更新均校验版本、包大小与 SHA256。
- 模块窗口 IPC 权限按 `module-*` 标签隔离，不继承文件系统或进程权限。

**明确不是安全机制的部分**

- 权限字段仅用于声明与用户告知，**不构成强制沙箱**。
- `.qmod` 尚不支持包签名，因此只应导入可信来源的模块。
- 模块加载后拥有当前用户权限，其行为不受宿主强制约束。

漏洞报告方式见 [`SECURITY.md`](SECURITY.md)。

## 参与贡献

分支纪律、提交信息规范与验证流程见 [`CONTRIBUTING.md`](CONTRIBUTING.md)。

简要说明：`toolbox` 分支承载宿主、协议、脚本与文档；`modules` 分支承载模块源码与模板。两个分支独立维护，宿主提交不得顺手修改 `modules`，模块协议变更也不得未经审查混入宿主提交。

## License

QingToolbox 使用 [MIT License](LICENSE)。

Shell 使用的 Nieobie Game Icon Pack 图标遵循 CC0 1.0。第三方组件许可见 [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) 与 [`QingToolbox.Tauri/THIRD_PARTY_NOTICES.md`](QingToolbox.Tauri/THIRD_PARTY_NOTICES.md)。

---

## Star 趋势

<a href="https://star-history.com/#QingMo-A/QingToolbox&Date">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=QingMo-A/QingToolbox&type=Date&theme=dark" />
    <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=QingMo-A/QingToolbox&type=Date" />
    <img alt="Star History Chart" src="https://api.star-history.com/svg?repos=QingMo-A/QingToolbox&type=Date" width="640" />
  </picture>
</a>

<br />

![Visitors](https://komarev.com/ghpvc/?username=QingMo-A&repo=QingToolbox&label=visitors&color=185FA5&style=flat-square)

<!--
  上面两组图表由第三方服务渲染（star-history.com / komarev.com）。
  在部分网络环境下可能加载较慢或被拦截；如影响阅读可直接删除本节。
  徽章使用 shields.io，若需离线文档体验也可整体替换为静态文本。
-->

---

<details>
<summary><b>English overview</b></summary>

<br />

QingToolbox is a modular Windows toolbox built on a Rust/Tauri 2 core with Vue 3 surfaces. The host ships no tools of its own: it owns windows, the tray, settings, module discovery and lifecycle management, while every concrete capability is delivered as an independent process module.

Each module runs in its own operating system process and talks to the host over a versioned newline-delimited JSON protocol. The Rust core owns paths, process creation, module state and all security decisions; the Vue front end receives only opaque IDs and typed snapshots and can never hand an arbitrary path to an operating system operation.

- Target platform: Windows 10 / 11 (x64)
- Host stack: Rust + Tauri 2 + Vue 3
- Module format: `.qmod` (ZIP container, unsigned)
- Distribution: per-user installer, no administrator rights required

This is an Alpha project. The installer and module packages are not code-signed. Download only from this repository's releases and verify the supplied `.sha256` sidecar.

Start with the **文档地图** section above for the full documentation index, [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the architecture, and [`protocol/README.md`](protocol/README.md) for the host/module wire contract.

</details>
