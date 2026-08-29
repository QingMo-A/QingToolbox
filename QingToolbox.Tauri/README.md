# QingToolbox.Tauri

QingToolbox 新宿主的 Tauri 2 + Vue foundation。它与现有 WPF 宿主并行存在，
当前阶段用于验证 Rust 核心、进程模块协议和窗口/托盘基线；迁移完成前不会
自动替换现有生产启动入口。

## 目录

```text
QingToolbox.Tauri/
├─ src/                         # Vue 3 + Vite 前端
├─ src-tauri/
│  ├─ src/main.rs               # Windows 入口
│  ├─ src/lib.rs                # Tauri Builder 与 typed command
│  ├─ src/modules.rs            # 后端拥有的清单发现与索引
│  ├─ src/paths.rs              # 模块根和资源路径边界
│  ├─ src/settings.rs           # Rust 持有的共享设置与原子保存
│  ├─ src/importer.rs           # 有界、安全的 .qmod 导入与原子发布
│  ├─ src/protocol.rs           # JSON envelope/frame 校验
│  ├─ src/runtime.rs            # 新模块进程生命周期
│  ├─ src/web.rs                # 受控 qmod:// Web 资源和模块窗口
│  ├─ capabilities/default.json # 主窗口最小权限白名单
│  ├─ capabilities/module.json  # 模块窗口最小权限白名单
│  ├─ Cargo.toml
│  ├─ build.rs
│  └─ tauri.conf.json
├─ package.json
└─ vite.config.ts
```

`native-module-canary/` 是一个可复现的 Rust 进程模块样例，用于验证
nonce-bound hello、invoke 和有界 shutdown。`native-launcher/` 是第一个产品
迁移切片：它把启动台状态、桌面投影、排序和按 ID 启动放在独立 Rust 进程，
UI 通过模块窗口 IPC 调用，不接收绝对路径。

## 本地运行

需要 Node.js、Rust stable、`cargo` 和 Tauri 所需的 Windows WebView2/构建工具。

```powershell
cd QingToolbox.Tauri
npm install
npm run typecheck
npm run build
npm run tauri dev
```

仓库根目录也提供了 `run-tauri-dev.bat`：它会先构建协议 canary 和 Rust
Launcher 模块，再启动 Tauri 开发宿主。

桌面构建完成后可用仓库脚本运行启动、单实例和 Launcher 模块窗口烟测：

```powershell
pwsh ../scripts/verify-tauri.ps1 -BuildDesktop -SmokeDesktop
```

没有 Tauri 环境时仍可使用 `npm run dev` 在浏览器中预览；前端会显示
`浏览器预览` 状态，而不会伪装成 Rust 后端。

## 当前边界

- 已实现 `get_host_info`、`list_modules`、`hide_to_tray`、`start_module`、
  `stop_module`、`invoke_module`、`invoke_module_window`、
  `get_module_window_context`、`hide_module_window` 和 `open_module` 窄 typed command；模块发现只读取清单，
  进程启动必须来自后端索引中的 manifest-owned `.exe`。
- 已接入 Rust 持有的宿主设置：语言、外观、关闭行为、启动显示模式、登录启动偏好和
  最近模块 ID 通过固定 `%APPDATA%\\QingToolbox\\settings.json` 字段读写。
  更新使用同目录临时文件和备份回滚；未知旧字段会被保留，损坏文件会生成有限数量的
  `settings.corrupt-*.json` 备份。
- 主窗口可以通过原生文件选择器导入 `.qmod`。导入器只接受 ZIP 容器，限制条目、展开大小、
  单文件大小和压缩比，拒绝绝对/穿越/重复/加密/符号链接路径；包先在用户模块根下的随机
  临时目录中解压并通过新宿主 manifest 校验，随后以同卷 rename 原子发布。导入阶段不会
  启动模块，也不会覆盖同 ID 目录。
- 已接入最小托盘菜单（打开工具箱 / 退出）和关闭窗口转入托盘行为。
- 宿主通过固定版本的 global-shortcut 插件在 Rust setup 阶段注册持久化的
  `Ctrl+Alt+Space`（可在设置中修改，重启后生效）；回调只切换主窗口显示状态，
  页面不会直接持有全局键盘监听。
- 应用退出事件会先请求所有由本宿主创建的模块进程优雅关闭，超时后由
  Rust runtime supervisor 强制收拢，不会触碰用户自行启动的同名进程。
- 已实现版本化 envelope、单行 JSON frame 限制（1 MiB）、nonce-bound hello
  握手（每次启动使用系统 RNG nonce）、Rust 后台监督、单实例锁，以及模块根路径
  和资源路径校验。
- Web 模块使用后端注册的 `qmod://` 资源协议打开独立窗口；资源每次请求
  都重新做模块目录边界检查，Vue 不会得到绝对路径。
- 新宿主只接受 `runtimeType=Process`、`runtimeIsolation=OutOfProcess` 的
  模块清单；旧 DLL 清单会明确显示为无效，不会被尝试加载。
- Qing Launcher 的迁移样板已随开发宿主资源构建：它支持 Desktop `.exe`、
  `.lnk`、`.url` 投影、custom/alphabetical/desktop 三种视图、稳定 ID 启动和
  原子 JSON 状态保存。模块窗口 IPC 权限匹配 `module-*` 标签，Rust 会再次
  校验 manifest operations。
- `scripts/build-tauri-canary.ps1` 和 `scripts/smoke-tauri-canary.ps1` 提供
  一个真实子进程的 hello、invoke、shutdown 协议验证闭环。
- 尚未接入 Everything、Explorer 拖入、更新器和完整设置迁移；这些会
  在新协议确认后逐项重写，不建立旧 ABI 兼容层。
- `bundle.active` 暂时关闭，避免在品牌图标和签名资产就绪前生成安装包。
- capability 当前只授予 Tauri core 默认能力和显式的 dialog 文件选择器权限；新增系统能力必须
  显式增加权限。Vue 仍不能直接读写文件系统或启动进程。
- 新宿主首次启动默认显示主窗口；之后读取共享设置中的启动显示模式。开发/烟测可用
  `QING_TAURI_STARTUP_PRESENTATION=main|minimized|tray` 临时覆盖，而不会写入用户设置。
- 主窗口关闭行为由设置控制：`tray` 隐藏到托盘，`exit` 走 Tauri 正常退出清理，`ask`
  使用原生确认对话框让用户选择。本体退出时只收拢本宿主创建的模块进程。
- 不修改、不加载现有 WPF 项目；旧模块迁移将在协议确定后单独进行。

## 版本策略

Tauri/npm 依赖使用固定版本，便于迁移期间复现构建。Rust 依赖锁定到当前
Tauri 2 系列版本，并提交宿主、canary 与 Qing Launcher 各自的
`Cargo.lock`，避免构建时漂移。
