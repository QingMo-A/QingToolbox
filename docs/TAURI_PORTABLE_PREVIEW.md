# Tauri portable preview

`QingToolbox.Tauri` 现在可以生成一个可验证的 Windows portable 目录，用来
体验 Rust + Tauri 2 + Vue 3 宿主和当前已迁移的原生模块。它不会写入用户安装
目录；旧 WPF 安装器仍保留到签名安装链切换完成为止。

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
qpdf 运行时及许可证；其余模块均为独立 Rust 进程。Screen Pin 浮动窗口、
Launcher 的 Explorer 拖入和模块快捷键已经走 Rust 宿主边界；仍需完成签名安装器、
更新器和完整生产验收。生产候选使用：

```powershell
pwsh ./scripts/build-tauri-production.ps1 -SkipModuleBuild -Smoke
```

输出位于 `artifacts/tauri-production/QingToolbox/`，其中 manifest 会记录
`distribution=production`、Release 构建和源码是否干净。

如需验证安装器候选（仍不覆盖旧 WPF 安装），运行：

```powershell
pwsh ./scripts/build-tauri-installer.ps1 -SkipBuild -Smoke
```

脚本会校验 production manifest 的逐文件 SHA256、`resources/modules` 的完整层级、
Everything/qpdf 许可文件，然后用仓库现有 Inno Setup 编译每用户安装器并执行静默
安装/卸载 smoke。输出位于 `artifacts/tauri-installer/output/`；该候选使用独立迁移
AppId，正式切换前不会与旧 WPF 安装记录互相覆盖。
