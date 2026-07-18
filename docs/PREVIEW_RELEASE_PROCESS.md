# Preview Release Candidate Process

本文说明 QingToolbox `0.2.0-alpha` Preview 2 的最终候选构建与人工发布交接流程。

Preview 2 RC 必须使用经过官方 SHA256 sidecar 验证的 `v0.1.0-alpha` 安装器，
完成原地覆盖、同版本修复、降级拒绝、用户状态保留和单一卸载身份验证。
该流程只验证候选产物，不创建 GitHub Release、tag，也不上传正式发布资产。

## 前置条件

- Windows、PowerShell、.NET 10 SDK 和 Inno Setup 6。
- 当前位于 `toolbox` 分支且不是 detached HEAD。
- 工作区（包括未跟踪文件）完全干净，并通过 `git diff --check`。
- 当前 `HEAD` 与 `origin/toolbox` 完全一致。
- 可访问 NuGet、GitHub 和固定版本的 Inno Setup 中文翻译来源。

## 构建最终候选

```powershell
./scripts/build-preview-release-candidate.ps1
```

如果 Inno Setup 不在标准位置：

```powershell
./scripts/build-preview-release-candidate.ps1 `
  -IsccPath "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
```

脚本依次执行 clean-source/origin 门禁、Release 构建、开发模块部署、Smoke Test、
便携包发布、隔离的 Inno 依赖准备、安装包构建、隔离用户目录中的安装—卸载
Roundtrip、manifest v2 生成和资产复核。结束前会再次确认 HEAD 未变化、工作区干净且
仍与 `origin/toolbox` 同步。

RC 总控会对每个原生命令和 PowerShell 子阶段立即检查 PowerShell 成功状态与退出码。
构建开始前会按集中式 metadata 的精确路径清除旧候选资产；任意阶段失败都会立即停止，
不会生成 Manifest 或最终通过摘要。这项自动门禁不代表尚未完成的人工测试已经通过。

输出资产位于 `artifacts/`，包括便携 ZIP、当前用户安装包、各自的 `.sha256` 和
manifest。manifest 记录 `QingMo-A/QingToolbox`、完整 source commit、
`sourceTreeClean: true`、资产大小和 SHA256。构建资产被 Git 忽略，不应提交。

## 人工发布交接清单

### 已记录的人工验收

- **Pass — tested manually by project owner：**安装程序已由项目所有者在真实 Windows 环境中完成基本人工测试；安装和安装后的基本运行未发现阻塞问题。
- 此结果不代表所有 Windows 版本、所有 DPI 与多显示器组合、Explorer 重启、注销与关机路径，或所有机器上的 SmartScreen 均已通过。
- **Not tested manually：**便携版程序真实启动。

- [ ] 候选脚本完整通过，终端摘要中的 clean 和 origin sync 均为 `True`。
- [ ] 对应 `toolbox` commit 的 Preview validation GitHub Actions 成功。
- [ ] 显式退出在模块窗口或模块运行时清理失败时仍能完成，且通知区图标被移除。
- [ ] 通知区初始化失败后可重试，Dispatcher 操作失败不会禁用后续菜单操作。
- [ ] 从通知区直接切换悬浮标时不闪现 Shell，并保留挂起的模块窗口。
- [ ] manifest 的 source commit 与准备发布的 commit 完全一致。
- [ ] ZIP、安装包、两个 SHA256 文件和 manifest 文件名与摘要一致。
- [ ] ZIP 和安装器均为 host-only，不包含 TextTools、PowerGuard 或其他具体模块；模块只能通过独立 `.qmod` 导入。
- [ ] 在真实 Windows 用户会话中启动 Shell，确认首页和模块页显示正常。
- [ ] 验证首次关闭选择、持久化关闭行为、托盘左键恢复和本地化右键菜单。
- [ ] 验证正常退出后通知区域图标和 QingToolbox.Shell.exe 均不残留。
- [ ] 确认便携 ZIP 和安装器均不包含开发诊断用 `stop-qingtoolbox.bat`。
- [ ] 确认 Refresh Modules 不会自动加载模块 DLL。
- [ ] 手动验证中文和英文 Shell、本地化模块界面及安装向导。
- [ ] 手动验证无管理员权限安装、开始菜单入口和可选桌面快捷方式。
- [ ] 手动验证卸载入口、运行中阻止覆盖，以及用户模块和设置保留策略。
- [ ] 手动验证便携 ZIP 在已安装 .NET 10 Desktop Runtime 的机器上启动。
- [ ] 核对 `.qmod` 未签名、程序未签名和 SmartScreen 风险说明仍然醒目。
- [ ] 核对 Module API 标记为 Experimental，且未宣称已发布稳定 NuGet SDK 或兼容承诺。
- [ ] 核对 Release Notes、CHANGELOG、许可证和第三方声明。
- [ ] 人工决定是否创建 GitHub Release/tag，并单独上传已复核资产。
- [ ] 发布后从 GitHub 重新下载资产并再次核对 SHA256。

RC 脚本和 CI 都不会替发布者创建 Release/tag，不会推送代码，不会提交
`artifacts/`，也不会更改版本或签名状态。

真实 Preview 1 升级测试会启动 Production 模式的旧宿主。Windows Known Folder 不能通过修改
`APPDATA`/`LOCALAPPDATA` 环境变量安全重定向，因此该自动化只允许在一次性 GitHub Actions
Windows 账户中运行。普通本机 RC 应将这一阶段报告为 **Blocked**，不得强制关闭用户 Shell 或
以真实用户配置换取测试通过。

安装—卸载 Roundtrip 也会写入产品固定 AppId 对应的当前用户卸载注册。为避免覆盖开发者机器上
真实安装的卸载信息，该脚本同样只允许在一次性 GitHub Actions Windows 账户中运行；本地调用会在
启动安装器之前安全失败。自定义目录的 Preview 1→Preview 2 自动发现、运行中进程替换和 Repair
验证由远程隔离门禁负责，Preview 2 与 Repair 均不得依赖测试代码传入 `/DIR`。

目录选择优先级为：合法的显式 `/DIR`、可信自动发现目录、默认用户安装目录。显式目录允许是尚未
创建的新目录，但仍拒绝空值、相对路径、UNC、磁盘根和受保护的 Windows、System、用户配置及
临时目录根；显式值无效时不得回退。没有 `/DIR` 时，不同的有效自动记录继续 fail closed。隔离
升级门禁同时验证冲突拒绝、显式覆盖、危险显式目录拒绝，以及基于最终目标路径的 Shell 替换。

全部候选修改提交并推送后，使用以下命令对精确最终 HEAD 调度并验证远程门禁：

```powershell
./scripts/verify-preview-final-head.ps1
```

人工证据记录在 [`PREVIEW_2_ACCEPTANCE_CHECKLIST.md`](PREVIEW_2_ACCEPTANCE_CHECKLIST.md)。
远程自动化成功不能替代其中的人工登录、升级、Repair、卸载和代表性环境验收。
