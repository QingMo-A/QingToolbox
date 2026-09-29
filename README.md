<div align="center">

<img src="QingToolbox.Shell/Assets/Branding/QingToolbox.Mark.svg" alt="QingToolbox" width="112" height="112" />

# QingToolbox Android

**Android 端的轻量模块化工具箱 · Kotlin + Jetpack Compose + Material 3**

宿主保持最小，工具按需以导入模块交付。

[![Version](https://img.shields.io/badge/version-0.1.1--alpha-blue?style=flat-square)](#版本状态)
[![License](https://img.shields.io/github/license/QingMo-A/QingToolbox?style=flat-square&color=green)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Android%208.0%2B-3DDC84?style=flat-square&logo=android&logoColor=white)](#项目简介)

[![Kotlin](https://img.shields.io/badge/Kotlin-2.0.21-7F52FF?style=flat-square&logo=kotlin&logoColor=white)](https://kotlinlang.org/)
[![Compose](https://img.shields.io/badge/Jetpack%20Compose-BOM%202024.09.03-4285F4?style=flat-square&logo=jetpackcompose&logoColor=white)](https://developer.android.com/jetpack/compose)
[![Material 3](https://img.shields.io/badge/Material%203-M3-757575?style=flat-square&logo=materialdesign&logoColor=white)](https://m3.material.io/)
[![AGP](https://img.shields.io/badge/AGP-8.6.1-3DDC84?style=flat-square&logo=gradle&logoColor=white)](https://developer.android.com/build)

[![Unit tests](https://img.shields.io/badge/unit%20tests-79%20passing-brightgreen?style=flat-square)](#测试)
[![Modules](https://img.shields.io/badge/modules-4%20official-8957E5?style=flat-square)](android_modules)
[![Branch](https://img.shields.io/badge/branch-toolbox--android-lightgrey?style=flat-square)](https://github.com/QingMo-A/QingToolbox/tree/toolbox-android)

[![Typing SVG](https://readme-typing-svg.demolab.com?font=JetBrains+Mono&weight=500&size=16&pause=1400&color=3DDC84&center=true&vCenter=true&width=620&lines=Minimal+shell%2C+capability-gated+modules.;Offline+web+modules%2C+no+network+by+construction.;Kotlin+%2B+Compose+M3+for+Android+8.0%2B.)](https://github.com/QingMo-A/QingToolbox)

</div>

> **Alpha 状态提示**
>
> 当前发布为 Alpha，仅供自主开发与受控测试使用，**不上架任何应用商店**。
> APK 使用调试签名，未做代码签名。请只从本仓库自行构建或获取安装包。

> **分支说明**
>
> 本分支（`toolbox-android`）的主线是 **Android 端**。
> Windows 桌面宿主的正式文档位于 [`toolbox` 分支](https://github.com/QingMo-A/QingToolbox/tree/toolbox)；
> 本仓库默认分支是 `toolbox`，因此直接打开仓库首页看到的将是 Windows 版本。
> Windows 宿主的技术说明在本分支归档于 [`docs/WINDOWS_HOST_NOTES.md`](docs/WINDOWS_HOST_NOTES.md)。

---

## 目录

- [项目简介](#项目简介)
- [设计原则](#设计原则)
- [功能特性](#功能特性)
- [架构总览](#架构总览)
- [快速开始](#快速开始)
- [模块体系](#模块体系)
- [QingTransfer](#qingtransfer)
- [项目结构](#项目结构)
- [测试](#测试)
- [版本状态](#版本状态)
- [文档地图](#文档地图)
- [安全模型](#安全模型)
- [参与贡献](#参与贡献)
- [License](#license)

---

## 项目简介

QingToolbox Android 是 QingToolbox 的移动端外壳，与 Windows 桌面宿主遵循同一条产品规则：**宿主不内置任何具体工具，每一个可见工具都以导入模块的形式交付**。

宿主只负责导航框架、主题外观、语言、模块发现与生命周期，以及局域网传输会话；具体能力全部由独立模块提供。模块以 Web 页面运行在受控容器中，只能通过能力桥访问系统，且必须先在清单里声明所需能力——不声明即无调用面。

- 目标平台：Android 8.0+（API 26），编译与目标 SDK 均为 API 35
- 技术栈：Kotlin 2.0.21 + Jetpack Compose（BOM 2024.09.03）+ Material 3
- 构建：AGP 8.6.1，JDK 17
- 模块形态：`.qmod` 包（ZIP 容器，内含 `manifest.json` 与 `web/` 载荷）
- 分发：侧载 APK，**不经过应用商店**

## 设计原则

1. **宿主不内置工具。** 宿主只提供壳、导航、主题与安全边界。
2. **导入不等于执行。** 导入只做校验与落盘，模块以「未加载」状态出现；加载是用户另行做出的决定。
3. **能力白名单。** 模块只能调用清单 `capabilities` 里声明的能力，其余一律拒绝。
4. **默认离线。** 模块只能访问自身包内的资源，向任何其他主机发起的请求都会被拒绝。
5. **窄接口。** 模块无法自行获取系统权限，一切系统访问都经过宿主的能力桥。
6. **诚实的状态。** 未实现的能力在文档中明确标注为「未实现」，不以计划替代事实。

## 功能特性

**外壳**

- 四个平级目的地：首页、模块、设备、设置；系统返回键语义正确。
- 七套外观主题（Qing Default / Neon Circuit / Greenline / Aurora Flow / Qing Nova / Brushed Metal / Aurora），各带独立的圆角、描边与主色梯度。
- 简体中文与英文界面，可跟随系统或手动指定。
- 目的地之间无过渡动画：交叉淡入会在整段动画期间保留上一屏，使切换读起来像卡顿而非精致。
- 所有界面字符串均为资源引用，中英两套语言同步维护（由测试强制）。

**模块运行时**

- 从系统文件选择器导入 `.qmod`，校验清单元数据与载荷摘要后写入应用私有目录。
- 模块列表支持搜索与加载状态筛选；每个模块有详情页，可加载 / 卸载 / 删除。
- 模块作为 Web 页面运行，资源由宿主离线供给，主题变量注入页面。
- 能力桥按清单声明逐项放行；未声明能力与跨主机请求均被拒绝。

**QingTransfer**

- 基于 DNS-SD 的局域网发现，无服务器、无中继、无账号。
- 接收方确认后才开始传输。
- 128 KB 缓冲区流式传输，完成后校验 SHA-256。
- 传输过程支持进度与取消，接收文件可自动归入指定目录。

**设备发现与配对**

- 通过 mDNS 找到局域网内的对端；广告里解析出的端点还必须完成一次 nonce 绑定的非静默 TCP 探测才会进入列表，因此陈旧或伪造的广告不会显示出来。
- 配对要求两端显示同一个 8 位码，并完成一次 Noise 握手才建立关系；码不匹配即中止。
- 配对关系决定后续授权，不只是一个标记。目前只有**亲密**关系且平台为 Windows 的对端才会收到转发的通知。
- 信任记录由 Android Keystore 包裹后落盘，不以明文保存。
- 发现、配对与通知转发共用一个进程级会话，并以 owner 引用计数维持，因此在「设备」页与传输页之间来去不会中断在线状态。

**通知转发**

- 把本机通知转发的目标限定为已配对的亲密 Windows 设备；未配对或非亲密对端无论在附近与否都不会收到。
- 通知读取权限由 Android 系统设置单独授予，**配对本身不隐含该权限**；未授权时「设备」页明确提示并给出跳转入口，而不是静默失败。
- 分组摘要与常驻通知被跳过；标题与正文按上限截断。

## 架构总览

```text
Kotlin 宿主（单 Activity）
├─ MainActivity                    唯一入口，承载 Compose 内容
├─ QingToolboxApp                  四个目的地的导航与页面
│   ├─ QingShellNavigation         导航动画策略（无过渡）
│   ├─ QingComponents              通用组件与图标表面
│   └─ QingAppearanceStyle         各主题的几何与配色
├─ QingToolboxViewModel            单向状态流
└─ 模块宿主
    ├─ MobileModuleStore           导入、扫描、删除
    ├─ MobileModuleRuntime         运行时、离线资源供给、主题注入
    ├─ MobileModuleHostBridge      暴露给模块页的能力桥
    └─ MobileModuleWeb             Compose 侧的模块容器

QingTransfer（设备页）
├─ QingTransferDiscovery           DNS-SD 广告与发现
├─ QingTransferProtocol            帧格式与元数据
├─ QingTransferConnection          连接、传输、SHA-256 校验
└─ QingTransferEndpointProbe       nonce 绑定的端点探测

设备会话（设备页）
├─ DeviceDiscoverySession          进程级会话、owner 引用计数、通知转发入口
├─ DeviceDiscoveryProtocol         mDNS 属性与候选端点格式
├─ DevicePairingSession            8 位码配对、Noise 握手与授权
├─ DevicePairingProtocol           配对帧格式
├─ DevicePairingStore              Keystore 包裹的信任记录与撤销
├─ DeviceLinkService               存在配对时的前台服务
├─ DeviceHubScreen                 设备页界面
└─ DeviceNotificationListener      系统通知读取与转发

模块（每个 `.qmod` 一个 Web 页面）
└─ 只能经由能力桥访问系统，且仅限清单已声明项
```

数据流的关键约束：

- 模块清单的 `capabilities` 数组是白名单，未声明即拒绝，无隐式默认。
- 模块页面只能读取自身包内资源；跨主机请求被拒绝，因此模块**在结构上离线**。
- 模块无法自行获取文件系统或网络权限，一切系统访问都经过宿主的能力桥。
- 权限声明用于告知与约束调用面，**不构成强制沙箱**。

## 快速开始

### 使用者

本项目不上架应用商店。构建或获取 APK 后侧载安装：

```bash
adb install -r app/build/outputs/apk/debug/build-<yyyyMMdd-HHmmss>.apk
```

首次启动后进入「模块」页面，用顶栏的 `+` 导入 `.qmod` 模块包。导入与刷新只发现并校验清单，**只有**用户主动点击「加载」后模块才会运行。官方模块包见 [`android_modules/`](android_modules)。

### 开发环境

需要 JDK 17、Android SDK Platform 35 与 build tools，并在 `QingToolbox.Android/local.properties` 中指向 SDK：

```properties
sdk.dir=C:/Android
```

本仓库**没有 Gradle wrapper**，因此需要 Gradle 8.8 及以上在 `PATH` 中（或自备 wrapper）。从 `QingToolbox.Android/` 目录执行：

```bash
# 单元测试
gradle :app:testDebugUnitTest

# 构建调试 APK
gradle :app:assembleDebug

# 构建并安装到已连接设备
gradle :app:assembleDebug
adb install -r app/build/outputs/apk/debug/build-<时间戳>.apk
```

产物会写两份：无时间戳的 `app-debug.apk`，以及 `build-<yyyyMMdd-HHmmss>.apk`。**应当保留的是带时间戳那份**——当设备上同时存在多个包时，只有它能把两次构建区分开。

## 模块体系

官方 Android 模块位于 [`android_modules/`](android_modules)，均为 Web 运行时、独立能力边界：

| 模块 | 模块 ID | 版本 | 能力边界 |
| --- | --- | --- | --- |
| 文本编解码 / Text Codec | `qing.text-codec` | 0.1.0 | `text.codec`、`clipboard.write` |
| 设备信息 / Device Info | `qing.device-info` | 0.1.0 | `device.info`、`clipboard.write` |
| 二维码 / QR Code | `qing.qr-code` | 0.1.0 | `graphics.qr`、`graphics.share`、`clipboard.write` |
| 文件哈希 / File Hash | `qing.file-hash` | 0.1.0 | `file.hash`、`clipboard.write` |

当前支持的能力名：

| 能力 | 含义 |
| --- | --- |
| `text.codec` | 文本编解码 |
| `device.info` | 读取公开的机型与系统信息 |
| `file.hash` | 计算文件摘要（异步） |
| `graphics.qr` | 生成二维码 |
| `graphics.share` | 调起系统分享（异步） |
| `clipboard.write` | 写入剪贴板 |

`host.info` 与 `toast.show` 对模块始终可用，无需声明。

模块导入流程：

1. 打开应用，进入「模块」页面。
2. 点顶栏的 `+`，打开系统文件选择器。
3. 选中 `.qmod` 文件。它会先被校验，再复制进应用私有存储。
4. 模块以「未加载」状态出现在列表里，此时不会执行任何代码。
5. 需要使用时，在模块详情页主动点「加载」。

包内另有 [`android_modules/index.json`](android_modules/index.json) 目录文件，记录每个包的 id、版本、能力、体积、整包 SHA-256 与载荷哈希。**宿主不读取它**——它的用途是让镜像、脚本或人可以独立校验下载到的东西。

## QingTransfer

「设备」目的地承载两件事：**设备发现与配对**（见上方[功能特性](#功能特性)）以及 QingTransfer 的局域网文件传输会话。本节只说明传输。

传输的当前行为：

- 设备通过 DNS-SD 在局域网内互相广告与发现，无服务器、无中继、无账号。
- 连接由接收方确认后才开始传输。
- 文件通过 socket 以 128 KB 缓冲区流式传输，完成后校验 SHA-256。
- 传输过程提供进度与取消，接收文件可自动归入选定目录。
- 接收偏好（默认目录、是否自动接收）保存在本机，并做可写性检查。

协议中带有平台字段，取值接受 `windows` 与 `android`。Android↔Android 传输可用；**Android 与 Windows 之间的文件传输当前不可用**，因为传输的 Windows 端仍未接通。设备发现、配对与通知转发不受此限制：Android 可以与已配对的 Windows 电脑互认身份并把本机通知转发过去。

## 项目结构

```text
QingToolbox/
├─ QingToolbox.Android/            # Android 宿主（本分支主线）
│  ├─ app/src/main/java/com/qingtoolbox/android/
│  │  ├─ MainActivity.kt           唯一 Activity
│  │  ├─ QingToolboxApp.kt         目的地导航与全部页面
│  │  ├─ QingShellNavigation.kt    导航动画策略
│  │  ├─ QingComponents.kt         通用 UI 组件
│  │  ├─ QingAppearanceStyle.kt    各主题的几何与配色定义
│  │  ├─ MobileModule*.kt          模块清单、存储、运行时、能力桥
│  │  └─ QingTransfer*.kt          发现、协议、连接、元数据、会话
│  ├─ app/src/main/assets/shell/   注入到每个模块页的宿主资源
│  ├─ app/src/test/                单元测试（19 个测试类）
│  └─ docs/                        移动端专项文档
├─ android_modules/                # 官方 Android 模块包（.qmod）与目录
├─ plans/                          # 编号实现计划索引
├─ docs/                           # 文档（含 Windows 宿主归档说明）
├─ QingToolbox.Shell/  QingToolbox.Core/  QingToolbox.Abstractions/
├─ QingToolbox.ModuleHost/  QingToolbox.ModuleLoader/
└─ QingToolbox.DevTools.*/         # 独立开发验证工具（非应用运行时组成部分）
```

`QingToolbox.Shell` 及其后的项目属于 **Windows 宿主**，不参与 Android 构建。它们保留在本分支是因为两条线共享品牌资产与模块格式约定；Android 宿主不会引用或加载它们。

## 测试

从 `QingToolbox.Android/` 执行：

```bash
gradle :app:testDebugUnitTest
```

当前 **72 个用例，19 个测试类**，全部通过。覆盖范围：

| 领域 | 测试类 |
| --- | --- |
| 模块包契约与清单校验 | `MobileModulePackageTest`、`PackagedAndroidModulesTest` |
| 模块搜索与筛选 | `MobileModuleQueryTest` |
| 能力规则 | `MobileModulePackageTest` |
| 零依赖 JSON 读写 | `MobileJsonTest` |
| QingTransfer 协议与元数据 | `QingTransferProtocolTest`、`QingTransferMetadataTest` |
| 连接状态与端点探测 | `QingTransferConnectionStateTest`、`QingTransferEndpointProbeTest` |
| 接收偏好 | `QingTransferReceivePreferencesTest` |
| 资源完整性（中英同步） | `ResourceCompletenessTest` |
| 启动契约与导航策略 | `StartupContractTest`、`QingShellNavigationTest` |
| 工具类 | `TextCodecTest`、`FileHashDigestTest`、`QrCodeEncoderTest`、`DeviceInfoTest`、`AppLanguageTest`、`AppearanceThemeTest` |

其中 `PackagedAndroidModulesTest` 会读取 `android_modules/` 里**真实的模块包**，因此 Python 打包器与 Kotlin 导入器是互相对照验证的，而不只是各测各的。

## 版本状态

| 项目 | 值 |
| --- | --- |
| 版本名 | `0.1.1-alpha` |
| 版本号 | `2` |
| minSdk / targetSdk | 26 / 35 |
| compileSdk | 35 |
| 分发方式 | 侧载 APK（**不上架应用商店**） |

**已实现**

- 四目的地导航与正确的系统返回键处理
- 经系统文件选择器的模块导入、校验与私有目录存储
- 模块列表的搜索与加载状态筛选
- 模块详情页的加载 / 卸载 / 删除
- 离线 Web 模块运行时、资源供给与能力桥
- 七套外观主题与中英双语
- 两台 Android 设备间的 QingTransfer：发现、确认、流式传输、SHA-256 校验、进度与取消
- 局域网设备发现与配对：mDNS 发现，配对要求两端显示同一个 8 位码并完成 Noise 握手，信任记录由 Android Keystore 包裹后落盘
- 把本机通知转发到已配对的亲密 Windows 设备；通知读取权限由系统设置单独授予，配对本身不隐含该权限
- 存在配对关系时以前台服务维持设备会话，进出「设备」页与传输页不会中断在线状态

**尚未实现**

- QingTransfer 的 Windows 端点——通知可以转发到电脑，但 Android↔Windows 的**文件传输**当前不可用
- 原生（进程外 DEX）模块通道
- 模块更新、云端或账号同步、远程控制
- 通知转发的开机自启：当前只在 App 启动时检查配对并起前台服务
- Root 或 hook 框架类能力

## 文档地图

| 文档 | 状态 | 说明 |
| --- | --- | --- |
| [`QingToolbox.Android/README.md`](QingToolbox.Android/README.md) | 当前 | Android 宿主详细说明：文件职责、构建步骤、测试明细。 |
| [`QingToolbox.Android/docs/MOBILE_MODULE_RUNTIME.md`](QingToolbox.Android/docs/MOBILE_MODULE_RUNTIME.md) | 当前 | 移动端模块运行时：离线资源供给、能力桥边界、主题注入。 |
| [`android_modules/README.md`](android_modules/README.md) | 当前 | 官方 Android 模块包清单、导入步骤与校验方式。 |
| [`docs/releases/`](docs/releases/) | 当前 | 逐版本发布说明；Android 构建见 [`android-0.1.1-alpha.md`](docs/releases/android-0.1.1-alpha.md)。 |
| [`docs/WINDOWS_HOST_NOTES.md`](docs/WINDOWS_HOST_NOTES.md) | 归档 | Windows 宿主技术说明。**不要**作为 Android 开发依据。 |
| [`plans/README.md`](plans/README.md) | 当前 | 编号实现计划索引，含移动端壳层路线。 |

### 相关分支

| 分支 | 内容 |
| --- | --- |
| `toolbox`（默认分支） | Windows 宿主：Rust/Tauri 2/Vue 3 与历史 WPF 路径。 |
| `toolbox-android`（本分支） | Android 宿主主线。 |
| `android_modules` | 官方 Android 模块源码与打包工具。 |
| `modules` | 历史 WPF 进程内模块源码与模板。 |

## 安全模型

**已实现**

- 模块清单的 `capabilities` 数组是白名单，未声明即拒绝调用。
- 模块页面只能读取自身包内资源，跨主机请求被拒绝，因此模块在结构上离线。
- 导入时校验清单元数据与载荷摘要，包写入应用私有目录。
- 模块无法自行获取系统权限，全部系统访问经宿主能力桥转发。

**明确不是安全机制的部分**

- 能力声明用于约束调用面与告知用户，**不构成强制沙箱**。
- `.qmod` 不支持包签名，因此只应导入可信来源的模块。
- APK 使用调试签名，仅供自主开发与受控设备安装。

## 参与贡献

- 分支纪律：`toolbox-android` 承载 Android 宿主；`android_modules` 承载模块源码与打包工具。两条线独立维护，宿主提交不得顺手修改模块，模块变更也不得未经审查混入宿主提交。
- 提交信息：使用 `[ui]` / `[docs]` / `[build]` / `[feat]` / `[fix]` / `[test]` 标签，正文用完整句子说明**为什么**改。
- 验证要求：改 UI 必须在真机或模拟器上确认启动无崩溃；改资源必须同时更新中英两套字符串（由 `ResourceCompletenessTest` 强制）。

## License

QingToolbox 使用 [MIT License](LICENSE)。

---

## Star 趋势

<a href="https://star-history.com/#QingMo-A/QingToolbox&Date">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=QingMo-A/QingToolbox&type=Date&theme=dark" />
    <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=QingMo-A/QingToolbox&type=Date" />
    <img alt="Star History Chart" src="https://api.star-history.com/svg?repos=QingMo-A/QingToolbox&type=Date" width="640" />
  </picture>
</a>

<br />

![Visitors](https://komarev.com/ghpvc/?username=QingMo-A&repo=QingToolbox&label=visitors&color=3DDC84&style=flat-square)

<!--
  上面两组图表由第三方服务渲染（star-history.com / komarev.com）。
  在部分网络环境下可能加载较慢或被拦截；如影响阅读可直接删除本节。
  徽章使用 shields.io，若需离线文档体验也可整体替换为静态文本。
-->

---

<details>
<summary><b>English overview</b></summary>

<br />

QingToolbox Android is the mobile shell of QingToolbox, an Android app built on Kotlin 2.0.21 with Jetpack Compose and Material 3. It follows the same product rule as the Windows desktop host: the shell ships no tools of its own, and every visible capability arrives as an imported module.

The shell owns four destinations — Home, Modules, Devices and Settings — plus theming, language and the module lifecycle. Modules are `.qmod` packages: a plain zip holding `manifest.json` and a `web/` payload. Importing verifies and copies the package into app-private storage and lists it as **not loaded**; loading is a separate decision the user makes on the module page.

A module runs as a web page served offline by the shell and reaches the system only through a capability bridge. It declares what it needs in its manifest, and anything undeclared is refused. Requests to any host other than the module's own package are refused too, so a module is offline by construction.

- Target platform: Android 8.0+ (API 26), compiled and targeted at API 35
- Stack: Kotlin 2.0.21 + Jetpack Compose (BOM 2024.09.03) + Material 3
- Module format: `.qmod` (ZIP container, unsigned)
- Distribution: sideloaded APK; **not published to any app store**

The Devices destination runs QingTransfer, a LAN file transfer between two Android devices using DNS-SD discovery, receiver approval, streaming transfer with SHA-256 verification, and progress and cancel. The protocol carries a platform field but **only the Android end is implemented** — there is no Windows endpoint yet.

This branch, `toolbox-android`, carries the Android product line. The Windows desktop host lives on the `toolbox` branch, which is also this repository's default branch. Windows host notes are archived here at [`docs/WINDOWS_HOST_NOTES.md`](docs/WINDOWS_HOST_NOTES.md).

This is an Alpha build for autonomous development and controlled testing, not a production release.

</details>
