# Tauri portable preview

`QingToolbox.Tauri` 现在可以生成一个可验证的 Windows portable 目录，用来
体验 Rust + Tauri 2 + Vue 3 宿主。宿主包不包含具体模块；各模块独立交付
为 `.qmod`，由用户导入。portable 构建不会写入用户安装目录。

在仓库根目录执行：

```powershell
pwsh ./scripts/build-tauri-portable.ps1 -Smoke -Zip
```

产物位于 `artifacts/tauri-portable/QingToolbox/`，其中包含：

- `QingToolbox.exe`：Rust/Tauri 宿主；
- `portable-manifest.json`：源码 commit、目标平台以及每个文件的 SHA256；
- `LICENSE` 与 `THIRD_PARTY_NOTICES.md`：随交付物保留的许可信息。

也可以运行根目录的 `run-tauri-portable.bat`。模块开发和 `.qmod` 打包走独立
脚本，不再由宿主 portable/installer 构建隐式打包。

当前 portable 预览依赖系统已安装的 Windows WebView2。Qing Launcher 的独立
`.qmod` 自带固定版本 Everything runtime 和许可证，首次使用搜索时按需准备独立客户端/服务；
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

脚本会校验 production manifest 的逐文件 SHA256、宿主包不含 `resources/modules`
以及宿主许可文件，然后用 Inno Setup 编译每用户安装器。升级已发布的
0.3.0-alpha 时，安装器先把旧版随包模块迁往用户模块目录，再删除旧宿主资源；
现有有效 Tauri 用户模块不会被覆盖；同 ID 的旧 WPF 模块会先备份，再迁入兼容的
Tauri 模块。输出位于
`artifacts/tauri-installer/output/`，使用 QingToolbox 原有产品 AppId。
