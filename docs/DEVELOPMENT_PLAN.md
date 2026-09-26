# QingToolbox 开发计划

> 本文是 QingToolbox 的当前开发路线、发布边界与项目交接说明。
>
> **文档同步状态**：更新于 2026-09-26，对应宿主主线 `0.3.1-alpha`（开发中）与最新发布 `v0.3.0-alpha`。
> 继续开发前必须重新获取远程状态，不得假设该基线始终是最新版本。

## 0. 当前状态速览

| 项目 | 值 |
| --- | --- |
| 默认分支 | `toolbox` |
| 模块源码分支 | `modules` |
| 宿主主线技术栈 | Rust + Tauri 2 + Vue 3（`QingToolbox.Tauri`） |
| 宿主当前版本 | `0.3.1-alpha`（开发中） |
| 最新已发布宿主版本 | `v0.3.0-alpha`（2026-09-25） |
| 历史 WPF 宿主版本 | `0.2.9-alpha`（冻结，仅维护历史安装链） |
| 正式分发方式 | 每用户 Inno Setup 安装器 + 同名 `.sha256` |
| 代码签名 | **未完成** |
| 稳定 Module API | **未冻结**（当前 Experimental） |

已发布版本与证据：

| 版本 | 发布日期 | 关键证据 |
| --- | --- | --- |
| `0.3.0-alpha` | 2026-09-25 | 首个 Tauri 宿主预览；Tag `v0.3.0-alpha` |
| `0.2.9-alpha` | 2026-08-21 | 提交 `d4b30d5`；Preview 验证 run `32462342007` 通过 |
| `0.2.8-alpha` | 2026-08-16 | 提交 `b6a7cc2`；Preview 验证 run `31951259828` 通过 |
| `0.2.7-alpha` | 2026-08-15 | Tag `v0.2.7-alpha` |
| `0.2.6-alpha` | 2026-08-15 | 无独立发布说明文件（见 `docs/releases/`） |
| `0.2.5-alpha` | 2026-08-11 | Tag `v0.2.5-alpha` |
| `0.1.0-alpha` | 2026-07-16 | Preview 1；含便携 ZIP 与安装器 |

## 1. 项目定位

QingToolbox 是一个面向 Windows 的模块化桌面工具箱。宿主只提供壳、生命周期与安全边界，具体工具能力由独立模块按需交付。

项目核心目标：

- 宿主长期稳定运行，不因单个模块故障退出。
- 模块发现、加载、启动授权、更新和删除具有明确的安全边界。
- Production、Development、ModuleTest 三种环境严格隔离。
- 发布过程可复现、可审计，并通过安装器、宿主载荷审计和升级测试验证。
- 可靠基础设施与宿主自更新已完成并冻结；后续优先推进用户可见工具与现代化 UI。

当前开发分支：

- `toolbox`：宿主、协议、脚本、安装器、开发工具与文档。
- `modules`：官方模块源码、模块模板与模块更新协议（历史 WPF 模块线）。

两个分支独立维护。宿主开发不得顺手修改 `modules`，模块协议变更也不得未经审查混入宿主提交。

## 2. 不可突破的架构边界

### 2.1 宿主与模块

Tauri 主线（当前）：

- 宿主不得引用任何具体模块的实现代码。
- 宿主与模块之间只允许存在 [`protocol/`](../protocol/README.md) 定义的版本化进程协议。
- 模块在独立操作系统中以当前用户权限运行；宿主不得建立通用文件系统、进程或窗口桥。
- 模块只能调用自己清单 `operations` / `events` 白名单中声明的操作；不声明即无调用面。
- 模块窗口的 IPC 权限按 `module-*` 标签隔离，页面不得提交任意模块 ID 或路径。
- 单个模块失败、超时或协议错误必须被隔离，不能终止宿主。
- 宿主创建的模块进程由宿主收拢；不得触碰用户自行启动的同名进程。

历史 WPF 线（冻结）：

- 模块扫描阶段只读取 `module.json` 和本地化资源。
- 扫描期间禁止加载、反射或执行模块 DLL。
- 模块 DLL 只能在用户明确操作或已验证的启动授权恢复阶段加载。
- 宿主项目不得引用任何具体模块程序集。

