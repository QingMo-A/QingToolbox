# Tauri 宿主发布流程

本文说明 Rust/Tauri 2/Vue 3 宿主（`QingToolbox.Tauri`）的候选门禁与公开发布交接。

> **旧的 WPF 发布链不适用于 Tauri 宿主。** `docs/PREVIEW_RELEASE_PROCESS.md`、`publish-preview-release.bat`、`scripts/publish-preview-release.ps1`、`scripts/build-preview-release-candidate.ps1` 与 `.github/workflows/preview-release-validation.yml` 属于已冻结的 WPF 发行线：它们只 `dotnet build` `QingToolbox.Shell`，并从根 `Directory.Build.props` 读取版本号（该文件停留在 WPF 线的 `0.2.9-alpha`），升级验证基线也硬编码为 `v0.2.8-alpha`。对 Tauri 宿主使用它们，只会得到 WPF 宿主和错误的版本号。

## 版本号来源

Tauri 宿主的版本号只有一处权威来源：

| 文件 | 字段 | 说明 |
| --- | --- | --- |
| `QingToolbox.Tauri/src-tauri/Cargo.toml` | `version` | **权威来源**。`build-tauri-portable.ps1` 与 `build-tauri-installer.ps1` 都读它 |

以下派生位置必须同步更新，否则 `cargo test --locked` / `cargo clippy --locked` 与 npm 会不一致：

- `QingToolbox.Tauri/src-tauri/Cargo.lock`（`qingtoolbox-tauri` 条目）
- `QingToolbox.Tauri/package.json` 与 `QingToolbox.Tauri/package-lock.json`
- `QingToolbox.Tauri/src-tauri/tauri.conf.json`

`QingToolbox.WebUI/package.json` 是独立序列，不随宿主版本变动。根 `Directory.Build.props` 属于 WPF 线，**不要**为 Tauri 版本改动它。

## 候选门禁（仅能在 CI 通过）

```powershell
./scripts/build-tauri-release-candidate.ps1
```

七个阶段：验证宿主与全部官方模块 → 打包模块 → 本地环境契约 → 构建 production 宿主 → 构建并冒烟安装器 → 校验候选资产 → 复核最终源码状态。开始时与结束前都会确认位于 `toolbox` 分支且工作区干净，并比对两次 HEAD 是否一致。

该门禁**只能在 CI 中通过**：它调用的 `build-tauri-installer.ps1 -SkipBuild -Smoke` 会执行 `scripts/smoke-tauri-installer.ps1`，而该脚本在 `GITHUB_ACTIONS -ne 'true'` 时直接抛错——安装/卸载冒烟会写入固定产品 AppId 的当前用户卸载记录，只能跑在一次性 Windows 配置里。本地运行会停在这一步，这是设计如此，**不要伪造 `GITHUB_ACTIONS`**，也不要因此认为门禁"失败"。

配套工作流是 `.github/workflows/tauri-validation.yml`。推送到 `toolbox` 且触及 `QingToolbox.Tauri/**`、`protocol/**`、`scripts/build-tauri-*.ps1`、`scripts/package-tauri-*.ps1`、`scripts/smoke-tauri-*.ps1`、`scripts/verify-tauri.ps1`、`scripts/prepare-inno-setup.ps1`、`run-latest.bat`、`installer/QingToolbox.Tauri.iss`、`installer/dependencies.psd1` 等路径时自动触发，也可 `workflow_dispatch` 手动触发。

## 本地构建可发布安装包

发布用的安装包由本地构建，因为门禁里的安装器冒烟只在 CI 运行。实测记录：`v0.3.0-alpha` 的公开资产与本机构建产物逐字节相同（SHA256 `22AFF4C714D7D6E228F3DCAA1F034C3BF58871CA225AC9F80B1A0CEC02C55CF2`，7,289,828 字节），说明这条本地构建路径就是发布来源。

