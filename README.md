# QingToolbox

The Modules page provides read-only detection against the official per-module update metadata. Checks use isolated conditional-request caches and never download or install packages. See [module update detection](docs/MODULE_UPDATE_DETECTION.md).

QingToolbox Shell 和模块宿主窗口共享可扩展的 WPF `WindowChrome` 标题栏基础设施。它保留系统拖动、缩放、系统菜单和标准窗口命令，并通过最大化按钮命中测试支持 Windows 11 Snap Layout；MainWindow 的自定义操作区提供主动切换桌面悬浮标的入口。

标题栏度量、窗口能力映射和 DPI 感知命中测试由共享窗口层统一管理。模块窗口标题可随语言切换原位更新；空扩展区不会占位。Shell 在 500 DIP 宽度下进入紧凑布局，但 Windows 11 Snap 弹层、多显示器及不同 DPI 显示器切换仍需在对应实体环境中验证。

MainWindow 标题栏可由用户主动切换到单个桌面悬浮标。悬浮标会隐藏而不关闭现有 Shell 和模块窗口，单击、键盘或菜单可恢复原窗口，右键菜单可执行完整退出。窗口模式转换被串行协调，退出不会先闪现隐藏的 MainWindow。位置以显示器设备名和显示器工作区内相对比例保存在现有 `settings.json`，显示器消失时安全回退。

主窗口关闭行为现在可以明确配置。首次关闭会询问是最小化到 Windows 通知区域还是完整退出，选择保存在现有 `settings.json`，设置页可随时改为重新询问。通知区域图标左键恢复 Shell，右键菜单提供打开、设置、桌面悬浮标和退出；图标可能最初位于 Windows 隐藏图标溢出区，并会在正常退出时移除。非退出状态始终保留主窗口、悬浮标或通知区域图标之一作为恢复入口，普通用户不需要进程终止脚本。

`settings.json` 是共享用户设置文件。语言、悬浮标、登录启动和模块启动授权更新在实例级异步锁内合并，并通过同目录临时文件原子替换，避免并发更新互相覆盖；损坏文件会保留为有限数量的 `settings.corrupt-*.json` 备份。

QingToolbox 以当前 Windows 用户为范围保持单实例运行。Pipe Server 在 DI 前启动，二次启动会在有限预算内重试并等待 `OK` 确认；普通手动激活优先于尚未完成的后台启动显示模式，登录启动探测不会抢占焦点。用户可选择 HKCU 登录自启动以及主窗口、最小化或悬浮标显示模式；选择会可靠保存，失败则回滚，安装器默认不启用该功能。

模块的“随工具箱启动”必须由用户明确开启。授权除诊断用 manifest/入口 SHA256 外，还绑定递归覆盖依赖、原生库、配置、本地化和资源的完整载荷 SHA256；旧版仅入口授权必须重新确认。Shell 会先进入可恢复显示状态，再在 Load 前重新验证载荷并激活匹配模块，且不会自动打开模块窗口。模块文件变化后必须重新确认；Refresh 和 Import 仍只发现模块，不会触发加载。该校验用于启动授权一致性，不构成插件沙箱。

无人值守启动期间的取消会在退出边界内安全收拢。单实例 Pipe 对输入执行严格长度限制，在激活请求被安全排队后立即应答；UI 激活失败、客户端断开或协议错误不会终止后续 Pipe 服务。退出时 Shell 会先拒绝新激活并停止 Pipe，再卸载模块和释放依赖服务。

QingToolbox 是面向 Windows 的轻量模块化工具箱。Shell 提供现代化界面、
模块发现和生命周期管理，实际工具功能由独立模块按需提供。

当前版本：**0.2.0-alpha Preview 2**。主题为 **Reliable Startup & Module Lifecycle**，
这是面向少量测试用户的预览版，
不是正式稳定版，也不代表生产环境可用。

## 主要功能