### 2.2 环境隔离

Production、Development、ModuleTest 必须分别隔离：

- 设置文件。
- 用户模块目录。
- 模块数据目录。
- 缓存。
- 单实例 Mutex。
- 激活 Pipe。
- 更新检测和测试数据。

Development 和 ModuleTest 不得注册真实 Windows 开机自启，不得污染正式用户数据。

沙箱根目录唯一派生自 `<RepositoryRoot>\.qingtoolbox\development\<Profile>` 或
`<RepositoryRoot>\.qingtoolbox\module-test\<Profile>`；宿主校验稳定的源码标记，不得从当前目录、`.git`、可执行文件位置或解决方案父目录猜测仓库根。相关路径段中的符号链接、junction 等 reparse point 必须在启动前被拒绝。

### 2.3 Git 与发布

- 未经用户明确授权，不创建 tag 或 GitHub Release。
- 不修改已经发布的 `v0.1.0-alpha` tag 和 Release。
- 不使用 `git commit --amend`、`git push --force`、`git reset --hard` 或 `--no-verify`。
- 不覆盖未提交的用户修改。
- 提交信息沿用四段式风格，并保证 UTF-8 无 BOM。
- 远程验证必须匹配精确最终 `HEAD` SHA；其他提交的成功 CI 不能代替当前提交。

## 3. 已完成能力

### 3.1 Tauri 宿主（主线）

- 单实例运行：实例锁 + 本地管道激活，二次启动切换到已有窗口。
- 窗口与界面：原生无边框标题栏、通知区域图标、桌面悬浮标、关闭行为（询问 / 托盘 / 退出）、可配置启动显示模式。
- 全局快捷键：固定版本 plugin 在 Rust setup 阶段注册，默认 `Ctrl+Alt+Space`，可在设置中修改。
- 登录自启动：固定版本 autostart 插件在 Rust 侧同步，设置页可读取真实注册状态并发起修复；开发/烟测默认不产生副作用。
- 设置持有：语言、外观、字体、关闭行为、启动显示模式、最近模块、模块启动授权均由 Rust 持有并原子写入共享 `settings.json`，保留未知旧字段并生成有限数量的损坏备份。
- 字体：内置安全系统字体目录 + 以 SHA-256 ID 管理的用户导入字体，经受控 `qfont://` 协议读取，WebView 无法提交任意路径。
- 宿主自更新：官方 Release 检查、安装器下载、sidecar/大小/SHA256 校验、安装记录与 production marker 复核，最后由用户确认并交给 Inno Setup 静默交接。
- 会话日志：Rust 持有有界内存日志，Vue 通过 `get_session_logs` 读取，不暴露路径或句柄。
- `.qmod` 导入：ZIP 结构校验、条目/大小/压缩比上限、路径穿越与 reparse point 拒绝、同卷原子发布。
- 模块窗口：受控 `qmod://` 资源协议，每次请求重新校验模块目录边界。
- 界面双语：简体中文与英文。

### 3.2 模块运行时与生命周期

- 版本化协议信封：单行 UTF-8 JSON 帧，1 MiB 上限。
- nonce-bound `hello` 握手：每次模块启动使用系统 RNG nonce，必须回显才标记就绪。
- 驻留与激活分离：Load / Enable / Disable / Unload / Delete 是独立动作。
- 打开界面与一次性操作不会隐式启用后台工作。
- 未加载模块不会被陈旧 UI 调用、事件或排队快捷键回调重新拉起。
- 授权启动的模块会显式 Load **并** Enable；Disable 不撤销用户独立的启动授权。
- 协议损坏或超时是可见失败，不会被投影为成功停用。
- 缺少生命周期契约的旧模块明确失败并要求更新，宿主不得把终止旧模块伪装成停用。

详见 [`docs/TAURI_MODULE_LIFECYCLE.md`](TAURI_MODULE_LIFECYCLE.md)。

### 3.3 模块发现、授权与更新

