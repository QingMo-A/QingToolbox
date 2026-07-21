# QingToolbox 开发计划

> 本文是 QingToolbox 的当前开发路线、发布边界和项目交接说明。
>
> 文档创建时，`toolbox` 的已验证远程基线为 `aaae0475529c570c87c170e773feabd2b779834b`。继续开发前必须重新获取远程状态，不得假设该 SHA 始终是最新版本。

## 1. 项目定位

QingToolbox 是一个面向 Windows 的模块化桌面工具箱。

项目核心目标：

- 宿主长期稳定运行，不因单个模块故障退出。
- 模块发现、加载、启动授权、更新和删除具有明确的安全边界。
- Production、Development、ModuleTest 三种环境严格隔离。
- 发布过程可复现、可审计，并通过安装器、便携包和升级测试验证。
- 优先建立可靠基础设施，再逐步加入自动安装和宿主自更新。

当前开发分支：

- `toolbox`：宿主、安装器、开发工具、文档和发布基础设施。
- `modules`：官方模块索引、每模块更新描述及模块发布协议。

两个分支独立维护。宿主开发不得顺手修改 `modules`，模块协议变更也不得未经审查混入宿主提交。

## 2. 不可突破的架构边界

### 2.1 宿主与模块

- 宿主项目不得引用任何具体模块程序集。
- 模块扫描阶段只读取 `module.json` 和本地化资源。
- 扫描期间禁止加载、反射或执行模块 DLL。
- 模块 DLL 只能在用户明确操作或已验证的启动授权恢复阶段加载。
- 模块窗口是宿主子窗口；宿主退出时必须关闭窗口并卸载模块。
- 单个模块失败必须被隔离，不能终止宿主。

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

### 2.3 Git 与发布

- 未经用户明确授权，不创建 tag 或 GitHub Release。
- 不修改已经发布的 `v0.1.0-alpha` tag 和 Release。
- 不使用 `git commit --amend`、`git push --force`、`git reset --hard` 或 `--no-verify`。
- 不覆盖未提交的用户修改。
- 提交信息沿用四段式风格，并保证 UTF-8 无 BOM。
- 远程验证必须匹配精确最终 `HEAD` SHA；其他提交的成功 CI 不能代替当前提交。

## 3. 当前已经完成的主体能力

### 3.1 宿主与模块生命周期

已实现：

- 模块 Manifest 扫描、校验和展示。
- Load、Activate、Deactivate、Unload 生命周期。
- 独立模块窗口管理。
- 启动授权与模块载荷指纹验证。
- 模块执行前最终指纹复验，降低 TOCTOU 风险。
- 用户模块安全删除流程，并保留模块数据。
- 点击模块安装目录，在资源管理器中打开。
- 模块卡片响应式按钮布局和图标回退规则。

### 3.2 Windows 登录自启动

自启动主体架构已经封板，当前包含：

- 当前用户 Task Scheduler Logon Trigger 作为首选后端。
- HKCU Run 作为兼容降级后端。
- 当前用户、InteractiveToken、LeastPrivilege，不提权、不保存密码。
- Pipe-first 单实例启动，激活通信早于设置读取和 DI 重型初始化。
- 可见呈现先于模块扫描和启动模块恢复。
- Preferred 与根目录 Fallback 任务路径。
- 任务存在状态、健康状态、重复任务和外部禁用诊断。
- 注册事务、原始任务 XML 快照和回滚。
- Startup Test、Startup Health Journal 和阶段耗时。
- Explorer 重启后的通知区域恢复。
- 卸载时清理 QingToolbox owned Task 和 Run 项。
- 自启设置页中的刷新、修复、测试和诊断。

开机自启不再作为持续扩建的主线。后续只修复明确的发布阻塞错误。

### 3.3 模块更新检测与下载

已实现：

- 官方模块索引和每模块更新协议。
- SemVer 和宿主兼容性选择。
- ETag、Last-Modified、隔离缓存和过期状态。
- 手动下载 `.qmod`。
- 流式大小限制与长度验证。
- SHA256 校验。
- Verified Package 隔离存储。
- 下载取消、进度和状态展示。
- 不自动加载、执行或安装已下载包。

当前必须准确描述为：

> 检测模块更新，并下载、验证更新包。

不得描述为：

> 自动更新或安装模块。

### 3.4 发布基础设施

