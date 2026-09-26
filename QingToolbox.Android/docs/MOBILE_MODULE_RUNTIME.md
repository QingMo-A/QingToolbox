# 移动端模块运行时

> Status: **Web 通道已实现**（2026-09-26）。原生 DEX 通道仍是设计，未写代码。
>
> 关联：桌面端 `docs/QMOD_FORMAT.md`、`protocol/module-manifest.tauri.v1.schema.json`、
> 本目录 `../android_modules/README.md`、`plans/013-android-mobile-shell-and-root-capability-foundation.md`。

## 0. 结论先行

你问的三件事，答案不一样：

| 能力 | Android 能否实现 | 本项目的做法 | 硬约束 |
| --- | --- | --- | --- |
| 加载模块进内存 | **能** | Web：`WebView` 实例常驻，切走再回来不重载。原生：`InMemoryDexClassLoader`（API 26 起） | 本项目 `minSdk = 26` |
| 从内存中卸载模块 | **单进程内不能卸载 DEX**；Web 可以 | Web：`WebView.destroy()` 释放运行时。原生：结束模块进程 | ART 没有卸载已加载 DEX 的 API |
| 从本地存储删除模块 | **能** | 先卸载 → 再删目录 → 刷新列表 | `targetSdk ≥ 34` 要求动态加载文件只读 |

还有一个决定整个方案形态的红线：

> **Google Play 禁止从 Play 以外的来源下载可执行代码（dex / JAR / .so），例外是运行在
> 虚拟机或解释器里的代码（例如 WebView 中的 JavaScript）。**

所以本项目**先做 Web 通道**：合规、低风险、而且和桌面端「Web 模块 + 进程模块」两条通道
一一对应，清单字段与 `hostReady` 握手两端共用。

---

## 1. 已实现：Web 模块通道

### 1.1 生命周期

| 阶段 | 操作 | 代码 |
| --- | --- | --- |
| 导入 | SAF 选文件 → 校验 → 解包到 `filesDir/modules/<id>/` | `MobileModuleStore`、`MobileModuleArchive` |
| 未加载 | 只读清单，页面显示「未加载」 | `InstalledMobileModule` |
| 加载 | 创建 `WebView`，页面常驻内存 | `MobileModuleRuntime.load` |
| 使用 | 页面通过桥请求宿主能力 | `MobileModuleHostBridge` |
| 卸载 | `WebView.destroy()`，释放运行时 | `MobileModuleRuntime.unload` |
| 删除 | 先卸载 → `deleteRecursively()` | `MobileModuleStore.delete` |

**导入不加载，这一点是刻意的**：导入只是把文件复制进私有目录，是否运行由用户单独决定。

### 1.2 模块怎么被服务

模块页面**不落成文件 URL，也不走网络**。宿主用 `WebViewClient.shouldInterceptRequest`
接管 `https://module.qing.local/`：

- 路径映射到 `filesDir/modules/<id>/` 下的文件，越界、符号链接、非法扩展名一律 404/403；
- `/shell/…` 映射到宿主自己的 `assets/shell/`（基础样式表与桥脚本）；
- **其他任何 host 直接 403**，所以模块在构造上就是离线的；
- 返回的 HTML 会被注入主题 CSS、基础样式表和桥脚本，再交给 WebView。

主题以 CSS 自定义属性（`--qing-bg`、`--qing-primary`、`--qing-radius` …）注入，模块因此
不需要知道任何 Android 主题的存在，却能跟随用户选的外观。

### 1.3 能力通道

模块不能直接碰 Android API。能力对齐桌面端语义：**模块声明 → 用户看到 → 宿主代做**。

| 能力 | 方法 | 同步/异步 | 宿主做什么 |
| --- | --- | --- | --- |
| *(无需声明)* | `host.info`、`toast.show` | 同步 | 契约版本、语言、已声明能力 |
| `text.codec` | `text.codec` | 同步 | UTF-8 上的 Base64 / URL 编解码 |
| `device.info` | `device.snapshot` | 同步 | 公开设备与应用属性 |
| `file.hash` | `file.hash` | **异步** | 打开系统选择器，流式计算 MD5/SHA-1/SHA-256 |
| `graphics.qr` | `graphics.qr` | 同步 | 文本 → 二维码 PNG（zxing） |
| `graphics.share` | `graphics.share` | **异步** | 写应用缓存 → 系统分享面板 |
| `clipboard.write` | `clipboard.write` | 同步 | 写剪贴板 |

方法到能力的映射写在 `MobileModuleCapabilities`，**没有在清单里声明的能力会被拒绝并
把错误回给页面**，而不是静默失败。

