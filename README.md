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

从 GitHub Release 下载 `QingToolbox-0.1.0-alpha-win-x64.zip`，校验 SHA256
后解压，运行 `QingToolbox.Shell.exe`。当前没有安装程序或卸载程序，二进制
文件也没有代码签名，因此 Windows SmartScreen 可能显示未知发布者警告。
默认发布包需要预先安装 .NET 10 Desktop Runtime；发布工程也支持生成
self-contained 包。

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

## License

QingToolbox 使用 [MIT License](LICENSE)。Shell 使用的 Nieobie Game Icon Pack
图标遵循 CC0 1.0。
