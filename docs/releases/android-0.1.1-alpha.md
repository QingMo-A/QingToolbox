# QingToolbox Android 0.1.1-alpha

The first published Android build of QingToolbox, after the internal `0.1.0-alpha` (versionCode 1) iteration. It is a Kotlin + Jetpack Compose + Material 3 shell for Android 8.0 (API 26) and later, and it follows the same product rule as the desktop host: the shell ships no tool of its own, and every visible tool arrives as an imported module.

The APK is **debug-signed** and delivered by sideload. It is not code-signed and is not submitted to any app store. Build it from source or take it from this Release, and verify the supplied SHA256 sidecar.

## What's new

- **Local-network device discovery.** Peers are found over mDNS. An endpoint resolved from an advertisement must still answer a nonce-bound, non-silent TCP probe before it is listed, so a stale or forged advertisement never reaches the devices page.
- **Pairing.** Both sides must show the same eight-digit code and complete a Noise handshake before a relationship exists, and that relationship decides later authorisation instead of acting as a label. Trust records are wrapped with the Android Keystore rather than stored in clear text.
- **Notification forwarding to a paired Windows PC.** Only a peer whose relationship is **Intimate** and whose platform is `windows` receives forwarded notifications; group summaries and ongoing notifications are skipped, and title and body are truncated to a bound. Notification access is granted by Android's settings and is deliberately **not** implied by pairing, so the devices page says so and offers the settings entry instead of failing silently. See the limitation below before relying on this.
- **A foreground device service.** Discovery, pairing and forwarding share one process-wide session kept alive by owner reference counting, so the service stays online while the devices page and the transfer page come and go.
- **Two more appearances** — Brushed Metal and Aurora — for seven presets in total.
- A settings page with a section structure instead of three bare rows.
- Transfer actions moved into the top bar.
- Fixed a light inner rectangle that appeared inside gradient buttons, and fixed the crash on launch plus the delay when switching destinations.

## Install

```bash
adb install -r QingToolbox-0.1.1-alpha-android-debug.apk
```

The shell starts with no tool of its own. Import `.qmod` packages from `android_modules/` on the `toolbox-android` branch: importing only validates and stores a package in app-private storage, and a module runs only after you load it from the modules page.

## Known limitations

- **File transfer between Android and Windows is not available.** The transfer protocol carries a `windows` platform field, but only the Android side is implemented, so Android↔Android transfer is the supported path.
- **Notification forwarding is not yet reliable.** The transfer page starts and stops the shared discovery session directly instead of acquiring and releasing it as an owner, so once that page has stopped the session, the online peer list it forwards against is empty and forwarding stops until the app is restarted. Device discovery and pairing themselves are unaffected.
- Notification forwarding has no boot-time startup: the shell looks for an existing pairing when it starts and starts the foreground service then.
- The APK is debug-signed and is not code-signed.

## 简体中文

`0.1.1-alpha` 是 QingToolbox 的首个公开发布 Android 构建，承接内部的 `0.1.0-alpha`（versionCode 1）。它是面向 Android 8.0（API 26）及以上、基于 Kotlin + Jetpack Compose + Material 3 的宿主，并遵循与桌面宿主相同的产品规则：宿主不内置任何工具，一切可见工具都以导入模块交付。

APK 使用**调试签名**、只侧载，未做代码签名，也不上架任何应用商店。请自行构建或从本 Release 获取，并核对同名 SHA256。

本版本新增：局域网设备发现（mDNS 发现之外，广告里解析出的端点还必须通过一次 nonce 绑定的非静默 TCP 探测才会进入列表，因此陈旧或伪造的广告不会显示）；设备配对（两端显示同一个 8 位码并完成 Noise 握手，关系决定后续授权，信任记录由 Android Keystore 包裹）；把本机通知转发给已配对的亲密 Windows 设备（通知读取权限由系统设置单独授予，配对本身不隐含该权限）；存在配对时以前台服务维持设备会话；新增 Brushed Metal 与 Aurora 两套外观（共七套）；设置页改为分区结构；传输动作移入顶栏；修复渐变按钮内部的浅色内矩形、启动崩溃与切换目的地的延迟。

已知限制：Android↔Windows 的**文件传输**当前不可用（协议带 `windows` 平台字段，但只实现了 Android 一端）；**通知转发尚不可靠**——传输页直接启停共享的发现会话，而没有按 owner 引用计数获取与释放，因此该页停止会话后，转发所依据的在线设备列表为空，转发会中止直到重启 App；Android↔Android 传输是可用的路径。APK 为调试签名，未做代码签名。
