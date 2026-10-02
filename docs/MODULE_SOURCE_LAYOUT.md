# 独立模块源码目录

Rust/Tauri 独立模块仅在 `modules` 分支的 `modules/` 维护。宿主本体、API 与共享 UI 留在 `toolbox` 分支，原 `QingToolbox.Tauri/native-launcher` 等目录不再保留第二份源码。设备页的 `native-transfer` 是宿主内部引擎，不是独立下载模块。

现有 `build-tauri-*.ps1` 和模块打包入口兼容保留，但源码通过 `module-sources.mjs` 定位到模块 worktree。分别克隆或 CI 必须设置 `QINGTOOLBOX_MODULES_ROOT` 为模块仓库根目录。源码解析器要求 schema 2 原生官方目录，拒绝旧 WPF 源。

```powershell
$env:QINGTOOLBOX_MODULES_ROOT = 'D:\QingToolbox-modules'
./scripts/build-tauri-modules.ps1
./scripts/package-tauri-modules.ps1 -SkipBuild -Smoke
```

模块 Vue 通过构建别名引用本体 `QingToolbox.WebUI/src/design-system/module/` 的真实组件，不复制组件源码。`src-tauri/resources/modules/` 是开发环境暂存产物，不是源码；正式安装器仍为 host-only。

缓存分别计算模块源码、共享 UI 和输出指纹。独立模块源码改变不要求宿主本体重新构建；发布记录变化、Rust target 和 npm/dist 缓存不触发模块重编译。

官方目录与每模块更新清单位于 GitHub `modules` 分支的 `modules/`。目录 schema 2、宿主 API 1 与进程包格式 `tauri-process-v1` 互相独立。在线模块检查和官方模块下载页面尚未接通。

迁移两边的改动需要协调提交，CI 需同时检出匹配的两个分支。此次本地整理没有创建发布或自动推送分支。
