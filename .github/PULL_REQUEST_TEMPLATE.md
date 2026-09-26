## 改动说明

<!-- 用一两句话说明这个 PR 做了什么，以及为什么。 -->

## 改动类型

- [ ] `[+]` 新增功能
- [ ] `[fix]` 修复
- [ ] `[refactor]` 重构（无行为变化）
- [ ] `[docs]` 文档
- [ ] `[style]` UI / 样式
- [ ] `[chore]` 工程配置 / 脚本
- [ ] `[test]` 测试

## 影响范围

- [ ] Tauri 宿主（`QingToolbox.Tauri`）
- [ ] Vue 前端（`QingToolbox.WebUI`）
- [ ] 模块协议（`protocol/`）
- [ ] 原生模块（`QingToolbox.Tauri/native-*`）
- [ ] 安装器 / 发布脚本
- [ ] 文档
- [ ] 历史 WPF 宿主

## 验证

<!-- 列出实际执行过的验证命令与结果。未执行的项目请明确写「未执行」。 -->

```powershell
pwsh ./scripts/verify-tauri.ps1
```

## 检查清单

- [ ] 改动范围与提交信息一致，未夹带无关修改。
- [ ] 涉及宿主或模块时，`verify-tauri.ps1` 通过。
- [ ] 未提交 `artifacts/`、`bin/`、`obj/`、`.qingtoolbox/`、安装器、日志或用户数据。
- [ ] 新增或改名文档时，已同步更新根 `README.md` 的「文档地图」表格。
- [ ] 文档中的版本号、脚本路径与实际状态一致。
- [ ] 未完成的能力已如实标注（未完成 / `Not Run` / `Blocked`）。
- [ ] 未创建 tag 或 GitHub Release。

## 相关 Issue

<!-- 例如：Closes #12 -->
