# QingToolbox

QingToolbox Shell 和模块宿主窗口共享可扩展的 WPF `WindowChrome` 标题栏基础设施。它保留系统拖动、缩放、系统菜单和标准窗口命令，并通过最大化按钮命中测试支持 Windows 11 Snap Layout；MainWindow 的自定义操作区提供主动切换桌面悬浮标的入口。

标题栏度量、窗口能力映射和 DPI 感知命中测试由共享窗口层统一管理。模块窗口标题可随语言切换原位更新；空扩展区不会占位。Shell 在 500 DIP 宽度下进入紧凑布局，但 Windows 11 Snap 弹层、多显示器及不同 DPI 显示器切换仍需在对应实体环境中验证。

MainWindow 标题栏可由用户主动切换到单个桌面悬浮标。悬浮标会隐藏而不关闭现有 Shell 和模块窗口，单击、键盘或菜单可恢复原窗口，右键菜单可执行完整退出。窗口模式转换被串行协调，退出不会先闪现隐藏的 MainWindow。位置以显示器设备名和显示器工作区内相对比例保存在现有 `settings.json`，显示器消失时安全回退；应用不会自动进入该模式，也不包含系统托盘集成。

`settings.json` 是共享用户设置文件。语言、悬浮标、登录启动和模块启动授权更新在实例级异步锁内合并，并通过同目录临时文件原子替换，避免并发更新互相覆盖；损坏文件会保留为有限数量的 `settings.corrupt-*.json` 备份。

QingToolbox 以当前 Windows 用户为范围保持单实例运行。普通二次启动只唤醒现有窗口，登录启动探测不会抢占焦点。用户可选择 HKCU 登录自启动以及主窗口、最小化或悬浮标显示模式；安装器默认不启用该功能。

模块的“随工具箱启动”必须由用户明确开启。授权同时绑定模块 ID、版本、manifest SHA256 和入口程序集 SHA256，只有完整匹配的模块才会在首次初始化扫描完成后被加载并激活，且不会自动打开模块窗口。模块文件变化后必须重新确认；Refresh 和 Import 仍只发现模块，不会触发加载。

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

`toolbox` 分支的 Windows CI 会构建并校验便携 ZIP 与安装器、执行模块 Smoke
Test，并在隔离用户目录中进行静默安装—卸载往返测试。CI 上传的 Preview
artifacts 仅用于验证，不会自动创建 GitHub Release、tag 或提交构建产物。
Shell、任务栏、快捷方式和安装器现在统一使用正式 QingToolbox 品牌图标。

## 品牌资产

`QingToolbox.Shell/Assets/Branding/QingToolbox.Mark.svg` 是权威、可编辑的纯矢量源，
`QingToolbox.ico` 是包含 16、20、24、32、40、48、64、128 和 256 像素帧的 Windows
发布资产。图标采用蓝色圆角工具箱容器与白色几何 Q，不使用字体或第三方品牌素材。

Windows Explorer 和任务栏可能缓存旧图标。验证新版本时可能需要重启 Explorer，旧
快捷方式可能需要删除后重新创建；安装器测试建议先卸载旧 Preview 或使用干净环境。
应用和安装器不会主动清理系统图标缓存，当前二进制仍未代码签名。

发布资产也可在本地运行 `./scripts/verify-preview-assets.ps1` 重新计算并校验
SHA256。CI 将中文 Inno Setup 翻译固定到已审核哈希，Roundtrip 使用
`/NOICONS`，并在失败时上传隔离的安装和卸载日志。GitHub Actions 是发布门禁，
但不会自动发布版本。

Preview 的版本、文件版本、runtime、资产文件名和 CI artifact 名称统一由
`Directory.Build.props` 与 `scripts/get-preview-release-metadata.ps1` 派生。
CI 还会生成机器可读 manifest，记录构建源码 commit、ZIP 与安装器的大小和
SHA256；官方 GitHub Actions 与 Inno 中文翻译均固定到不可变 commit。该流程
继续只做发布门禁，不创建 Release 或 tag。

## 首次使用

QingToolbox 主程序不内置任何具体工具模块。首次启动后请前往“模块”页面，导入来自
可信开发者或可信发布页面的 `.qmod` 文件；导入和刷新只发现并校验模块清单，不会加载
模块 DLL，只有用户主动点击“加载”后才会载入程序集。用户模块目录为：

```text
%LOCALAPPDATA%\QingToolbox\Modules
```

模块加载后拥有当前用户权限，因此请勿导入来源不明的模块。
导入成功后会自动进入“模块”页面并选中新模块，但仍保持未加载；只有用户主动点击
“加载”才会载入 DLL。空工具箱模式不会显示无意义的零值统计，三步引导采用受约束的
等宽布局。扫描失败时保留上一次成功发现的模块状态。

## Preview release candidate

最终 Preview 候选必须从干净且与 `origin/toolbox` 同步的 `toolbox` 分支构建：

```powershell
./scripts/build-preview-release-candidate.ps1
```

完整门禁、产物溯源和人工发布交接清单见
[`docs/PREVIEW_RELEASE_PROCESS.md`](docs/PREVIEW_RELEASE_PROCESS.md)。该流程只生成并验证
候选资产，不会创建 GitHub Release 或 tag。

## License

QingToolbox 使用 [MIT License](LICENSE)。Shell 使用的 Nieobie Game Icon Pack
图标遵循 CC0 1.0。
