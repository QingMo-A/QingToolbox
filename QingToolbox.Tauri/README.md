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
│  ├─ src/protocol.rs           # JSON envelope/frame 校验
│  ├─ src/runtime.rs            # 新模块进程生命周期
│  ├─ src/web.rs                # 受控 qmod:// Web 资源和模块窗口
│  ├─ capabilities/default.json # 最小权限白名单
│  ├─ Cargo.toml
│  ├─ build.rs
│  └─ tauri.conf.json
├─ package.json
└─ vite.config.ts
```

`native-module-canary/` 是一个可复现的 Rust 进程模块样例，不是产品模块；
它用于验证 nonce-bound hello 和有界 shutdown。

## 本地运行

需要 Node.js、Rust stable、`cargo` 和 Tauri 所需的 Windows WebView2/构建工具。

```powershell
cd QingToolbox.Tauri
npm install
npm run typecheck
npm run build
npm run tauri dev
```

仓库根目录也提供了 `run-tauri-dev.bat`：它会先构建协议 canary，再启动
Tauri 开发宿主。

桌面构建完成后可用仓库脚本运行启动和单实例烟测：

```powershell
pwsh ../scripts/smoke-tauri-host.ps1 -ExecutablePath ./src-tauri/target/debug/qingtoolbox-tauri.exe
```

没有 Tauri 环境时仍可使用 `npm run dev` 在浏览器中预览；前端会显示
`浏览器预览` 状态，而不会伪装成 Rust 后端。

## 当前边界

- 已实现 `get_host_info`、`list_modules`、`hide_to_tray`、`start_module`、
  `stop_module` 和 `open_module` 窄 typed command；模块发现只读取清单，
  进程启动必须来自后端索引中的 manifest-owned `.exe`。
- 已接入最小托盘菜单（打开工具箱 / 退出）和关闭窗口转入托盘行为。
- 已实现版本化 envelope、单行 JSON frame 限制（1 MiB）、nonce-bound hello
  握手（每次启动使用系统 RNG nonce）、Rust 后台监督、单实例锁，以及模块根路径
  和资源路径校验。
- Web 模块使用后端注册的 `qmod://` 资源协议打开独立窗口；资源每次请求
  都重新做模块目录边界检查，Vue 不会得到绝对路径。
- 新宿主只接受 `runtimeType=Process`、`runtimeIsolation=OutOfProcess` 的
  模块清单；旧 DLL 清单会明确显示为无效，不会被尝试加载。
- `scripts/build-tauri-canary.ps1` 和 `scripts/smoke-tauri-canary.ps1` 提供
  一个真实子进程的协议验证闭环。
- 尚未接入全局快捷键、更新器、设置迁移和产品模块；这些会在新协议确认后
  逐项重写，不建立旧 ABI 兼容层。
- `bundle.active` 暂时关闭，避免在品牌图标和签名资产就绪前生成安装包。
- capability 当前只授予 Tauri core 默认能力；新增系统能力必须显式增加权限。
- 不修改、不加载现有 WPF 项目；旧模块迁移将在协议确定后单独进行。

## 版本策略

Tauri/npm 依赖使用固定版本，便于迁移期间复现构建。Rust 依赖锁定到当前
Tauri 2 系列版本，并提交 `src-tauri/Cargo.lock` 与 canary 的
`Cargo.lock`，避免构建时漂移。