- 扫描模块清单但不在启动或刷新时加载 DLL。
- 模块导入后默认由用户手动加载、启用、停用和卸载；只有明确授权“随工具箱启动”的完整载荷匹配模块才会在启动阶段恢复。
- 使用 collectible `AssemblyLoadContext` 支持进程内模块卸载。
- 在独立窗口中承载模块提供的 WPF View。
- Shell 与模块支持简体中文和英文。
- 从 `.qmod` 包导入用户模块（Preview）。
- 对已下载并校验的官方 `.qmod` 执行稳定句柄离线验证，并原子发布到环境隔离的 Verified Staging；候选目录在 Incoming 中完成全部认证，`Directory.Move` 是唯一提交点。完整 Release 身份控制共享，崩溃可恢复的文件句柄锁保证 module/version 唯一发布；诊断 marker 清理失败不会改写已提交成功。暂存不等于安装，不接触用户模块目录，也不加载 DLL。

## 运行 Preview

### 当前用户安装版

运行 `QingToolbox-0.2.0-alpha-win-x64-setup.exe`。安装器基于 Inno Setup，
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

从 GitHub Release 下载 framework-dependent 的 `QingToolbox-0.2.0-alpha-win-x64.zip`，校验 SHA256
后解压，运行 `QingToolbox.Shell.exe`。便携版需要预先安装 .NET 10 Desktop
Runtime。二进制文件没有代码签名，Windows SmartScreen 可能显示警告。

当前 Preview 的便携 ZIP 是 **framework-dependent** 版本，适合已经安装
.NET 10 Desktop Runtime 的开发者和测试用户。运行库应从 Microsoft 官方渠道
安装；不要将独立的 Runtime 安装程序直接塞入 ZIP 后要求用户手动执行。

后续正式发布计划同时提供两种清晰命名的便携包：

- `win-x64-self-contained.zip`：内含应用所需的 .NET 运行时，解压即用，作为普通
  用户的首选下载项；它不包含 .NET SDK，也不会内置任何具体工具模块。
- `win-x64-framework-dependent.zip`：体积较小，需要预先安装对应版本的
  .NET Desktop Runtime，供高级用户和受控部署环境选择。

在 self-contained 便携包的发布脚本和 CI 校验完成之前，项目不会把当前 ZIP
描述成“无需运行库”版本。两类包都应继续保持 host-only，并提供独立 SHA256。

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

`.qmod` 本质是 ZIP 模块包。`0.2.0-alpha` 尚未实现包签名；模块加载后拥有
当前用户权限，因此只应导入可信来源模块。格式和校验规则参见
[`docs/QMOD_FORMAT.md`](docs/QMOD_FORMAT.md)。

安全 Staging 的路径、ZIP Bomb、Manifest 和原子发布边界参见
[`docs/QMOD_STAGING_SECURITY.md`](docs/QMOD_STAGING_SECURITY.md)。

当前 Module API 为 **Experimental**，权限声明不构成系统级沙箱，独立 NuGet SDK
和稳定兼容承诺尚未发布。开发状态与路线见
[`docs/sdk/README.md`](docs/sdk/README.md)。

## 开发和发布

- 模块契约与本地化：[`docs/MODULE_DEVELOPMENT.md`](docs/MODULE_DEVELOPMENT.md)
- 本地化：[`docs/LOCALIZATION.md`](docs/LOCALIZATION.md)
- 开发环境：[`docs/DEVELOPMENT_SETUP.md`](docs/DEVELOPMENT_SETUP.md)
- Preview 2 Release Notes：[`docs/releases/0.2.0-alpha.md`](docs/releases/0.2.0-alpha.md)
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

宿主开发与模块测试应使用项目本地隔离 Profile，参见
[`docs/DEVELOPMENT_ENVIRONMENTS.md`](docs/DEVELOPMENT_ENVIRONMENTS.md)。

## License

Windows 登录自启动的 Task Scheduler、注册表降级、关键启动路径与诊断说明见 [`docs/WINDOWS_STARTUP_RELIABILITY.md`](docs/WINDOWS_STARTUP_RELIABILITY.md)。

Official module updates can now be downloaded and integrity-verified manually without extraction or installation. See [`docs/MODULE_PACKAGE_DOWNLOAD.md`](docs/MODULE_PACKAGE_DOWNLOAD.md) for the security boundaries and staging workflow.


QingToolbox 使用 [MIT License](LICENSE)。Shell 使用的 Nieobie Game Icon Pack
图标遵循 CC0 1.0。
Shell 通过显式 Shutdown 管理后台生命周期。通知区、悬浮标、模块窗口或模块运行时的单项清理失败不会阻止最终退出，普通用户不需要借助进程清理 BAT。
