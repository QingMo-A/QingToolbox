# 官方模块目录和发布规则

模块更新和未来的官方模块下载共用这份可信发布信息。源码由 `modules` 分支维护，包存放于固定 GitHub Release 资产；工具箱仍只安装用户选择的模块。

## 入口和身份

固定入口：`https://raw.githubusercontent.com/QingMo-A/QingToolbox/modules/modules/index.json`。本地模块不能重定向官方源。

目录使用 schema 2、`sourceId: qingtoolbox-official-tauri` 和 `moduleProfile: tauri-process-v1`。旧 WPF schema 1 客户端必须拒绝这个目录，不得把新包当作 `.NET` 更新。

每条记录包含中英文名称和简介、图标、包身份与更新清单的相对路径，以及 `public` 或 `development` 可见性。路径必须在同一个模块目录内，不允许上级目录、编码路径、符号链接或任意外部 URL。

`module.json` 是源码和包身份；`qmod.json` 是打包生成的身份信封；`update.json` 是已发布版本记录。Cargo、UI 和模块身份版本保持一致。目录 schema、宿主 API、进程包格式彼此独立。

## 发布记录

每个版本包含 SemVer、渠道、API 和包格式、平台及架构、最低宿主版本、可选排他最高版本、UTC 发布时间、中英文说明，以及 `.qmod` 的文件名、URL、字节大小和 SHA256。不解析 Release 标题或标签来推断版本。

同一版本只能对应同一组包字节。禁止 `latest/download`，只接受固定官方 Release 地址。SHA256 不是数字签名，目录和 GitHub 发布账号仍是可信边界。

空 `releases` 表示未发布。源码版本提高不会自动进入发布记录。当前记录只包含重新下载并核验过的既有原生包，本地后续 UI 改动不冒充历史包的功能。

## 待接入的检查规则

检查器先筛选渠道、平台、架构、API 和宿主范围，再选择高于本地的最高兼容版本。不兼容的最新版本不能遮盖稍低但兼容的新版。没有兼容候选时报告宿主或 API 阻碍；获取失败不是“已经是最新版”。

正式环境启动后异步检查已安装模块，元数据缓存约 24 小时；手动检查绕过时限并使用条件请求。开发环境默认手动检查，缓存独立。过期缓存需标记，安装前重新确认。并发旧响应和旧本地版本的响应不能覆盖新状态。

当前 Rust/Tauri 模块在线检查与下载尚未实现。未来浏览页读取同一目录，过滤 `development` 条目，仅提供实际发布且兼容的包。

## 构建和发布顺序

1. 修改源码，更新模块、Cargo 和 UI 版本。
2. 验证目录、构建模块，生成 `.qmod` 与校验文件。
3. 经明确授权上传 GitHub Release 资产。
4. 下载已发布的包，核对大小、哈希、校验文件和包内身份。
5. 最后登记 `update.json`，验证后再提交和推送发布信息。

记录既有发布的命令只读取 GitHub、修改本地记录，不上传、不创建 Release、不提交、不推送：

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
