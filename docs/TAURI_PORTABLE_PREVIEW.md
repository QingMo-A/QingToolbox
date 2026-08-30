# Tauri portable preview

`QingToolbox.Tauri` 现在可以生成一个可验证的 Windows portable 目录，用来
体验 Rust + Tauri 2 + Vue 3 宿主和当前已迁移的原生模块。它是迁移阶段的
预览交付物，不会替换现有 WPF 安装器，也不会写入安装目录。

在仓库根目录执行：

```powershell
pwsh ./scripts/build-tauri-portable.ps1 -Smoke -Zip
```

产物位于 `artifacts/tauri-portable/QingToolbox/`，其中包含：

- `QingToolbox.exe`：Rust/Tauri 宿主；
- `resources/modules/`：协议 canary、Qing Launcher、Qing PDF、QingTransfer、
  Text Tools、Window Topmost、PowerGuard 和 Screen Pin；
- `portable-manifest.json`：源码 commit、目标平台以及每个文件的 SHA256；
- `LICENSE` 与 `THIRD_PARTY_NOTICES.md`：随交付物保留的许可信息。

也可以运行根目录的 `run-tauri-portable.bat`，它会复用已经构建的模块，执行
宿主/模块窗口烟测后启动 portable 目录中的 exe。首次构建若没有模块资源，去掉
`-SkipModuleBuild`（默认行为）即可。

当前 portable 预览依赖系统已安装的 Windows WebView2。Qing Launcher 自带固定版本
Everything runtime 和许可证，首次使用 Everything 搜索时按需准备独立客户端/服务；
不会调用用户自己安装的实例，也不会弹出 Everything 界面。Qing PDF 自带固定版本
qpdf 运行时及许可证；其余模块均为独立 Rust 进程。正式替换 WPF 安装链前，仍需
完成 Screen Pin 浮动窗口、Launcher 的 Explorer 拖入/模块快捷键、更新器和完整
生产入口验收；这些缺口不会被此脚本隐藏。
