# QingToolbox

QingToolbox 是面向 Windows 的轻量模块化工具箱。Shell 提供现代化界面、
模块发现和生命周期管理，实际工具功能由独立模块按需提供。

当前版本：**0.1.0-alpha Preview**。这是面向少量测试用户的预览版，
不是正式稳定版，也不代表生产环境可用。

## 主要功能

- 扫描模块清单但不在启动或刷新时加载 DLL。
- 由用户手动加载、启用、停用和卸载模块。
- 使用 collectible `AssemblyLoadContext` 支持进程内模块卸载。
- 在独立窗口中承载模块提供的 WPF View。
- Shell 与模块支持简体中文和英文。
- 从 `.qmod` 包导入用户模块（Preview）。

## 运行 Preview

### 当前用户安装版

运行 `QingToolbox-0.1.0-alpha-win-x64-setup.exe`。安装器基于 Inno Setup，
只为当前用户安装，不需要管理员权限，也不会触发 UAC。默认目录为：

```text
%LOCALAPPDATA%\Programs\QingToolbox
```

安装器会根据 Windows UI 语言显示英文或简体中文，并创建本地化的开始菜单
卸载入口；桌面快捷方式默认不勾选。安装包始终是 self-contained，因此不要求
预先安装 .NET 10 Desktop Runtime。安装器当前没有代码签名，Windows
SmartScreen 可能显示未知发布者警告。

卸载可使用“Windows 设置 → 应用 → 已安装的应用 → QingToolbox → 卸载”，
或开始菜单中与安装语言一致的卸载入口。卸载默认保留
用户模块、模块数据和设置。需要完整清理时，请手动删除：

```text
%LOCALAPPDATA%\QingToolbox
%APPDATA%\QingToolbox
```

### 便携 ZIP

从 GitHub Release 下载 `QingToolbox-0.1.0-alpha-win-x64.zip`，校验 SHA256
后解压，运行 `QingToolbox.Shell.exe`。便携版需要预先安装 .NET 10 Desktop
Runtime。二进制文件没有代码签名，Windows SmartScreen 可能显示警告。

开发环境运行：

```powershell
dotnet build
dotnet run --project QingToolbox.Shell
```

## 模块目录

- `QingToolbox.Shell.exe` 同目录下的 `Modules`：开发/随程序提供的模块。
- `%LOCALAPPDATA%\QingToolbox\Modules`：用户导入模块。
- `%APPDATA%\QingToolbox\Data`：模块运行数据。
- `%APPDATA%\QingToolbox\settings.json`：用户设置。

开发目录优先于用户目录。Refresh Modules 只读取清单，不会加载程序集。

## `.qmod` 安全提醒

`.qmod` 本质是 ZIP 模块包。`0.1.0-alpha` 尚未实现包签名；模块加载后拥有
当前用户权限，因此只应导入可信来源模块。格式和校验规则参见
[`docs/QMOD_FORMAT.md`](docs/QMOD_FORMAT.md)。

## 开发和发布

- 模块契约与本地化：[`docs/MODULE_DEVELOPMENT.md`](docs/MODULE_DEVELOPMENT.md)
- 本地化：[`docs/LOCALIZATION.md`](docs/LOCALIZATION.md)
- 开发环境：[`docs/DEVELOPMENT_SETUP.md`](docs/DEVELOPMENT_SETUP.md)
- Preview Release Notes：[`docs/releases/0.1.0-alpha.md`](docs/releases/0.1.0-alpha.md)
- 更新记录：[`CHANGELOG.md`](CHANGELOG.md)

生成 Preview 发布包：

```powershell
./scripts/publish-preview.ps1
```

输出位于 `artifacts/`，其中包含 zip 和 SHA256 文件。发布产物不提交到 Git。

生成当前用户安装器（需要本机安装 Inno Setup 6）：

```powershell
./scripts/build-installer.ps1
```

安装器构建说明参见 [`installer/README.md`](installer/README.md)。安装器只包含
QingToolbox 宿主，不包含 TextTools、ScreenPin、WindowTopmost 或其他具体模块。

## License

QingToolbox 使用 [MIT License](LICENSE)。Shell 使用的 Nieobie Game Icon Pack
图标遵循 CC0 1.0。
