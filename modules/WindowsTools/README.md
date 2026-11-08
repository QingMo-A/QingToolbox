# Windows 设置助手

官方独立进程模块，ID `qing.windowstools`，API 1，Windows x64。使用 Rust + Vue 3，与工具箱共用按钮、弹窗、字体和主题适配，不依赖 WPF。

## 功能与边界

- 自动更新：读取 `NoAutoUpdate`，关闭、启用、恢复首次修改前的值（包括原来没有此值）。仅支持相应策略的专业版、企业版、教育版等；家庭版引导到系统更新设置。不禁用服务、计划任务或 Defender，不承诺阻止全部更新。策略生效可能延迟或需要重启。
- 防火墙：展示域、专用、公用三种配置的实际状态，分别关闭、开启或恢复本地原配置；不停止 `MpsSvc` 服务、不更改防火墙规则。服务未运行时跳过不可用的系统查询，显示“已保存配置（不代表已生效）”。可在模块确认与 UAC 授权后依次检查、启动 BFE 和 MpsSvc，仅把已禁用的服务改为自动启动。启动服务会让现有规则生效，可能影响网络连接；不修改服务 ACL、不重置规则、不关闭第三方安全软件，无法修复时明确提示而非伪报成功。组织策略或其他安全软件可能优先生效。
- 默认应用表格：预置 22 个常用文档、图片、音视频和压缩格式，支持搜索、增删自定义后缀、多选、批量保存目标程序和准备候选关联。可为不同后缀保存不同程序；批量设置不要求它们使用同一个应用。最多 128 项，全选仅作用于当前搜索结果。表格同时展示保存目标、系统当前默认、检查状态及逐项错误。
- 启动检查：默认开启；自动准备恢复默认关闭。检查在模块进程的后台线程运行一次，启停和页面切换不反复修改。勾选自动准备后，只有确认发生变化或没有默认值的已配置项才重新注册候选；未知状态、已匹配或目标程序失效的条目不自动注册，也不自动打开系统设置。**Windows 10/11 不允许此类工具静默修改用户默认应用；“准备恢复”不等于“已经恢复默认”**。请点击“前往 Windows 确认”，返回后点“检查全部/选中项”核对。Windows 11 新版设置可定位到注册项，旧版按后缀查找；不会模拟系统确认、改写 `UserChoice` 或其哈希。
- 安全边界：只允许本地现存 `.exe` 候选程序，注册不运行程序、不打开文件，不处理可执行文件、脚本或系统快捷方式后缀。移除条目只影响管理表，不删除仍可能作为默认的注册项，避免破坏打开方式。

## 权限、备份与恢复

读取状态不提权。每次系统修改先显示模块确认框，再单独启动固定脚本请求 UAC，工具箱本体不需要管理员运行。管理操作仅接受封闭动作枚举，不接收任意命令、脚本、注册表路径或外部恢复文件。后台操作通过轮询呈现，不阻塞宿主 IPC。

首次修改前值保存在管理员权限的 `HKLM\SOFTWARE\QingToolbox.WindowsTools\Backups`。恢复前比较本模块最后应用值及原值；检测到其他修改则拒绝覆盖。全局互斥锁防止多个模块实例同时操作。域加入或检测到 MDM 管理时禁止系统修改（不是完整的企业管理检测器；组织策略始终优先）。

模块启动时只按上述用户选项检查/准备默认应用，不会自动修改更新策略、防火墙配置或服务。停用、关闭窗口或卸载模块都不会自动恢复系统配置。删除模块前请按需恢复。管理表和偏好保存在模块数据目录 `associations.json`，原子写入；损坏时保留文件并禁止覆盖。首次升级从本模块已有候选注册项迁移，不读取其他软件的配置。系统操作审计在同目录 `operations.jsonl`，约 1 MiB 轮转；不记录用户文件内容。首次服务修复前快照在管理员备份区 `serviceRepair.before`，仅供诊断，不提供再次停用核心服务的“还原”。

关闭更新或防火墙降低系统防护，建议仅为排查问题临时使用，并及时恢复。

## 构建与检查

```powershell
./scripts/build-module.ps1 -Module windowstools -QingToolboxHostRoot F:/QingToolbox-toolbox
./scripts/package-module.ps1 -ModuleId qing.windowstools -SkipBuild
cargo test --manifest-path modules/WindowsTools/Cargo.toml --locked
powershell -NoProfile -ExecutionPolicy Bypass -File modules/WindowsTools/tests/system.Tests.ps1
node modules/WindowsTools/tests/protocol.mjs
# ui-src: npm test
```

系统写操作仅通过模拟后端测试，开发构建不会关闭当前电脑的更新或防火墙。实际 UAC、策略生效、系统设置内默认应用确认需用户自行验收。

## 官方依据

- [Windows Update 策略及 NoAutoUpdate](https://learn.microsoft.com/en-us/windows/deployment/update/waas-wu-settings)
- [Windows 防火墙命令配置（不要停止防火墙服务）](https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/configure-with-command-line)
- [启动 Windows 默认应用设置](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-default-apps-settings)
- [默认应用平台：注册候选并尊重系统用户选择](https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/default-apps-platform)
- [Windows 10 起 Open With 对话框不能修改默认关联](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/ns-shlobj_core-openasinfo)

本模块遵循项目 MIT 许可；依赖许可见安装包 `LICENSES.txt` 等文件。
