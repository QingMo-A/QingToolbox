# Icon Assets

> 状态：**参考**。本页记录图标与品牌资产的来源与映射。当前主线宿主（Tauri）的界面图标定义在
> `QingToolbox.WebUI` 中；下表描述的是历史 WPF Shell 的侧边栏与操作图标。

QingToolbox Shell uses a selected subset of Nieobie Game Icon Pack from
`svg/no-padding`, licensed under CC0 1.0 Universal.

| Target file | Upstream source path | Purpose |
|---|---|---|
| `home.svg` | `svg/no-padding/6-buildings/house.svg` | Sidebar Home |
| `modules.svg` | `svg/no-padding/8-ui/grid.svg` | Sidebar Modules |
| `running.svg` | `svg/no-padding/8-ui/circle-ring.svg` | Sidebar Running |
| `settings.svg` | `svg/no-padding/8-ui/settings.svg` | Sidebar Settings |
| `pin.svg` | `svg/no-padding/2-items/pushpin.svg` | Sidebar Pin |
| `refresh.svg` | `svg/no-padding/8-ui/refresh.svg` | Refresh modules |
| `load.svg` | `svg/no-padding/9-media/download.svg` | Load module |
| `activate.svg` | `svg/no-padding/9-media/play.svg` | Activate module |
| `deactivate.svg` | `svg/no-padding/9-media/pause.svg` | Deactivate module |
| `unload.svg` | `svg/no-padding/9-media/trash.svg` | Unload module |
| `open.svg` | `svg/no-padding/8-ui/menu-open.svg` | Open module view |
| `close.svg` | `svg/no-padding/8-ui/cross.svg` | Close module view |

## Module icons

Modules may set `"icon": "icon.svg"` in `module.json`. The path is resolved relative
to the deployed module directory and currently only SVG is supported. If the file is
missing, invalid, or omitted, the Shell displays its packaged `modules.svg` fallback.

The Hello development module includes its own `icon.svg`, derived from the same
Nieobie `svg/no-padding/8-ui/grid.svg` source.

## 品牌资产

| 资产 | 路径 | 说明 |
| --- | --- | --- |
| 矢量源 | `QingToolbox.Shell/Assets/Branding/QingToolbox.Mark.svg` | 权威、可编辑的纯矢量源 |
| Windows 图标 | `QingToolbox.Shell/Assets/Branding/QingToolbox.ico` | 含 16、20、24、32、40、48、64、128 与 256 像素帧 |
| Tauri 宿主图标 | `QingToolbox.Tauri/src-tauri/icons/icon.ico` | Tauri 构建使用 |

图标采用蓝色圆角工具箱容器与白色几何 Q，不使用字体或第三方品牌素材。

Windows Explorer 与任务栏可能缓存旧图标。验证新版本时可能需要重启 Explorer，旧快捷方式可能需要
删除后重新创建；安装器测试建议先卸载旧版本或使用干净环境。应用与安装器不会主动清理系统图标缓存。
