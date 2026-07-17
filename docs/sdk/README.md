# QingToolbox Module API

Status:

**Experimental — 0.x compatibility is not guaranteed.**

模块目前可以依赖 `QingToolbox.Abstractions` 开发。示例模块、项目模板和模块源码位于仓库的 `modules` 分支；当前开发流程仍可能需要设置 `QingToolboxHostRoot`。

独立 NuGet SDK 尚未发布，稳定的 Module API Version 也尚未冻结。权限字段用于声明和用户告知，不构成强制沙箱；模块在 QingToolbox 宿主进程内以当前用户权限运行。外部开发者暂时不应依赖 0.x 二进制兼容性。

后续计划包括：

- API 契约规范；
- NuGet SDK；
- `dotnet new` 模板；
- `qtool` 验证和打包 CLI。

这些内容不属于 0.2.0-alpha Preview 2 的实现范围。
