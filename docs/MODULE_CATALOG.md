# 官方模块目录和发布规则

模块更新和官方模块下载共用这份可信发布信息。源码、版本化 `.qmod` 和校验文件均由 `modules` 分支的模块目录维护；工具箱仍只安装用户选择的模块。新模块更新不创建 Tag / Release。

## 入口和身份

固定入口：`https://raw.githubusercontent.com/QingMo-A/QingToolbox/modules/modules/index.json`。本地模块不能重定向官方源。

目录使用 schema 2、`sourceId: qingtoolbox-official-tauri` 和 `moduleProfile: tauri-process-v1`。旧 WPF schema 1 客户端必须拒绝这个目录，不得把新包当作 `.NET` 更新。

每条记录包含中英文名称和简介、图标、包身份与更新清单的相对路径，以及 `public` 或 `development` 可见性。路径必须在同一个模块目录内，不允许上级目录、编码路径、符号链接或任意外部 URL。

`module.json` 是源码和包身份；`qmod.json` 是打包生成的身份信封；`update.json` 是已发布版本记录。Cargo、UI 和模块身份版本保持一致。目录 schema、宿主 API、进程包格式彼此独立。

## 发布记录

每个版本包含 SemVer、渠道、API 和包格式、平台及架构、最低宿主版本、可选排他最高版本、UTC 发布时间、中英文说明，以及 `.qmod` 的文件名、URL、字节大小和 SHA256。不解析 Release 标题或标签来推断版本。

同一版本只能对应同一组包字节。新包放在 `<模块目录>/packages/<模块ID>-<版本>-tauri.qmod`，更新 URL 为 `https://raw.githubusercontent.com/QingMo-A/QingToolbox/<40位Commit>/modules/<模块目录>/packages/<文件名>`。Commit 必须是已包含该包的本分支祖先；记录脚本会比较 Git blob、本地包、SHA256、大小、校验文件及包内身份。

禁止可变分支下载地址、`latest/download`、其他仓库或模块目录、外部镜像。既有固定官方 Release 地址继续兼容，但以后不再创建模块 Release。SHA256 不是数字签名，目录和 GitHub 账号仍是可信边界。

空 `releases` 表示未发布。源码版本提高不会自动进入发布记录。当前记录只包含重新下载并核验过的既有原生包，本地后续 UI 改动不冒充历史包的功能。

## 待接入的检查规则

检查器先筛选渠道、平台、架构、API 和宿主范围，再选择高于本地的最高兼容版本。不兼容的最新版本不能遮盖稍低但兼容的新版。没有兼容候选时报告宿主或 API 阻碍；获取失败不是“已经是最新版”。

正式环境启动后异步检查已安装模块，元数据缓存约 24 小时；手动检查绕过时限并使用条件请求。开发环境默认手动检查，缓存独立。过期缓存需标记，安装前重新确认。并发旧响应和旧本地版本的响应不能覆盖新状态。

支持新规则的 Rust/Tauri 工具箱从同一目录检查、下载和安装，过滤 `development` 条目，仅提供实际发布且兼容的包。旧工具箱的下载白名单只允许 Release，必须先更新一次本体；模块 API 保持 1。raw 连接失败可读取 GitHub Contents API 的同一 Commit、同一文件，仍验证完整包哈希，不切换为最新分支。

## 构建和发布顺序

1. 修改源码，更新模块、Cargo 和 UI 版本。
2. 验证目录、构建模块，生成 `.qmod` 与校验文件。
3. 用现有打包器输出到 `modules/<目录>/packages/`，提交源码、包和校验文件，取得完整 Commit SHA。
4. 用 `record-branch-module.ps1` 登记这一 Commit 中的包，核对大小、哈希、校验文件和包内身份。
5. 验证后提交 `update.json` 并正常推送 `modules` 分支。两次提交用于避免包的固定 Commit 地址循环依赖；不 amend、不 force push、不创建 Tag / Release。

```powershell
./scripts/package-module.ps1 -ModuleId qing.launcher -Smoke -OutputDirectory ./modules/Launcher/packages
# 提交模块源码、包和校验文件后，取得完整的包 Commit SHA。
./scripts/record-branch-module.ps1 -ModuleId qing.launcher -PackageCommit <40位Commit> `
  -NotesZh '此版本的实际更新说明' -NotesEn 'Actual update notes'
./scripts/verify-modules.ps1 -MetadataOnly
# 提交 update.json 后 git push origin modules。
```

仅补录历史 Release 时，可使用旧命令；它不上传、不创建 Release、不提交、不推送：

```powershell
./scripts/record-published-module.ps1 `
  -ModuleId qing.launcher `
  -Tag modules-launcher-v0.3.0 `
  -MinimumHostVersion 0.3.0-alpha `
  -NotesZh '此版本的实际更新说明' `
  -NotesEn 'Actual release notes for this version'
./scripts/verify-modules.ps1 -MetadataOnly
```

最低宿主版本是对该包的兼容声明，必须与验收结果一致，不能用本地源码推断历史包要求。现有原生发布要求至少 `0.3.0-alpha`。

## 双分支构建

宿主通过 worktree 或 `QINGTOOLBOX_MODULES_ROOT` 定位模块分支；模块 UI 通过 worktree 或 `QINGTOOLBOX_HOST_ROOT` 定位共享 UI。拒绝旧目录，不回退到重复的模块源码。

宿主 `src-tauri/resources/modules/` 只是生成的开发资源。CI 检出两个分支并显式设置路径；迁移两边的改动需协调提交，不能只推送一边后声称跨分支构建可用。