```powershell
# 1. 在干净 HEAD 上构建 production 宿主
#    manifest 会记录 sourceCommit，并标记 sourceDirty=false
./scripts/build-tauri-production.ps1 -SkipModuleBuild -Smoke

# 2. 编译 host-only 安装器
#    不要加 -Smoke，该冒烟仅限 CI
./scripts/build-tauri-installer.ps1 -SkipBuild
```

产物：

- `artifacts/tauri-production/QingToolbox/` —— 宿主载荷与 `portable-manifest.json`
- `artifacts/tauri-installer/output/QingToolbox-<version>-win-x64-tauri-setup.exe` 及同名 `.sha256`

`build-tauri-installer.ps1` 会校验 `portable-manifest.json` 的 `sourceCommit` 等于当前 `HEAD`，因此**必须先提交版本号改动、再构建**，否则会报 "The production directory is stale"。Inno Setup 6 不在标准位置时用 `-IsccPath` 指定。

## 更新下载与覆盖安装

- 安装包下载仅接受官方 Release 链接，以及 GitHub 官方 CDN 的 HTTPS 跳转。CDN 签名查询参数原样保留；每次跳转重新检查域名，最终仍需匹配文件大小与 SHA-256 sidecar。
- 应用内安装要求安装登记、当前运行版本、production manifest 与 exe 哈希一致。安装登记未完成时，应重新运行安装器修复，不要手工修改登记或跳过校验。
- 安装器自动退出握手独立于上述载荷校验：已登记的 Tauri 安装路径、程序与卸载身份必须吻合，并验证本次进程生成的随机 token；版本登记过旧、manifest 的 `sourceDirty=true` 不再阻止正常退出。未登记 portable、开发环境及旧 WPF 不满足条件。
- 安装器先按目标安装路径定位正在运行的宿主，主动发送退出请求，再有限等待该进程退出；`QINGHOSTPID` 只是提示，不会盲目等待或关闭其他安装/开发实例。不强杀进程。完全没有握手通道的历史版本首次升级仍需手动退出一次。
- 回归验证：`cargo test --lib host_update::tests`；实际网络下载可单独运行 `cargo test --lib live_official_installer_download_smoke -- --ignored --nocapture`，只下载和校验，不执行安装包。完整安装器冒烟仍仅限一次性 CI 用户配置。

## 发布交接（手动）

Tauri 线没有自动发布脚本，tag 与 Release 由发布者手动创建：

1. 确认 `tauri-validation.yml` 在**精确的最终 HEAD** 上跑绿。
2. 确认 `portable-manifest.json` 的 `sourceDirty` 为 `false`，且 `sourceCommit` 与要打 tag 的提交一致。
3. 校验安装包与其同名 `.sha256` 一致。
4. 创建 tag 与 pre-release，并附上安装包与同名 `.sha256` 两个资产：

```bash
gh release create v<version> --prerelease \
  --title "QingToolbox <version> (Tauri)" \
  --notes-file docs/releases/<version>.md \
  artifacts/tauri-installer/output/QingToolbox-<version>-win-x64-tauri-setup.exe \
  artifacts/tauri-installer/output/QingToolbox-<version>-win-x64-tauri-setup.exe.sha256
```

5. 发布后从 GitHub 重新下载两个资产并再次核对 SHA256。

## 发布边界

- 安装器是 **host-only**：`build-tauri-portable.ps1` 与 `build-tauri-installer.ps1` 一旦在宿主包里发现 `resources/modules` 就直接失败。官方模块只能通过独立 `.qmod` 交付。`QingToolbox.Tauri/src-tauri/resources/modules/` 是仓库内的构建暂存目录，仅开发宿主只读扫描，不属于生产宿主安装器。
- 构建产物位于 `artifacts/` 且被 Git 忽略，不要提交。
- 任何脚本都不替发布者创建 tag 或 Release，也不改动版本号或签名状态。
- 仍未签名；真实用户升级、修复安装、卸载与 SmartScreen 验收需单独记录在人工验收清单中。