已实现：

- Debug 与 Release 构建。
- Module Load、Module Update、Module Package Download、Startup Reliability Smoke Test。
- 本地环境契约测试。
- 便携包构建和资产 Manifest。
- Inno Setup 安装器。
- 当前版本安装、卸载和用户数据保留 Roundtrip。
- 固定 AppId 与自有 marker 的安全旧目录发现、自定义目录原地升级，以及运行中 Shell 的成功后恢复。
- 安装器目录选择契约：合法显式 `/DIR` 优先于自动记录；无显式目录的有效记录冲突继续安全拒绝。
- 安装前通过 `WizardDirValue()` 复验向导最终目录，并只恢复该最终目标中原本运行的 Shell。
- Preview Release Candidate Gate。

Preview 2 当前已完成但仍需远程门禁确认的实现：

- 统一 `0.2.0-alpha` / `0.2.0.0` / `QingToolbox Preview 2` 发布元数据。
- 固定应用身份、原目录覆盖、同版本 Repair 和 SemVer 降级保护。
- 确定性 Host Payload Manifest、官方 Preview 1 宿主基线和精确废弃文件差集。
- 官方 Preview 1 安装器及 SHA256 sidecar 解析与校验。
- 原地升级、旧进程替换、用户状态与未知文件保留、单一卸载入口和快捷方式验证。
- Startup Recovery 托管、Startup Test 记录分离、并发扫描共享和 Dispatcher 外最终 Hash 复验。
- 精确最终 HEAD 的 `workflow_dispatch` 调度、run 身份核对和结论验证工具。
- 可记录 Automated Pass、Manual Pass、Blocked、Not Run 和 Failed 的 Preview 2 人工验收清单。

## 4. 当前发布目标：QingToolbox Preview 2

建议版本：

- 产品版本：`0.2.0-alpha`
- FileVersion：`0.2.0.0`
- 显示名称：`QingToolbox Preview 2`
- 主题：`Reliable Startup & Module Lifecycle / 可靠启动与模块生命周期`

Preview 2 的产品目标不是完成全部自动更新，而是证明：

> QingToolbox 能可靠启动、原地升级、安全管理模块，并保留用户状态。

## 5. 当前最高优先级：Preview 1 → Preview 2 原地升级门禁

这是下一阶段的唯一主线。在完成前，不进入 qmod 自动安装。

### 5.1 版本与发布说明

- 将宿主版本统一升级到 `0.2.0-alpha` / `0.2.0.0`。
- 安装器显示 `Preview 2`。
- 新增 `docs/releases/0.2.0-alpha.md`。
- 更新 README 和 CHANGELOG。
- 不修改 `docs/releases/0.1.0-alpha.md`。
- 不创建 tag 或 Release。

### 5.2 真实旧版资产

- 只从 GitHub 官方 `v0.1.0-alpha` Release 获取旧安装器。
- 必须验证官方 SHA256 sidecar。
- 不从第三方来源下载。
- 不提交旧安装器、旧 ZIP 或下载缓存。
- 可以提交由官方资产生成的旧版宿主文件基线 JSON。
- 旧 Release 缺少安装器或 Hash 时必须明确阻塞，不得猜测或伪造。

### 5.3 原地覆盖升级测试

新增自动化流程：

1. 将官方 `v0.1.0-alpha` 安装到隔离目录。
2. 创建设置、用户模块、模块数据、缓存、启动授权和未知安装目录文件 Sentinel。
3. 在同一目录运行当前 `0.2.0-alpha` 安装器。
4. 不先卸载旧版。
5. 验证旧进程关闭，新宿主文件替换。
6. 验证设置、模块、数据、缓存和启动授权保留。
7. 验证未知用户文件保留。
8. 验证废弃的旧版宿主自有文件被精确清理。
9. 验证只有一个安装目录和一个 Inno 卸载入口。
10. 再次运行同版本安装器，验证 Repair Install。
11. 卸载 Preview 2，验证安装目录删除而用户数据继续保留。

### 5.4 安装器兼容保证

- 固定 `AppId`，Preview 1 与 Preview 2 必须被识别为同一应用。
- 显式启用 `UsePreviousAppDir=yes`。
- 安装器不得通过先卸载旧版实现升级。
- 用户设置、模块、数据和缓存不得放入 `{app}`。
- 运行中的旧 Shell 可以关闭，但安装结束后只能运行新版。
- 不得产生重复开始菜单项、桌面快捷方式或卸载入口。

