# 贡献指南

本文说明 QingToolbox 的分支纪律、提交规范与验证流程。开始修改前请先读完本文。

## 开始之前

| 你的目标 | 先读这些 |
| --- | --- |
| 了解项目全貌 | [`README.md`](README.md) → [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) |
| 参与宿主开发 | [`docs/DEVELOPMENT_SETUP.md`](docs/DEVELOPMENT_SETUP.md) → [`docs/DEVELOPMENT_ENVIRONMENTS.md`](docs/DEVELOPMENT_ENVIRONMENTS.md) |
| 开发一个模块 | [`protocol/README.md`](protocol/README.md) → [`docs/TAURI_MODULE_LIFECYCLE.md`](docs/TAURI_MODULE_LIFECYCLE.md) → [`docs/MODULE_DEVELOPMENT.md`](docs/MODULE_DEVELOPMENT.md) |
| 接手项目上下文 | [`docs/DEVELOPMENT_PLAN.md`](docs/DEVELOPMENT_PLAN.md) 第 10 节 |

完整文档索引见根 [`README.md`](README.md) 的「文档地图」章节。

## 分支模型

| 分支 | 职责 |
| --- | --- |
| `toolbox` | 默认分支。宿主、协议、脚本、安装器、开发工具与文档。 |
| `modules` | 官方模块源码、模块模板与模块更新协议（历史 WPF 模块线）。 |
| `android` / `android_modules` | 移动端相关工作，独立于桌面主线。 |

**两个分支独立维护。** 宿主提交不得顺手修改 `modules`；模块协议变更也不得未经审查混入宿主提交。除非任务明确要求，否则不要改动 `modules` 分支。

当前开发主线是 `QingToolbox.Tauri`（Rust + Tauri 2 + Vue 3）。`QingToolbox.Shell` 及其相关的 .NET 项目属于历史 WPF 宿主，仅用于维护历史安装链与数据迁移。

## 开发环境

按 [`docs/DEVELOPMENT_SETUP.md`](docs/DEVELOPMENT_SETUP.md) 准备工具链（Rust stable、Node.js、WebView2、C++ 构建工具）。

**开发与测试必须使用项目本地隔离 Profile**，不得把开发数据写入正式安装目录：

```powershell
pwsh ./scripts/start-dev-host.ps1 -Profile Shell2        # Development 环境
pwsh ./scripts/start-module-test-host.ps1 -Profile Demo   # ModuleTest 环境
```

沙箱实例的窗口标题会显示 `QingToolbox [DEV: <Profile>]` 或 `QingToolbox [MODULE TEST: <Profile>]`。详见 [`docs/DEVELOPMENT_ENVIRONMENTS.md`](docs/DEVELOPMENT_ENVIRONMENTS.md)。

## 提交信息规范

提交信息必须描述改动的真实范围。**每个逻辑改动一行带标签的说明**，不要把互不相关的工作压缩成一句笼统的摘要；一个提交包含多类工作时，就写多行。

允许的标签：

```text
[+]        新增功能、文件或模块
[-]        移除功能、文件或依赖
[fix]      Bug、构建或引用修复
[refactor] 不改变行为的内部重构
[docs]     文档改动
[style]    UI、样式、动画或格式改动
[chore]    工程配置、依赖、脚本或维护
[test]     测试改动
```

示例：

```text
[+] add Tauri module package and update pipeline
[fix] preserve pinned qpdf checksum bytes on Windows checkouts
[test] cover module lifecycle transitions in isolated profiles
[docs] align module development entry points
```

第一行概述主要改动，后续行补充同一个提交中的其他有意义改动。

**编码要求：提交信息必须为 UTF-8 且不带 BOM。**

## 提交流程与验证

每次开始工作前先获取真实状态：

```powershell
git branch --show-current
git status --short
git diff
git fetch origin --tags
git rev-parse HEAD
git rev-parse origin/toolbox
git rev-list --left-right --count origin/toolbox...HEAD
git log -15 --format=fuller
```

规则：

- 本地存在未提交修改时，先审查并在其基础上继续，**不得覆盖或丢弃**。
- 本地 HEAD 领先远程时，先检查已有提交，不得 amend。
- 推送前执行 `git diff --check`。
- 只显式添加本轮涉及的源码、测试、脚本和文档。
- 推送后确认 `HEAD == origin/toolbox`。
- 使用 `gh run list` 与 `gh run watch` 核对**精确最终 SHA** 的运行结论。

改动宿主或模块时，至少运行：

```powershell
pwsh ./scripts/verify-tauri.ps1
```

发布相关改动请使用 `pwsh ./scripts/build-tauri-release-candidate.ps1`，或按 [`docs/PREVIEW_RELEASE_PROCESS.md`](docs/PREVIEW_RELEASE_PROCESS.md) 执行门禁。

## 不要提交的内容

- `artifacts/`、`publish/`、`bin/`、`obj/`。
- 安装器、ZIP、PDB、日志。
- 真实注册表数据、真实 Task XML、真实用户设置或测试用户数据。
- `.qingtoolbox/`（项目本地沙箱状态）。

## 绝对禁止的 Git 操作

- `git commit --amend`
- `git push --force`
- `git reset --hard`
- `--no-verify`
- 未经明确授权创建 tag 或 GitHub Release。
- 修改已发布的 `v0.1.0-alpha` tag 或 Release。

## 文档规范

**语言策略（每份文件内部保持单一语言）：**

| 类别 | 语言 |
| --- | --- |
| `README.md`、`CONTRIBUTING.md`、`SECURITY.md`、`docs/` 下的开发文档 | 简体中文 |
| `CHANGELOG.md`、`docs/releases/` 发布说明 | 英文（可附 `## 简体中文` 平行段落） |
| `protocol/`、`docs/TAURI_MODULE_LIFECYCLE.md`、`docs/MODULE_DEVELOPMENT.md` 等契约类文档 | 英文 |

避免在同一份文件里无规律地中英混排。需要双语的文档，请使用明确分隔的平行段落（如发布说明中的 `## 简体中文` / `## English`）。

**其他要求：**

- 新增或改名文档后，同步更新根 [`README.md`](README.md) 的「文档地图」表格。
- 文档中的版本号、命令与脚本路径必须与仓库实际状态一致。
- 描述未完成的能力时，明确写出它是未完成、`Not Run` 或 `Blocked`，不要把自动化结果当作人工验收通过。

## Pull Request

提交 Pull Request 前请确认：

- [ ] 改动范围与提交信息一致，没有夹带无关修改。
- [ ] `pwsh ./scripts/verify-tauri.ps1` 通过（如涉及宿主或模块）。
- [ ] 新增或修改了文档时，已同步更新根 `README.md` 的「文档地图」表格。
- [ ] 没有引入任何「禁止提交的内容」。
- [ ] 涉及未完成能力时，文档中已如实标注状态。
