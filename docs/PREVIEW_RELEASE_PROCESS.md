# Preview Release Candidate Process

本文说明 QingToolbox `0.1.0-alpha` Preview 的最终候选构建与人工发布交接流程。
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

输出资产位于 `artifacts/`，包括便携 ZIP、当前用户安装包、各自的 `.sha256` 和
manifest。manifest 记录 `QingMo-A/QingToolbox`、完整 source commit、
`sourceTreeClean: true`、资产大小和 SHA256。构建资产被 Git 忽略，不应提交。

## 人工发布交接清单

- [ ] 候选脚本完整通过，终端摘要中的 clean 和 origin sync 均为 `True`。
- [ ] 对应 `toolbox` commit 的 Preview validation GitHub Actions 成功。
- [ ] 显式退出在模块窗口或模块运行时清理失败时仍能完成，且通知区图标被移除。
- [ ] 通知区初始化失败后可重试，Dispatcher 操作失败不会禁用后续菜单操作。
- [ ] 从通知区直接切换悬浮标时不闪现 Shell，并保留挂起的模块窗口。
- [ ] manifest 的 source commit 与准备发布的 commit 完全一致。
- [ ] ZIP、安装包、两个 SHA256 文件和 manifest 文件名与摘要一致。
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
- [ ] 核对 Release Notes、CHANGELOG、许可证和第三方声明。
- [ ] 人工决定是否创建 GitHub Release/tag，并单独上传已复核资产。
- [ ] 发布后从 GitHub 重新下载资产并再次核对 SHA256。

RC 脚本和 CI 都不会替发布者创建 Release/tag，不会推送代码，不会提交
`artifacts/`，也不会更改版本或签名状态。
