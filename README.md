# QingToolbox official modules

`modules` 分支的 `modules/` 是 Rust/Tauri 独立模块的唯一源码、安装包和官方更新信息目录。工具箱本体、模块 API 与共享 UI 在 `toolbox` 分支维护。安装器不内置这些模块；用户自行导入 `.qmod`，或由支持仓库下载的工具箱从此目录获取并安装。

## 目录

```text
modules/
  index.json                 官方模块目录和更新信息路径
  Launcher/                  启动台
  QingPdf/                   PDF 工具
  PowerGuard/                断电保护
  ScreenPin/                 截图贴图
  TextTools/                 文本工具
  WindowTopmost/             窗口置顶
  Canary/                    开发诊断，不作为普通工具展示
scripts/                     构建入口、目录验证和发布记录生成
docs/MODULE_CATALOG.md       更新源和发布规则
```

每个模块包含 `module.json`、`update.json`、`icon.svg`、Rust 源码与锁文件；有 Vue 界面的模块还包含 `ui-src/`。第三方运行时和许可证留在对应模块的 `third-party/`，版本与校验不变。

新版本的 `.qmod` 与 `.qmod.sha256` 放在各模块的 `packages/`。只提交真正交付的版本包，不提交 `target/`、`dist/` 或 `node_modules/`；模块更新不再创建 GitHub Tag / Release。

QingTransfer 已整合到工具箱设备页，其内部传输引擎仍属于工具箱，不提供第二个独立下载入口。

## 本地开发

同一 Git 仓库的两个 worktree 会自动识别。分别克隆的目录或 CI 可显式指定：

```powershell
$env:QINGTOOLBOX_HOST_ROOT = 'D:\QingToolbox-toolbox'
$env:QINGTOOLBOX_MODULES_ROOT = 'D:\QingToolbox-modules'
./scripts/verify-modules.ps1 -MetadataOnly
./scripts/build-module.ps1 -Module launcher
./scripts/package-module.ps1 -ModuleId qing.launcher -SkipBuild
```

构建沿用宿主现有的受控暂存机制，输出到宿主开发资源目录；`.qmod` 默认放在宿主的 `artifacts/tauri-modules/`。`target/`、`node_modules/`、`dist/` 为本地缓存，不提交。

Vue 模块通过 `@qingtoolbox/module-ui` 在构建时引用工具箱真实组件和令牌，静态产物分别打入各自 `.qmod`。不复制另一套组件源码，也不依赖宿主路由或页面运行时。兼容宿主必须包含 `QingToolbox.WebUI/src/design-system/module/`。

## 发布信息

入口为 GitHub `modules` 分支的 `modules/index.json`。每个模块的 `update.json` 只登记实际发布并核验过的包，源码改动不等于新版已发布。

目录使用 schema 2 和 `qingtoolbox-official-tauri`，与旧 WPF schema 1 隔离。宿主 API 仍为整数 `apiVersion: 1`；目录 schema、API 和 `tauri-process-v1` 包格式是三个独立概念。

`update.json` 指向固定 Commit 中的安装包，而不是会移动的分支文件。支持该下载规则的工具箱会检查大小、SHA-256、模块 ID、版本及 API 后安装。旧版只接受 Release 地址的工具箱需先更新一次本体；旧 Release 记录保留，不冒充新包。

旧 WPF 源码、测试、脚本和未提交改动在本机 `legacy-wpf/` 完整保留。该目录被 Git 忽略，不参与新构建；已提交历史仍可通过 Git 查看。
