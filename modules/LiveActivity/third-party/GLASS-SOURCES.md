# 玻璃实现借鉴来源 / Glass design references

Qing Island 的 `src/gpu_glass.rs` 和 `src/glass.hlsl` 是面向本项目原生胶囊、生命周期及输入规则编写的 Rust / HLSL 实现。以下项目用于架构与效果思路借鉴，不表示直接安装其软件或获得作者背书。

## electron-liquid-glass — hicccc77

- 仓库：https://github.com/hicccc77/electron-liquid-glass
- 参考提交：`0684fe7fd74855e4b13bd3e8d7b6db40f91e132a`
- 许可证：MIT；原始版权及完整许可保留在 `electron-liquid-glass-LICENSE.txt`。
- 借鉴范围：DXGI Desktop Duplication → D3D11 → DirectComposition 的原生背景管线；分离高斯模糊、圆角 SDF、折射与边缘光照；背景与文字分别处理。
- 本项目差异：不引入 Electron/N-API；GPU 区域比较只回读四字节变化标志，避免简单丢弃与胶囊相同的脏矩形而漏掉真实更新；胶囊输入使用独立圆角窗口，隐藏后释放采集资源。

## liquidDX11 — poncippg-spec

- 仓库：https://github.com/poncippg-spec/liquidDX11
- 参考提交：`00c2b2b19eb5523abde473b2c08220326d4146d4`
- 借鉴范围：实时桌面玻璃、SDF 轮廓、折射和边缘高光的效果设计。
- 上游 README 声明其 `examples/example_win32_directx11/src`、脚本和项目文件中的原始代码为 MIT；Dear ImGui、FreeType、字体与图标分别保留原许可证。
- 此处仅研究效果思路，不复制或打包其 ImGui 控件、字体、图标、FreeType 或 Apple 素材。仓库根许可证属于 Dear ImGui（Omar Cornut），参考原文另保留在 `liquidDX11-root-LICENSE.txt`，不将其版权误标为本模块作者或液态玻璃原始代码作者。

## 平台资料

- https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api
- https://github.com/robmikh/Win32CaptureSample/issues/103

背景纹理仅在本机 GPU 内存中使用，不写入日志或文件，不上传。生产运行只允许回读变化标志；渲染目标像素读取仅编译到测试程序，用于对测试自行创建的窗口验证实时更新。