### 5.5 降级保护

从 `0.2.0-alpha` 开始的新安装器必须拒绝覆盖更高版本。

允许：

- `0.1.0-alpha → 0.2.0-alpha`
- `0.2.0-alpha → 0.2.0-alpha` 修复安装
- `0.2.0-alpha → 更高版本`

拒绝：

- `0.2.0-beta → 0.2.0-alpha`
- `0.2.0 → 0.2.0-alpha`
- `0.3.0-alpha → 0.2.0-alpha`

版本比较必须使用 SemVer 语义，不能使用字符串比较。

已发布的 `v0.1.0-alpha` 安装器无法追溯加入保护，这一点必须在文档中明确。

### 5.6 Host Payload Manifest 与废弃文件清理

生成 `host-payload.manifest.json`，记录宿主拥有的文件：

- `relativePath`
- `size`
- `sha256`
- `category`

Manifest 不包含用户数据、具体模块、缓存、日志、DevTools、PDB 或源码。

通过旧版官方基线与当前 Manifest 的差集生成精确废弃文件清单：

- 只删除旧官方基线中存在、而当前版本不再拥有的路径。
- 禁止通配符清空安装目录。
- 禁止删除未知文件、相似文件名、用户模块或用户数据。
- 所有路径必须通过绝对路径、穿越、ADS、UNC 和目录边界校验。

### 5.7 启动和扫描残余边界

随升级门禁一起收尾，不再扩展新能力：

- 同步 COM 回滚超时后，底层 Recovery 必须继续被托管和观察。
- Recovery 未完成时，不得开始第二个注册修改事务。
- Startup Test 的 `AttemptId` 与 `StartupTestId` 必须统一为一条逻辑记录。
- Startup Test 结果与正式注册健康状态分开展示。
- 启动扫描和手动刷新相撞时共享同一个底层扫描任务，不能以 `IsScanning` 直接返回并错报成功。
- 启动模块执行前的最终 Hash 复验不得占用 WPF Dispatcher。

## 6. Preview 2 发布门禁

### 6.1 自动化门禁

必须通过：

- `dotnet restore`
- Debug build
- Release build
- Startup Reliability Smoke Test
- Module Update Smoke Test
- Module Package Download Smoke Test
- Module Load Smoke Test
- Local Environment Contracts
- Installer Roundtrip
- Preview 1 → Preview 2 Upgrade Test
- Repair Install Test
- Downgrade Guard Test
- Host Payload Manifest Verification
- Obsolete Host File Cleanup Test
- Unknown Install File Preservation Test
- Portable ZIP Audit
- Installer Build
- Asset Manifest Verification
- Preview RC Gate

CI 和 RC Gate 必须使用精确最终 HEAD。没有旧版官方安装器时，不得声称完整 Upgrade Gate 或 RC Gate 已通过。

Preview 2 自动化升级门禁已经实现，并至少有一个历史提交通过了对应精确 SHA 的隔离远程验证；历史运行证据记录在
`docs/PREVIEW_2_ACCEPTANCE_CHECKLIST.md`，不能代表后续最终 HEAD。每个新候选必须在全部修改提交并推送后，使用
`scripts/verify-preview-final-head.ps1` 调度 `workflow_dispatch`，严格核对 event、branch、head SHA 和最终结论。
真实升级测试会启动 Production 模式宿主，而普通 Windows 账户的 Known Folder 无法通过环境变量可靠重定向，因此
本地安全门禁不得为测试强制关闭用户 Shell 或触及真实 Production 数据；该测试只在一次性 GitHub Actions 账户中执行。
所有未实际执行的人工升级、登录启动、Repair、卸载和代表性 Windows 环境项目必须保持 **Not Run** 或 **Blocked**。
项目所有者已明确决定暂时推迟剩余 Preview 2 人工发布验收，并授权进入后续开发。这不表示未执行的原地升级、登录重登、Repair、卸载或代表性环境项目通过；这些项目继续保持 `Not Run`。当前开发主线转为阶段 A：`.qmod` 离线结构验证与安全 Staging。

### 6.2 人工验收

发布前至少在测试机执行：