- 官方模块索引与每模块更新协议；SemVer 与宿主兼容性选择。
- ETag / Last-Modified 条件请求与隔离缓存。
- 手动下载 `.qmod`，含流式大小限制、长度验证与 SHA256 校验。
- 用户明确开启的“随工具箱启动”授权，绑定递归覆盖依赖、原生库、配置、本地化与资源的完整载荷 SHA256；模块文件变化后必须重新确认。
- Production / Development 中由用户确认的已安装模块更新：严格 `qmod.json` 暂存、运行时停用、原子替换、回滚与冷启动恢复。
- 拒绝任意本地同 ID `.qmod` 覆盖，不支持自动安装。

必须准确描述为：

> 检测模块更新，并在用户确认下由宿主验证、原子替换、失败回滚。

不得描述为：

> 自动更新或自动安装模块。

### 3.4 发布与验证基础设施

- Debug / Release 构建与 Vue typecheck/build。
- Rust 宿主测试、协议 canary、模块窗口 IPC 烟测与八个原生模块烟测套件。
- 本地环境契约测试与 Profile 隔离校验。
- Tauri release candidate gate（`scripts/build-tauri-release-candidate.ps1`）：从干净 `toolbox` HEAD 执行全部官方模块/Everything/桌面验证、打包 `.qmod` 候选、构建生产宿主与 Tauri 专属 Inno 安装器、执行真实安装/宿主/卸载烟测，并把 manifest、安装器与 SHA256 sidecar 绑定回同一源码提交。
- 安装器：固定版本 Inno Setup 供应链、逐用户安装、载荷 manifest 与逐文件 SHA256 校验。
- GitHub Actions 双工作流：`tauri-validation` 与 `preview-release-validation`。
- 隔离用户目录中的安装—卸载往返、Repair、降级保护与原地升级验证。
- 精确最终 HEAD 的 `workflow_dispatch` 调度与结论核对工具。

### 3.5 历史 WPF 发行线（冻结）

0.2.x 线已完成并冻结，仅用于维护历史安装链与数据迁移：

- Task Scheduler 首选、HKCU Run 降级的可靠登录自启动。
- Pipe-first 启动、可见呈现先于模块发现、Startup Health Journal 与 Explorer 恢复。
- Host Payload Manifest 与精确废弃文件清理。
- 固定 AppId 的原地覆盖升级、同版本 Repair、SemVer 降级保护。
- B1 可恢复模块更新事务核心与 B2.1 生命周期适配（**Engineering Complete — Frozen**，仅限 Development / ModuleTest）。
- Plan 013 宿主自更新（**Implementation Complete / Frozen**）。

## 4. 当前发布目标

**Tauri 主线**：推进 `0.3.1-alpha`，在 0.3.0-alpha 之后继续补齐用户可见体验与生产加固，并在授权后推进签名与真实用户迁移验收。

**已达成**：`0.3.0-alpha`（2026-09-25）完成首次 Tauri 宿主公开预览，且在原有产品 AppId 下实现了从 WPF 的原地迁移。

## 5. 已冻结的计划

| 计划 | 状态 |
| --- | --- |
| Plan 013 宿主自更新 | Implementation Complete / Frozen |
| B1 模块更新事务核心 | Engineering Complete — Frozen（仅 Development / ModuleTest） |
| B2.1 生命周期适配与 TextTools 金丝雀 | Engineering Complete — Frozen |
| UI-1 ~ UI-4A / Plan 005–011 | Engineering Complete — Frozen |
| Plan 012 Development Web 本地化 | Implementation Complete |
| Plan 015 Qing Launcher 模块 | 已随 0.3.0-alpha 交付 |
| Plan 016 Web 模块宿主版本边界 | 已由 0.2.6 / 0.2.7-alpha 交付 |

编号计划的完整索引见 [`plans/README.md`](../plans/README.md)。

除发现明确 P0/P1 缺陷外，不再对已冻结边界追加普通边缘加固。

## 6. 发布门禁

### 6.1 自动化门禁

Tauri 主线必须通过：

```powershell
pwsh ./scripts/verify-tauri.ps1
pwsh ./scripts/verify-tauri.ps1 -BuildDesktop -SmokeDesktop -SmokeEverything
pwsh ./scripts/build-tauri-release-candidate.ps1
```

