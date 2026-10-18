# Qing Convert 0.1.0 — 本地初版，未发布

Rust 进程模块 + Vue 3。通过构建时别名引用宿主真实共享 UI，实时跟随深浅主题、皮肤和字体。API 版本为 1。

## 已实现

- PNG/JPEG/WebP/BMP 批量转换；最大宽度（不放大）、JPEG 质量、透明区域背景色。WebP 为无损编码。
- MP3/WAV/FLAC/M4A 音频转换，以及从本地视频提取第一条音轨。需要本机或自行配置的 FFmpeg；若旁边有 ffprobe.exe，可显示文件内进度，否则显示不定进度和已完成数量。
- DOC/DOCX → PDF：优先使用可用的 LibreOffice，否则使用已安装并激活的 Microsoft Word。可指定引擎。
- PDF → DOCX：内置文字提取，保留文字与分页，不还原图片、表格结构和原版式。纯扫描件明确报错，尚无 OCR。
- 本地多文件队列、取消、单文件错误、打开结果目录；原文件保留，重名自动编号。
- 每个转换任务独立子进程；Windows Job 绑定整个引擎进程树。Word 实例加入同一 Job，取消/超时/卸载不留下自己的 Office 实例，不改用户已打开的 Word。

首版最多 100 个文件。图片上限 128 MB / 8000 万像素，文档上限 64 MB / PDF 500 页；音频上限 8 GB。单个任务超时为文档/图片 3 分钟、音频 1 小时。加密文档暂不接受密码输入。

## 使用

选择转换类型 → 拖入或添加文件 → 选择格式、参数与输出目录 → 开始转换。输出默认位于首个源文件目录的 `Converted` 子目录；成功后点击文件右侧目录图标。

“引擎设置”可选择 `ffmpeg.exe` 和 LibreOffice 的 `soffice.exe`。模块不静默下载大型引擎，不上传文档，不安装或激活 Office。未配置可选引擎时仍可使用图片和 PDF 文字转换。

DOC/DOCX 导出依赖文档中的字体；不同 Office 引擎导出结果可能不同。Word 自动化以只读方式打开源文件，强制禁用宏、禁止自动更新链接，且不加入最近文件。为保护已有文档，检测到 Word 已在运行时会拒绝该次转换；请先关闭 Word，或改用 LibreOffice。

## 开发与安装

```powershell
./scripts/build-module.ps1 -Module convert
./scripts/package-module.ps1 -ModuleId qing.convert -SkipBuild
node modules/Converter/tests/smoke.mjs
```

产物默认在宿主 `artifacts/tauri-modules/`，构建同时更新宿主 Dev 资源。文件选择统一调用共享 SDK 的 `selectFiles` / `selectDirectory`，在身份文件 `hostCapabilities` 中声明能力；不需要宿主为模块 ID 配置 Tauri 插件权限。旧宿主缺少通用接口时提示先更新工具箱。

本版本只登记开发目录，不登记任何已发布安装包。视频转码、HEIC、OCR 与复杂 PDF 版式还原留待后续。
