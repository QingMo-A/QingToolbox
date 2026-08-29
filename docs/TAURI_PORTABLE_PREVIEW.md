# Tauri portable preview

`QingToolbox.Tauri` 现在可以生成一个可验证的 Windows portable 目录，用来
体验 Rust + Tauri 2 + Vue 3 宿主和已经迁移的 Qing Launcher。它是迁移阶段的
预览交付物，不会替换现有 WPF 安装器，也不会写入安装目录。

在仓库根目录执行：

```powershell
pwsh ./scripts/build-tauri-portable.ps1 -Smoke -Zip
```

产物位于 `artifacts/tauri-portable/QingToolbox/`，其中包含：

- `QingToolbox.exe`：Rust/Tauri 宿主；
- `resources/modules/`：协议 canary 和 Rust Qing Launcher；
- `portable-manifest.json`：源码 commit、目标平台以及每个文件的 SHA256；
- `LICENSE` 与 `THIRD_PARTY_NOTICES.md`：随交付物保留的许可信息。

也可以运行根目录的 `run-tauri-portable.bat`，它会复用已经构建的模块，执行
宿主/模块窗口烟测后启动 portable 目录中的 exe。首次构建若没有模块资源，去掉
`-SkipModuleBuild`（默认行为）即可。

当前 portable 预览依赖系统已安装的 Windows WebView2，不捆绑 Everything、qpdf
或旧 .NET 模块。正式替换 WPF 安装链前，仍需完成 QingTransfer、QingPdf、更新器、
图标提取和完整模块协议迁移；这些缺口不会被此脚本隐藏。
