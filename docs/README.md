# QingToolbox 文档目录说明

> **完整文档索引已经合并到仓库根 [`README.md`](../README.md) 的「文档地图」章节。**
> 那里按主题列出了全部文档的路径、状态与适用读者，是查阅文档的唯一入口——仓库首页下拉即可看到，无需先点进 `docs/`。
> 本页只保留维护者须知与待办，不再重复维护一份索引。

这样做的好处只有一个：**索引只有一处**。此前根 README 的「常用入口」与 `docs/` 下的完整索引是两份清单，新增文档时很容易只更新其中一份，导致读者按过期清单去找文件。

---

## 新增或改名文档时

1. 在根 [`README.md`](../README.md)「文档地图」的对应分组表格中增删一行。
2. 如果这篇文档属于历史路径或已经冻结，别忘了在「状态」列写清楚，避免后来者照着过期文档动手。
3. 重新检查相对链接是否可解析。

---

## 已知文档缺口

以下问题已识别，尚未修复：

- `docs/releases/0.2.6-alpha.md` 缺失。`docs/releases/` 中存在该版本的本地副本，但内容与 `0.2.7-alpha` 高度重合，疑为旧版误命名，需人工确认后补齐。
- 缺少 Rust 模块模板。`modules` 分支的 `templates/ModuleTemplate` 属于历史 WPF 进程内模块，不适用于当前 Tauri 主线。
- 缺少端到端「新建 Tauri 模块」指南。目前需组合 [`../protocol/README.md`](../protocol/README.md)、[`TAURI_MODULE_LIFECYCLE.md`](TAURI_MODULE_LIFECYCLE.md)、[`MODULE_DEVELOPMENT.md`](MODULE_DEVELOPMENT.md) 与 [`../QingToolbox.Tauri/native-module-canary/`](../QingToolbox.Tauri/native-module-canary/) 自行拼接。
- `modules` 分支仍停留在 WPF 模块线，其模块文档未标注当前主线状态。
- [`LOCALIZATION.md`](LOCALIZATION.md) 的 View 本地化示例仍为历史 WPF 路径，缺少 Tauri 对应说明。