覆盖范围：Rust `cargo check` 与宿主测试、Vue typecheck/build、协议 canary、八个原生模块烟测、Release 资源暂存、模块窗口 IPC 烟测、`.qmod` 打包、生产宿主构建、Tauri 专属 Inno 安装器与真实安装/宿主/卸载烟测。

历史 WPF 线保留门禁：`dotnet restore`、Debug/Release 构建、Module Load / Module Update / Module Package Download / Startup Reliability Smoke Test、Local Environment Contracts、Installer Roundtrip、原地升级与 Repair、Host Payload Manifest 校验、废弃文件清理与未知文件保留。

CI 与 RC Gate 必须使用精确最终 HEAD。没有对应官方旧安装器时，不得声称完整 Upgrade Gate 或 RC Gate 已通过。

### 6.2 人工验收

以下项目必须保持 **Not Run** 或 **Blocked**，除非在验收清单中另有记录：

- 真实 Windows 普通用户环境下的安装、升级、Repair 与卸载。
- 从已安装 WPF 版本的首次迁移。
- 登录自启动与重新登录后的可见启动。
- 签名与 SmartScreen 行为。
- 多显示器、跨 DPI 与 Windows 11 Snap 弹层。
- 代表性环境验收。

未实际执行的人工项目不得报告为 Pass，自动化结果不得替代人工结果。

## 7. 明确不包含的范围

当前版本明确不提供：

- 代码签名与数字签名信任链。
- 稳定版 Module API 与 0.x 二进制兼容承诺。
- 独立 NuGet SDK、`dotnet new` 模板与 `qtool` CLI。
- 模块自动安装（所有更新必须由用户明确确认）。
- 任意本地同 ID `.qmod` 覆盖。
- 第三方模块更新源。
- Windows Service、SYSTEM 或管理员级任务。
- Qmod 包签名。

## 8. 后续路线

### 8.1 阶段 C：Module API 与 SDK（0.4.0-alpha 方向）

- 冻结稳定 Module API 边界。
- 发布独立 NuGet SDK。
- 提供模块模板与端到端开发文档。
- 明确 API 兼容性策略。
- 完善权限声明与能力模型。
- 提供版本化测试宿主。

### 8.2 Tauri 模块开发体验补齐

当前 Tauri 模块开发需要读者自行组合多处资料。已知缺口：

- 缺少 Rust 模块模板（现有模板属于历史 WPF 进程内模块）。
- 缺少一份端到端「新建 Tauri 模块」指南。
- `modules` 分支仍停留在 WPF 模块线，需要在主文档中持续标注其状态。

### 8.3 M4：退役 WPF

- 对比 WPF 与 Tauri 的冷启动、空闲内存、隐藏/恢复延迟与模块启动延迟基线指标（该对比数据集仍为 pending）。
- 在所有官方模块通过一致性检查清单后，移除 WPF 宿主与旧的进程内加载器。

## 9. 提交与验证工作流

每次开始工作前执行：

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

- 本地存在修改时，先审查并在其基础上继续，禁止覆盖或丢弃。
- 本地 HEAD 领先远程时，先检查已有提交，不得 amend。
- 推送前执行 `git diff --check`。
- 只显式添加本轮源码、测试、脚本和文档。
- 不提交 `artifacts/`、`publish/`、`bin/`、`obj/`、安装器、ZIP、PDB、日志、真实注册表数据、真实 Task XML、设置或测试用户数据。
- 推送后确认 `HEAD == origin/toolbox`。
- 使用 `gh run list` 与 `gh run watch` 核对精确最终 SHA。

提交信息规范见 [`CONTRIBUTING.md`](../CONTRIBUTING.md)。

## 10. 项目上下文恢复提示词

下面的提示词用于在新的对话中恢复项目上下文。粘贴后，新环境应先读取本文件、根 `README.md` 与远程仓库状态，再开始任何修改。

