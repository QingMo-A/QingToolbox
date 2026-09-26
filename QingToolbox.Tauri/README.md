# QingToolbox.Tauri

QingToolbox 新宿主的 Tauri 2 + Vue 实现。Rust Core 是新开发和生产候选的默认
宿主；现有 WPF 宿主仅作为尚未切换安装器的历史路径保留。新宿主不加载旧 DLL，
模块通过版本化进程协议运行。

## 目录

```text
QingToolbox.Tauri/
├─ src/                         # 仅保留早期壳的兼容源码（不再作为正式前端入口）
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

正式主界面位于仓库根的 `QingToolbox.WebUI/`。Tauri 的 `beforeBuildCommand`
和 `frontendDist` 已直接指向该 Vue 3 工作区，包含首页、模块管理、运行中、日志、
设置、命令面板和完整设计系统。`QingToolbox.WebUI/src/bridge/transport/TauriTransport.ts`
把这些页面接到 Rust/Tauri typed commands；浏览器预览和旧 WebView 桥仍使用各自的
transport，不会被这次接入破坏。
```

`native-module-canary/` 是一个可复现的 Rust 进程模块样例，用于验证
nonce-bound hello、invoke 和有界 shutdown。`native-launcher/`、`native-pdf/`、
`native-transfer/`、`native-texttools/`、`native-windowtopmost/`、`native-powerguard/` 和 `native-screenpin/` 是当前产品迁移切片：各自的状态、系统能力、文件边界和重型运行时
都在独立 Rust 进程中，Vue 只通过模块窗口 IPC 调用，不接收可直接执行的路径。

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

桌面构建完成后可用仓库脚本运行启动、单实例以及全部已迁移模块的窗口烟测：

```powershell
pwsh ../scripts/verify-tauri.ps1 -BuildDesktop -SmokeDesktop -SmokeEverything
```

需要交付可导入的单模块包时，使用 `pwsh ../scripts/package-tauri-module.ps1
-ModuleId <id> -Smoke`；需要一次生成全部官方模块包时使用
`pwsh ../scripts/package-tauri-modules.ps1 -SkipBuild -Smoke` 或根目录的
`run-tauri-module-packages.bat`。包和 SHA256 sidecar 位于
`artifacts/tauri-modules/`，其根级 `qmod.json` 与 `module.json` 绑定，导入器会在
发布前重新校验身份、版本和目录边界。

要生成便于手工体验的 Release portable 目录（不生成安装器），运行：

```powershell
pwsh ../scripts/build-tauri-portable.ps1 -Smoke -Zip
```

脚本只把 Tauri 宿主 executable、逐文件 SHA256 manifest 和宿主许可文件放到
`artifacts/tauri-portable/`。官方模块均由独立 `.qmod` 交付，不再内置于宿主。
需要生产候选目录时使用 `pwsh ../scripts/build-tauri-production.ps1`
（输出到 `artifacts/tauri-production/`）；可安装候选使用
`pwsh ../scripts/build-tauri-installer.ps1 -Smoke`，输出到
`artifacts/tauri-installer/output/`，沿用 QingToolbox 产品 AppId。升级旧版 Tauri
安装时，安装器会先把 0.3.0-alpha 的内置模块迁入用户模块目录；已有的有效
Tauri 模块保持原样，同 ID 的旧 WPF 模块先备份再替换。
`run-tauri-installer.bat` 是同一流程的便捷入口。

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
- 字体选择也由 Rust 设置层持有：内置字体目录只暴露固定的安全系统字体，用户导入的
  `.ttf`、`.otf`、`.ttc` 会复制到私有 `%LOCALAPPDATA%\\QingToolbox\\Fonts\\Imported`
  目录并以 SHA-256 ID 管理。WebView 只能通过受控 `qfont://` 资源读取已校验字体，不能
  提交任意文件路径；缺失或损坏的导入字体会安全回退到默认字体。
- 主窗口可以通过原生文件选择器导入 `.qmod`。导入器只接受 ZIP 容器，限制条目、展开大小、
  单文件大小和压缩比，拒绝绝对/穿越/重复/加密/符号链接路径；包先在用户模块根下的随机
  临时目录中解压并通过新宿主 manifest 校验，随后以同卷 rename 原子发布。导入阶段不会
  启动模块，也不会覆盖同 ID 目录。