### 1.4 包格式与校验

```
<id>-<version>.qmod          # zip
├─ manifest.json             # 唯一入口，先读它
└─ web/…                     # 只允许 web/ 下的文件
```

- `payloadHash` = 对除 `manifest.json` 外每个条目按条目名排序后
  `sha256(名称 + "\n" + sha256hex(内容) + "\n")` 再取 SHA-256；
  打包脚本（Python）与内核（Kotlin）必须**逐字节一致**。
- 导入顺序：整包读入内存 → 逐条校验路径/前缀/重复/体积 → 解析清单 → 校验入口存在 →
  校验 `payloadHash` → **全部通过后才落盘**（先写 `.staging-*` 再原子改名）。
- 上限：单包 8 MB、单条目 4 MB、总解包 16 MB、最多 512 个条目。
- 拒绝清单：`../` 之类的越界路径、`web/` 以外的文件（例如 `classes.dex`）、重复条目、
  不认识的 `runtimeType`、不匹配的 `apiVersion`、与内容对不上的 `payloadHash`。

这套规则的回归测试读的就是仓库里真实发布的 `.qmod`
（`PackagedAndroidModulesTest`），因此打包脚本和内核一旦漂移就会红。

### 1.5 界面

- 模块页顶部有搜索框与「全部 / 已加载 / 未加载」筛选；
- titlebar 右上角的 `+` 直接打开系统文件选择器完成导入；
- 详情页在未加载时展示版本、占用空间、运行时与**申请的权限**，并提供「加载模块 / 删除」；
- 已加载时详情页把说明替换成模块本身，只留一条紧凑操作条（卸载 / 删除）。

---

## 2. 尚未实现：原生进程外 DEX 通道

以下只在侧载分发时考虑，**上架 Google Play 就不能做**。技术要点先记录，避免以后重走一遍。

### 2.1 加载进内存

`dalvik.system.InMemoryDexClassLoader`（API 26 起）从 `ByteBuffer` 加载 DEX，模块包可以
在内存里解压后直接加载，不必把 dex 写进存储。

宿主侧的老规矩（桌面端已经踩过）：**接口/契约类必须留在宿主自己的 DEX 里**，模块只实现它。
否则同一个类会被两个类加载器各加载一次，出现 `ClassCastException: X cannot be cast to X`。

### 2.2 卸载：进程即卸载边界

ART 没有「卸载已加载 DEX 或类」的 API。类加载器失去引用后可以被 GC，但已打开的 DEX 与已
解析的类在进程内是「粘住」的 —— ART 需要保留 DEX 以便验证、去优化、读取反射元数据。
Android 14 官方建议要重载一个动态文件就「先删除再重建」，而不是「卸载后重载」。

所以原生通道要按桌面端同构的模型做：**一模块一进程**。

```xml
<service android:name=".module.ModuleRuntimeService" android:process=":module" />
```

两个坑：

1. `stopService()` 不等于卸载，服务停了进程可能还在，必须主动结束进程。
2. 不要用 `android:isolatedProcess="true"`：它的语义是「无任何权限的沙箱」，模块连自己的
   文件都读不了，不适合做功能模块。

### 2.3 存储与删除

模块只能放 App 私有目录（`context.getDir("modules", MODE_PRIVATE)`），放外部公共目录等于
让任何应用都能改模块代码。顺序必须是**停模块进程 → 删除文件 → 清注册表**：Linux 上 unlink
一个正在被使用的文件会「成功」，但 ART 之后可能再次读取该 DEX，文件不在了就会崩。

### 2.4 targetSdk ≥ 34 的额外要求

Android 14 起，动态加载的文件必须标记为只读，否则 `SecurityException: Writable dex file
... is not allowed`。官方写法是「先开流 → 立刻置只读 → 再写内容」，用已打开的 fd 绕过刚被
chmod 掉的写权限。已存在的动态加载文件建议删除重建；若只是补打只读标记，则必须先用可信值
（哈希/签名）校验完整性再加载。

---

## 3. 明确不做的事

- 不做「下载即执行」，不做绕过 Play 政策的自更新。
- 不在单进程里做「假卸载」。
- 不引入热修复框架（Tinker / AndFix 等）当作模块运行时。
- 不把 APK 安装当作模块导入路径（必须弹系统确认、需要 `REQUEST_INSTALL_PACKAGES`、
  且要与宿主同签名，做不到应用内静默导入）。
- 不为了「能力齐全」把 Android API 直接暴露给页面：能力必须先在清单里声明、先被用户看到。