- 安装正式 v0.1.0-alpha。
- 创建真实测试设置和用户模块。
- 开启登录自启动。
- 使用 Preview 2 安装器原地覆盖。
- 验证设置、模块、数据和登录任务保留。
- 注销并重新登录，确认可见启动。
- 执行同版本修复安装。
- 卸载并确认 Task、Run 和卸载入口清理。
- 确认用户模块和模块数据保留。

未实际执行的人工项目不得报告为 Pass。

## 7. Preview 2 不包含的内容

以下内容明确延后：

- `.qmod` 自动解压和安装。
- 自动替换当前模块。
- Pending Update。
- 模块更新失败回滚。
- QingToolbox 宿主应用内一键自更新。
- 第三方模块更新源。
- 数字签名信任链。
- Windows Service。
- SYSTEM 或管理员任务。
- 稳定版 Module API。

## 8. Preview 2 之后的路线

### 8.1 阶段 A：qmod 离线结构验证与安全 Staging

目标：验证 `.qmod`，但仍不安装到正式模块目录。

要求包括：

- 将 qmod 视为 ZIP，但禁止加载或执行 DLL。
- 校验压缩包结构、必需文件和包内 Manifest。
- 防御 ZIP Bomb：总解压大小、单文件大小、Entry 数量和压缩比上限。
- 拒绝绝对路径、盘符、UNC、`..`、ADS、保留设备名、尾随点空格和大小写碰撞。
- 拒绝 Symlink、Reparse Point 和特殊文件。
- 严格比对模块 ID、版本、Module API、选中 Release、包大小和 SHA256。
- 仅解压到环境隔离的临时 Staging。
- 完整验证后原子重命名为已验证 Staging。
- 不写入 `UserModulesDirectory`。
- 不调用 Import、Load 或 Activate。

阶段 A 已达到 **Engineering Complete**：稳定包句柄和完整官方 Release 身份约束共享；本地与跨进程发布锁按物理 root/environment/module/version 隔离，独立 Worker 覆盖竞争、持锁崩溃恢复和取消，且锁等待不占解压容量。Incoming candidate 在原子移动前完成 metadata、Manifest、路径、长度与 Hash 的稳定句柄认证，`Directory.Move` 与 committed 标记位于同一线性化提交区；Caller 取消只能发生在提交前或提交后，不能观察到已 Move 但未 committed 的状态。移动后的取消、日志及锁 marker 诊断清理不能改写成功，未来复用仍执行同等严格认证。Staging/UserModules 配置根要求绝对、非卷根、物理不重叠且无 Reparse。Staging 仍不等于安装，不接触正式模块目录，也不加载或执行 DLL。阶段 B 已完成并冻结 B1 工程边界，Preview 2 剩余人工发布验收继续为 `Not Run`。安全边界见 `docs/QMOD_STAGING_SECURITY.md`。

### 8.2 阶段 B：0.3.0-alpha 模块事务更新

阶段 B 已开始。B1 的可恢复事务安装核心现为 **Engineering Complete — Frozen**：可信 Verified
Staging 重新认证、物理根/environment/module 跨进程锁、同卷 candidate、旧目录备份、原子
promotion、静态安装复验、失败回滚和崩溃恢复均由独立 smoke test 覆盖。除发现明确安全缺陷外，
不再扩展 B1 内核。核心仍只允许 `Development`/`ModuleTest` 执行；B2.1 已接入真实 Shell
生命周期适配器、启动恢复 Gate 和真实 TextTools 金丝雀，但 Production UI 与事务执行仍未开放。
Preview 2 未执行的人工验收继续为 `Not Run`。
完整边界见 `docs/MODULE_UPDATE_TRANSACTION.md`。

B1 的提交、内容和运行时边界已封闭：事务 Marker 与 backup 保留到 `Committed` schema 4 Journal
原子落盘；Journal temp 由同一文件句柄写入、Flush、复核，并通过 Journal Namespace Handle 加相对
叶名替换。旧 schema 3 只按严格旧字段布局和安全现场迁移，歧义现场保留且要求人工恢复。
installed→backup、candidate→installed、installed→failed-candidate 和 backup→installed 均通过
源目录句柄、目标父目录句柄和相对叶名完成 native rename，不存在 `Directory.Move` 降级。