- 已接入最小托盘菜单（打开工具箱 / 退出）和关闭窗口转入托盘行为。
- 宿主通过固定版本的 global-shortcut 插件在 Rust setup 阶段注册持久化的
  `Ctrl+Alt+Space`（可在设置中修改，重启后生效）；回调只切换主窗口显示状态，
  页面不会直接持有全局键盘监听。
- 登录启动由固定版本的 autostart 插件在 Rust 宿主侧同步。发布版启动时会校正
  当前用户的启动项；开发/烟测默认不改注册表，只有显式设置
  `QING_TAURI_ENABLE_AUTOSTART_SYNC=1` 才会启用该副作用；烟测统一设置
  `QING_TAURI_DISABLE_AUTOSTART_SYNC=1`，即使指向 Release 可执行文件也不会改动登录项。
  设置页会通过同一插件读取实际注册状态并显示 Healthy/Degraded/Unavailable；“修复启动项”
  也会复用同一条 Rust 校正路径，不会把注册表位置或可执行文件路径暴露给 Vue；被禁用的
  开发/烟测环境会明确显示 Disabled 并拒绝副作用。
- 应用退出事件会先请求所有由本宿主创建的模块进程优雅关闭，超时后由
  Rust runtime supervisor 强制收拢，不会触碰用户自行启动的同名进程。
- 已实现版本化 envelope、单行 JSON frame 限制（1 MiB）、nonce-bound hello
  握手（每次启动使用系统 RNG nonce）、Rust 后台监督、单实例锁，以及模块根路径
  和资源路径校验。
- 宿主更新快照、官方 Release 检查和安装器下载由 Rust 提供：Release 构建通过系统
  WinHTTP 请求固定的 QingMo-A/QingToolbox API，严格限制 SemVer 通道、响应大小和
  Tauri 专用 `QingToolbox-{version}-win-x64-tauri-setup.exe`/SHA256 资产身份；下载只允许受控的 GitHub 重定向，写入用户数据下的
  `Updates/Host` 缓存并在原子改名之前校验 sidecar、大小和 SHA256。Vue 不会接收
  下载 URL 或本地路径，只轮询字节进度和 ReadyToInstall 状态。交接前 Rust 会同时
  校验固定 migration AppId 的 HKCU 卸载记录、Tauri 专用 product marker、当前
  `QingToolbox.exe` 及旁置 production manifest；Debug、portable、旧 WPF 和缺失
  marker 的旧候选均不支持交接。交接使用 Inno Setup 静默参数，先收拢本宿主创建的
  模块再退出宿主；启动失败时宿主保持运行。Debug/烟测默认关闭联网检查，可显式
  设置 `QING_TAURI_ENABLE_UPDATE_CHECK=1` 进行集成验证。
- Web 模块使用后端注册的 `qmod://` 资源协议打开独立窗口；资源每次请求
  都重新做模块目录边界检查，Vue 不会得到绝对路径。
- 新宿主只接受 `runtimeType=Process`、`runtimeIsolation=OutOfProcess` 的
  模块清单；旧 DLL 清单会明确显示为无效，不会被尝试加载。
- Qing Launcher 的迁移样板已随开发宿主资源构建：它支持 Desktop `.exe`、
  `.lnk`、`.url` 投影、custom/alphabetical/desktop 三种视图、稳定 ID 启动和
  原子 JSON 状态保存。搜索框支持 `/e`、`/e:f`、`/e:d` 的内置 Everything
  模式；固定版本运行时和许可证随模块资源交付，结果只通过后端签发的
  `resultId` 打开或复制路径。模块窗口 IPC 权限匹配 `module-*` 标签，Rust
  会再次校验 manifest operations。
- Qing PDF 已迁移为 Rust + qpdf 进程模块：拼接、均分、页面提取和旋转均在本机
  通过固定版本的 qpdf 完成，输入/输出路径在模块内重新 canonicalize，结果用
  不透明 `resultId` 打开；qpdf 的许可证、通知和哈希清单随模块资源交付。
- QingTransfer 已迁移为 Rust 进程模块：DNS-SD 只负责发现，解析出的端点还必须
  完成 nonce-bound TCP 探测才会进入设备列表；连接和文件传输使用有界 JSON 控制帧、
  SHA-256 校验、临时文件和原子改名。接收目录偏好由模块数据目录保存，关闭时只
  收拢本模块创建的监听/发现资源。
