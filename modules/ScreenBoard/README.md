# 屏幕画板 / Screen Board

独立的 Rust + Tauri 2 + Vue 3 官方模块（API 1），不是内置功能，也不是桌面截图编辑器。原生透明置顶绘画层覆盖整个虚拟桌面，悬浮工具条是另一个原生窗口。模块设置及工具条复用工具箱共享 UI 组件和主题 token。

## 操作

- 在模块页加载、启用，打开设置并点击“打开画板”。
- 画笔、直线、曲线、矩形、椭圆；预设色、RGB 数值、系统调色盘，画笔宽度 1–40 px。
- 按住 **Ctrl**：画笔收笔时规整为直线或最小二乘二次曲线；直线吸附 45°；矩形/椭圆约束正方形/正圆。曲线工具始终拟合平滑的二次曲线，不声称能从任意涂鸦识别所有几何图形。
- **Alt + 点击**：暂时隐藏本模块两个窗口，读取下方屏幕颜色并恢复。不是持续录屏。部分受保护/独占全屏画面无法取色。
- 文字工具点击屏幕输入，支持中文和多行；Ctrl+Enter 完成，Esc 取消。选择工具双击文字再次编辑。
- 函数输入如 `sin(x)`、`x^2`、`1/x`、`sqrt(x)`，点击屏幕放置。坐标范围在模块设置中定义。仅解析有限的数学表达式，不执行 JS；采样有界、遇非有限值/明显渐近线断开路径。
- 选择工具拖动对象，滚轮缩放；“应用到选中项”更新颜色、宽度与文字字号。Delete 删除对象。
- 橡皮按对象删除；Ctrl+Z 撤销，Ctrl+Y / Ctrl+Shift+Z 重做。清空也可撤销。
- **Ctrl+Shift+D** 切换绘画/穿透；工具条始终可点，可拖动移动。快捷键冲突时工具条会提示。
- **Esc** 或工具条 × 收起画板，不清空；下次打开仍在。模块禁用隐藏覆盖层，重新启用不会自动盖住桌面。主机退出、模块卸载后绘画内存释放。

## 边界

初版不含画面导出、跨重启画面保存、图层管理或无限画布。设置保存在模块私有 `settings.json`；文字和笔迹只驻留本机进程内存，不联网。最多 500 个对象，每条自由笔迹最多 4096 点，有界撤销历史。桌面独占全屏、UAC 安全桌面不保证能覆盖；不试图绕过系统限制。显示器布局改变后请重新加载模块以重新获取虚拟桌面尺寸。

## 开发

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-module.ps1 -Module screenboard -QingToolboxHostRoot F:/QingToolbox-toolbox
cargo test --manifest-path modules/ScreenBoard/Cargo.toml --locked --offline
cd modules/ScreenBoard/ui-src
npm test
```

构建默认同步开发环境，不覆盖本地正式版，不自动发布。源代码及产物以后按模块分支发布流程更新。

UI 自动化可使用 `ui-src/tests/ui-smoke.mjs`（`QING_PLAYWRIGHT_PATH` 指向 Playwright 的模块入口）；`tests/native-smoke.mjs` 通过隔离数据目录启动真实 WebView2 覆盖层，短暂验证绘画、穿透、屏幕取色和生命周期，不操作其他应用。

依赖使用 Cargo/npm 锁文件固定。`third-party` 补充原始 crate 包遗漏的上游许可证文本，构建时汇总到 `LICENSES.txt`；其中 selectors 0.36.1 保持未修改，MPL 源代码获取链接随许可证一起提供。