```text
你正在接手 QingMo-A/QingToolbox 项目。

请先读取并遵守仓库中的：

docs/DEVELOPMENT_PLAN.md
README.md
docs/TAURI_MIGRATION.md
protocol/README.md

仓库：QingMo-A/QingToolbox
默认分支与宿主开发分支：toolbox（当前宿主版本 0.3.1-alpha，最新发布 v0.3.0-alpha）
模块源码分支：modules（历史 WPF 模块线，独立维护）

当前项目主线是 Rust + Tauri 2 + Vue 3 的 Windows 模块化工具箱。
宿主持有窗口、托盘、设置、模块发现与进程监督；模块是独立 Rust 进程，
通过 protocol/ 中的版本化 JSON 行协议通信，打包为 .qmod。
旧 .NET/WPF 宿主仅作为尚未退役的历史安装链保留，新宿主不会加载旧 DLL。

最重要的架构约束：

1. 宿主不得引用任何具体模块的实现代码。
2. 宿主与模块之间只允许存在 protocol/ 定义的版本化进程协议。
3. 模块只能调用清单 operations / events 白名单中声明的操作。
4. 宿主不得建立通用文件系统、进程或窗口桥；Vue 不得提交任意路径。
5. Production、Development、ModuleTest 的设置、模块、缓存、Mutex 和 Pipe 必须隔离。
6. 单个模块失败不能终止宿主。
7. 未经我明确授权，不得创建 tag 或 GitHub Release。
8. 不得修改已有 v0.1.0-alpha tag 或 Release。
9. 不得 amend、force push、reset --hard 或使用 --no-verify。
10. 不得覆盖本地未提交修改。
11. modules 分支与 toolbox 分支独立，除非任务明确要求，否则不要修改 modules。
12. 所有远程验证必须匹配精确最终 HEAD SHA。

当前已经具备的能力：

- Tauri 宿主：单实例、托盘、悬浮标、全局快捷键、登录自启动、Rust 持有设置与字体。
- 宿主自更新：官方 Release 校验 + SHA256 + Inno Setup 交接。
- 模块运行时：nonce 握手、1 MiB 帧限制、独立的 Load/Enable/Disable/Unload/Delete。
- 模块发现、启动授权（完整载荷 SHA256）与用户确认的模块更新事务。
- .qmod 导入与安全暂存。
- 八个原生模块：Qing Launcher、QingTransfer、Screen Pin、Window Topmost、
  PowerGuard、Text Tools、Qing PDF，以及验证用 Web Module Canary。
- 双 CI 工作流、release candidate gate 与安装器供应链。

当前仍未完成：

- 代码签名与真实用户迁移验收。
- 稳定 Module API、NuGet SDK、模块模板与 qtool CLI。
- Rust 模块模板与端到端「新建 Tauri 模块」指南。
- WPF 宿主的正式退役（M4）。

后续路线以用户可见产品体验为优先：

A. 推进真实用户可见工具。
B. 改善模块信息展示与管理体验，但不开放冻结的模块事务边界。
C. 完善设置体验。
D. 继续现代化 UI，同时保持现有宿主、模块与 Web 安全边界。

Plan 013 与 B1/B2.1 已完成并冻结；除 P0/P1 外不再追加普通边缘加固。
不得自动把后续主线切回 B1、B2.1、Web 激活协议、安装器供应链扩建、
新的宿主更新状态机或 Update Helper。

每次开始工作先执行：

git branch --show-current
git status --short
git diff
git fetch origin --tags
git rev-parse HEAD
git rev-parse origin/toolbox
git rev-list --left-right --count origin/toolbox...HEAD
git log -15 --format=fuller

本地存在修改时必须先审查并在其基础上继续，不得丢弃。

提交沿用四段式风格，例如：

[+] add verified Tauri module delivery
[fix] restore Tauri host dependencies in release validation
[test] cover module lifecycle transitions in isolated profiles
[docs] align module development entry points

提交必须 UTF-8 无 BOM。推送到 origin/toolbox，不得 force push。
只有精确最终 HEAD 的 GitHub Actions conclusion=success 才能报告远程验证通过。
```

## 11. 维护本文档

每个重要阶段完成后，应更新：

- 已完成能力。
- 当前最高优先级。
- 下一阶段范围。
- 发布阻塞项。
- 明确延后的内容。
- 当前版本和验证门禁。

本文档是路线说明，不替代代码、测试、Release Notes 或实际 CI 结果。发布层面的事实以
[`CHANGELOG.md`](../CHANGELOG.md)、[`docs/releases/`](releases/) 与 GitHub Actions 实际结论为准。