- Text Tools 已迁移为 Rust 进程模块：JSON、Base64、URL 编码和大小写等转换均
  在 2 MiB 输入/4 MiB 输出边界内执行；剪贴板写入由模块后端完成，Vue 不直接
  获得系统剪贴板能力。
- Window Topmost 已迁移为 Rust 进程模块：后端枚举当前桌面的普通可见窗口，
  仅向 Vue 发放短期 `windowId`；真实 HWND 保留在进程内并在每次置顶操作前重新
  校验，不提供通用 Win32 调用桥。
- PowerGuard 已迁移为 Rust 进程模块：网络探测、状态机、倒计时、设置原子保存
  和关机动作均由后端拥有；默认关闭守护，立即关机必须提交后端确认令牌。
- Screen Pin 已迁移为 Rust 进程模块：后端读取有界虚拟屏幕区域并编码为 PNG，
  Vue 只接收受协议帧限制的会话内截图和不透明 pin ID；关闭模块时截图立即释放。
- `scripts/build-tauri-canary.ps1` 和 `scripts/smoke-tauri-canary.ps1` 提供
  一个真实子进程的 hello、invoke、shutdown 协议验证闭环。
- Launcher 已接入受控 Explorer 拖入和模块级全局快捷键：宿主在原生窗口边界
  canonicalize 并限制 `.exe`、`.lnk`、`.url`，只向声明了 `launcher.externalDrop`
  的模块发送事件；快捷键由 Rust global-shortcut 插件注册，页面只能录入模块自己
  的组合键。更新交接已接入；签名发布和完整设置迁移仍是后续工作。Everything、qpdf 和局域网
  发现都是模块内受控 runtime/资源，不建立旧 ABI 兼容层。
- `bundle.active` 暂时关闭，避免 Tauri bundler 自动下载 NSIS 工具链；安装器使用
  仓库现有的固定 Inno Setup 供应链，并沿用 QingToolbox 的产品 AppId。
- capability 当前只授予 Tauri core 默认能力和主窗口显式的 dialog 文件选择器权限；
  模块窗口不继承文件选择器、文件系统或进程权限。新增系统能力必须显式增加权限，
  Vue 仍不能直接读写文件系统或启动进程。
- 新宿主首次启动默认显示主窗口；之后读取共享设置中的启动显示模式。开发/烟测可用
  `QING_TAURI_STARTUP_PRESENTATION=main|minimized|tray` 临时覆盖，而不会写入用户设置。
- 主窗口关闭行为由设置控制：`tray` 隐藏到托盘，`exit` 走 Tauri 正常退出清理，`ask`
  使用原生确认对话框让用户选择。本体退出时只收拢本宿主创建的模块进程。
- 完整 Vue 模块管理页的“打开目录”和“删除用户模块”已接入 Rust；删除前会关闭
  模块窗口、停止对应进程并再次校验用户模块根目录边界。正式安装包不含内置模块。
- 模块详情页的“随工具箱启动”授权已迁移到 Rust 设置，授权列表有界、按模块 ID
  去重并在宿主启动时只启动已授权且已发现的模块。
- 主窗口日志页已通过 `get_session_logs` 读取 Rust 宿主的有界内存会话日志；模块扫描、
  导入、覆盖更新、启停、目录操作、设置变更和启动授权等事件不会把路径或句柄暴露给 Vue。
- Rust 运行时监督器会在模块启动、退出或失败状态变化时向主窗口发送有界事件；Vue
  对事件刷新做合并处理，并重新读取宿主和模块快照，后台进程退出后无需手动刷新。
- 不加载旧 WPF DLL 模块；从 0.3.0-alpha 升级时，安装器会备份同 ID 的旧 WPF
  用户模块并迁入对应的 Tauri 模块。直接从 WPF 宿主升级时需单独导入新版 `.qmod`。

## 版本策略

Tauri/npm 依赖使用固定版本，便于迁移期间复现构建。Rust 依赖锁定到当前
Tauri 2 系列版本，并提交宿主、canary 与 Qing Launcher 各自的
`Cargo.lock`，避免构建时漂移。