树认证使用包含对象 File ID、长度和 SHA256 的双遍快照及有界 `SecureTreeLease`；Manifest 字节来自
同一认证文件句柄。Windows 不允许在子文件拒绝 delete sharing 时重命名其祖先目录，因此最终边界会在
持有全部句柄时再次校验，随后释放子句柄、保留 root 身份句柄完成原子 rename，并立即复核完整快照；
无法通过的后置状态不会被提交为可信版本。新版 Runtime Restore 的进程内副作用不依赖 Progress
Journal 写入成功才被观察，false、异常或写入失败都会先查询、静默并验证卸载 v2，再恢复 v1。
五个真实子进程窗口包含真正已写入 payload 的 candidate copy 中途崩溃。

当前已进入 B2.1：真实生命周期适配器和 Development/ModuleTest-only TextTools 金丝雀已接入；
宿主自更新、Production 安装、普通用户自动安装和 Production 模块替换仍未开始。

目标流程：

`Verified qmod → Staging → 关闭旧模块 → 卸载 → 原子替换 → 启动验证 → 失败回滚`

必须具备：

- 当前模块目录快照或可恢复备份。
- 原子目录替换。
- 运行中窗口和模块生命周期协调。
- 安装后 Manifest、指纹和启动验证。
- 失败自动回滚。
- 崩溃恢复和 Pending Transaction。
- 用户数据目录与模块程序目录严格分离。

第一个真实更新样本建议使用低风险的 `TextTools` 金丝雀版本，而不是 PowerGuard。

### 8.2.1 B2.1 lifecycle integration status

B1 remains **Engineering Complete — Frozen**. B2.1 now includes the real Shell lifecycle adapter,
an explicit startup recovery/execution gate, module-attributed RecoveryRequired blocking, and a
pinned real TextTools canary for Development/ModuleTest. Cold-start recovery defers runtime intent
until recovery has been inspected and discovery has completed, so recovery does not execute module
DLLs early.

Real TextTools WPF/BAML testing proved that closing the view and completing logical unload does not
reliably release a collectible ALC. ADR-001 therefore fixes the runtime boundary: UI-free service
modules use `InProcessCollectible + None`; real WPF view modules use one trusted out-of-process
ModuleHost per module; legacy in-process WPF remains compatible but cannot use a live transaction.
The capability is verified from the manifest, never guessed by creating a view.

This does not complete all of B2. Production transaction execution, a Production update button,
automatic qmod installation, and host self-update remain unavailable. Preview 2 manual acceptance
items that were not executed remain `Not Run`.

### 8.3 阶段 C：0.4.0-alpha Module API 与 SDK

- 稳定 Module API 边界。
- 独立 NuGet SDK。
- 模块模板与开发文档。
- API 兼容性策略。
- 权限声明和能力模型。
- 版本化测试宿主。

### 8.4 更后期：宿主应用内自更新

宿主自更新需要独立 Update Helper：

`检测宿主更新 → 下载并验证安装器 → 退出宿主 → 独立 Helper 启动安装器 → 验证升级 → 失败提示或恢复`

它不能由正在被替换的主进程直接完成，优先级低于模块事务更新。

## 9. 提交和验证工作流

每次 Codex 任务开始前：

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

- 本地存在修改时，先审查并接管，禁止覆盖。
- 本地 HEAD 领先远程时，先检查已有提交，不得 amend。
- 推送前执行 `git diff --check`。
- 只显式添加本轮源码、测试、脚本和文档。
- 不提交 `artifacts/`、`publish/`、`bin/`、`obj/`、安装器、ZIP、PDB、日志、真实注册表数据、真实 Task XML、设置或测试用户数据。
- 推送后确认 `HEAD == origin/toolbox`。
- 使用 `gh run list` 和 `gh run watch` 核对精确最终 SHA。

推荐提交风格：

```text
[+] add ...

[fix] ...

[test] ...

[docs] ...
```

纯文档提交可以使用四段 `[docs]`，但仍保持无 BOM、多段式和可审计语义。

## 10. 项目对话迁移提示词

下面的提示词用于在新的 ChatGPT/Codex 对话中恢复项目上下文。粘贴后，新环境应先读取本文件和远程仓库，再开始任何修改。

```text
你正在接手 QingMo-A/QingToolbox 项目。

请先读取并遵守仓库中的：

docs/DEVELOPMENT_PLAN.md

仓库：QingMo-A/QingToolbox
宿主开发分支：toolbox
模块协议分支：modules

当前项目是一个 .NET 10 / WPF 的 Windows 模块化工具箱。

最重要的架构约束：

1. 宿主不得引用具体模块程序集。
2. 模块扫描阶段只读取 module.json 和本地化资源，禁止加载 DLL。
3. Production、Development、ModuleTest 的设置、模块、缓存、Mutex 和 Pipe 必须隔离。
4. 单个模块失败不能终止宿主。
5. 未经我明确授权，不得创建 tag 或 GitHub Release。
6. 不得修改已有 v0.1.0-alpha tag 或 Release。
7. 不得 amend、force push、reset --hard 或使用 --no-verify。
8. 不得覆盖本地未提交修改。
9. modules 分支与 toolbox 分支独立，除非任务明确要求，否则不要修改 modules。
10. 所有远程验证必须匹配精确最终 HEAD SHA。

当前已经完成：

- 模块扫描、加载、激活、卸载和独立窗口。
- 启动授权与模块载荷指纹。
- 安全删除用户模块并保留模块数据。
- 打开模块安装目录。
- Task Scheduler 首选、Registry Run 降级的可靠登录自启动。
- Pipe-first 启动、可见呈现先于模块发现、启动健康与测试、Explorer 恢复和卸载清理。
- 官方模块更新检测。
- qmod 手动下载、大小限制和 SHA256 验证。
- 当前只下载并验证更新包，不自动解压、安装、Import、Load 或 Activate。
- 开发环境隔离、Smoke Tests、安装器和 Preview RC Gate。

项目所有者已明确推迟剩余 Preview 2 人工发布验收。未执行项目继续保持 `Not Run`，当前最高优先级是：

阶段 A：qmod 离线结构验证与安全 Staging；它不是 qmod 自动安装。

目标版本：

- Version：0.2.0-alpha
- AssemblyVersion：0.2.0.0
- FileVersion：0.2.0.0
- 显示名称：QingToolbox Preview 2
- 主题：Reliable Startup & Module Lifecycle

Preview 2 自动化能力已经完成：官方旧安装器和 sidecar 校验、真实原地覆盖、用户状态与未知文件保留、固定 AppId、
Repair、SemVer 降级保护、Host Payload Manifest、精确废弃文件清理、Startup Recovery 收尾以及隔离 GitHub Actions
门禁。每个候选仍必须对精确最终 HEAD 调度并通过远程验证。

当前必须完成的发布验收：

1. 使用人工验收清单记录真实 Windows 普通用户环境。
2. 人工验证 Preview 1 原地升级、登录启动、同版本 Repair 和卸载。
3. 记录安装器 SHA256、Windows 版本、测试人员和非隐私证据位置。
4. 保持未执行项目为 Not Run 或 Blocked，不以自动化结果替代人工结果。
5. 剩余验收被明确推迟而非通过；阶段 A 可以继续，但不得进入正式模块替换、回滚或 0.3.0-alpha 发布。

Preview 2 明确不包含：

- qmod 自动安装。
- 模块自动替换或回滚。
- 宿主应用内自更新。
- 第三方更新源。
- Windows Service、SYSTEM 或管理员自启。
- 稳定版 Module API。

完成 Preview 2 后的路线：

A. qmod ZIP 结构与安全 Staging 验证，仍不安装。
B. 0.3.0-alpha 模块事务更新、原子替换和失败回滚。
C. 0.4.0-alpha 稳定 Module API、NuGet SDK 和模板。
D. 更后期才实现独立 Update Helper 驱动的宿主自更新。

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

[+] add verified Preview 1 to Preview 2 upgrade validation

[fix] preserve owned payloads and startup state across installation

[test] cover in-place upgrades, repair installs and downgrade guards

[docs] define Preview 2 installation compatibility guarantees

提交必须 UTF-8 无 BOM。推送到 origin/toolbox，不得 force push。只有精确最终 HEAD 的 GitHub Actions conclusion=success 才能报告远程验证通过。
```

## 11. 维护本文档

每个重要阶段完成后，应更新：

- 已完成能力。
- 当前最高优先级。
- 下一阶段范围。
- 发布阻塞项。
- 明确延后的内容。
- 当前版本和验证门禁。

本文档是路线说明，不替代代码、测试、Release Notes 或实际 CI 结果。
