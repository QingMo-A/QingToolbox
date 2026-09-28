# QingToolbox PC ↔ Android 设备通信协议与授权规则（DCP/1 草案）

状态：**设计草案，不是“完整实现”或“已发布”的声明。** Windows 与 Android 开发版已有局域网发现、首次配对和 `QDM1` 已配对设备操作实现；跨平台双方确认、离线撤销同步与电量周期发送仍需真实双端验收。统一文件传输、远控及通用 `QDS1` 会话尚未实现。标为“拟定”的部分须先做双端互操作与安全测试，再冻结为正式 wire contract。

相关产品边界见 [跨设备规划](QINGTRANSFER_CROSS_DEVICE_PLAN.md)。本文只讨论同一局域网内的 PC/Android、PC/PC、Android/Android 四种对等路径；不设计云账号、互联网中继或穿透。文中的“必须/不得”是拟定协议的约束，“建议”允许实现选择，但不得削弱授权与安全边界。

## 1. 核心原则与术语

1. **对等设备**：Windows 与 Android 都可以发起请求，也都可以作为接收端。通信角色只有“本次连接发起端/接受端”“数据发送端/接收端”“控制端/被控端”，不能固定为“PC 是服务器”。
2. **发现 ≠ 身份**：mDNS 名称、IP、端口、平台字段和 nonce 回显只说明当前有一个能响应的候选端点，不证明它是此前配对的设备。只有加密握手中验证的静态公钥可作为设备身份。
3. **配对 ≠ 授权**：配对只把对端公钥加入本机信任记录，默认关系为“连接设备”。传文件、看屏幕、发送输入、读电量与通知分别决策，不能因为已配对而默许所有能力。
4. **亲密关系须双端同意**：首次配对后双方仅为“连接设备”；升级为亲密关系必须在已认证连接上经两端分别同意，并在两端记录成功后通知双方。发现、在线与关系仍是独立状态。离线断开先撤销本机信任，持久保存待同步撤销；对端再次出现时经身份验证同步撤销并提示。在线请求断开须两端同意。
5. **本地决定**：接收目录、系统权限、共享开关、会话批准均由提供资源的设备决定。Vue 只提出有限类型的操作请求；文件系统路径和原生权限只由本机后端持有。
6. **版本隔离**：设备线协议 `DCP/1` 与模块 `apiVersion: 1` 是两个独立版本号。旧 QingTransfer 的握手和“探测成功”不得自动升级成 DCP 的可信配对。

术语：`discoveryId` 为每次安装生成的 16 字节随机公开标识（小写 32 位十六进制）；`peerId` 为对端静态公钥的指纹/完整编码，用于本机已配对记录；`sessionId` 为单次连接或操作的随机标识。`discoveryId` 不是密钥，IP 也不是持久身份。**当前 Windows `peerId` 实际使用静态公钥的完整十六进制值；指纹显示方式尚未冻结，wire 中不得把截短显示值作为鉴权输入。**

## 2. 分层与状态机

```text
未开启发现 ──用户开启──> 发现中 ──mDNS + 活性探测──> 陌生候选
                                              │
                                        请求/接受配对
                                              ↓
                               加密握手 → 两端核对码并各自确认
                                              ↓
                                   已配对 · 连接设备
                                              │ 本机手动提升/降级
                                              ↓
                                   已配对 · 亲密设备

已配对设备再次通信：发现或手动地址 → 新加密会话 → 核对已存公钥
                                    → 协商能力 → 按能力分别申请授权
```

关闭发现时应停止广播、侦听和未完成的配对；已建立的高权限会话也必须结束，除非未来明确增加一个与“发现”分离的“仅保持当前会话”开关。网络断开、进程退出或系统撤权后，远控、输入、通知推送应立即停止；恢复网络不自动恢复远控。设备列表离线不删除配对记录，但不得把旧在线状态继续显示为在线。

## 3. 发现层：当前 Windows v1 与 Android 互通目标

当前 Windows 使用 DNS-SD/mDNS 服务类型 `_qingdevice._tcp.local.`，TXT 字段如下。Android 应用需要先实现同一服务注册/发现格式，不能另起不兼容的手机专用服务。DNS-SD 只提供候选地址；Android 侧可用系统 `NsdManager`，并按实际版本处理组播和局域网权限。[RFC 6763](https://www.rfc-editor.org/rfc/rfc6763)、[Android NSD](https://developer.android.com/develop/connectivity/wifi/use-nsd)、[NsdManager](https://developer.android.com/reference/android/net/nsd/NsdManager)。

| 字段 | 当前值/约束 | 含义 |
| --- | --- | --- |
| `v` | `1` | 发现格式主版本；未知值不加入附近列表 |
| `id` | 小写 32 位十六进制 | 公开 `discoveryId`，仅用于自我过滤和候选匹配 |
| `name` | 去控制字符、最多 64 字符 | 可显示昵称，不可信 |
| `pf` | `windows` 或 `android` | UI 提示平台，不是权限依据 |
| 端口 | mDNS SRV 中的 TCP 端口 | 只在本次发现有效；不可当作身份 |

候选端点必须通过当前活性探测后才显示：连接该 TCP 端口，发送 ASCII `QDB1` + 16 字节随机 nonce；读取并验证响应的前 36 字节为 ASCII `QDA1` + 原 nonce + 16 字节 `discoveryId`。Windows 当前探测超时约 400 ms、约每 4 秒重检并移除僵尸候选；Android 侧可以使用更符合省电策略的节流，但显示在线前必须再验证一次响应。该探测**不具备认证能力**；任何设备都可模仿响应。当前 Windows 最多保留 64 个候选、每条最多尝试 8 个地址。Android `NsdManager` 的 API 服务类型参数可能采用不带 `.local.` 的 `_qingdevice._tcp`，但其 DNS-SD wire 服务类型必须与 PC 一致。

手动地址/二维码回退只提供连接入口：二维码最多包含短期有效的地址、端口、发现 ID 与协议版本，不得包含私钥、永久授权令牌或“已配对”声明。扫码后仍须人工核对配对码，或在已配对场景核对存储的公钥；地址变化本身不撤销配对，公钥变化必须拒绝并重新走用户可见的配对流程。

局域网权限被拒绝、Wi-Fi 不支持组播、路由器隔离客户端或防火墙阻断时，应显示可理解的故障与手动连接入口；不得把“搜不到”解释成“对端未安装”。Android 新版本的局域网权限规则随目标 SDK 变化，必须以系统实际授予为准，不能静默绕开。[Android 局域网权限](https://developer.android.com/privacy-and-security/local-network-permission)。

## 4. 首次配对：现有 Windows v1 的精确互通要求

这是 Android 首先需要兼容的部分；变动任何字节级规则都必须升版本，不能让两端“看起来发现了”却用不兼容的握手。

| 步骤 | 当前 wire/行为 |
| --- | --- |
| 连接前缀 | 发起端在 TCP 流开头发送 4 字节 ASCII `QDP1` |
| 握手算法 | `Noise_XX_25519_ChaChaPoly_BLAKE2s`，prologue 为 UTF-8 `QingToolbox device pairing v1` |
| 帧 | 每条 Noise 消息前有 2 字节大端长度；长度为 `1..1024` 字节，否则立即拒绝 |
| XX 消息 | 发起端第 1 条 payload 为空；接受端第 2 条和发起端第 3 条 payload 为 UTF-8 JSON `PairHello` |
| `PairHello` | `{"discoveryId":"<32位小写hex>","name":"<昵称>","platform":"windows|android"}`；Android **必须**使用 `android`，现有 PC 发送 `windows` |
| 校验码 | 对 Noise handshake hash 计算 `SHA-256("QingToolbox device pairing code v1" || hash)`，取摘要前 8 字节大端无符号整数 `% 100000000`，补零显示为 8 位十进制；两端必须显示同一值 |
| 人工确认 | 双方核对码并分别点击同意；在 Noise transport 中发送加密单字节 `A` 或 `R`；任一端拒绝/超时均不保存配对 |
| 超时 | 当前握手读写约 5 秒，双方人工确认最多约 90 秒；不得无限挂起 |

Noise XX 的消息顺序与 AEAD 语义以 [Noise 官方规范](https://noiseprotocol.org/noise.html)为准。发起端必须核对握手中的 `discoveryId` 与原候选一致；两端拒绝本机 `discoveryId`、无效平台/昵称、重复配对，以及“同一发现 ID 换了静态公钥”的情况。校验码只在用户**肉眼比较且两端均确认**时防中间人；不应在日志中记录私钥、Noise payload 或验证码。配对成功后双方分别保存对端完整静态公钥，并各自建立 `Connected` 关系。只点一端确认、对端断开或超时均不能留下半完成的持久记录。

Windows 现状用 DPAPI 保护本机静态私钥；Android 方案需要以 Android Keystore 支持的密钥保护材料，例如用 Keystore 密钥加密/封装应用生成的 Noise 私钥。**不得直接声称所有 Android 版本都可把 X25519 私钥作为不可导出的硬件密钥**；设备与系统支持差异须真机验证，并在威胁模型中区分“Keystore 中的封装密钥”和“握手时进入进程内存的 Noise 私钥”。重装或丢失私钥后旧配对失效，不自动认回旧设备。[Android Keystore](https://developer.android.com/privacy-and-security/keystore)。

## 5. 已配对设备的新会话：DCP/1 拟定

当前 Windows 代码**尚未实现**本节。它将是文件、远控与状态共享的唯一入口；旧模块的明文 TCP 或仅靠探测的连接不能进入此层。

### 5.0 当前开发版的过渡设备操作 `QDM1`

`QDM1` 是已配对关系管理和手机电量的**窄范围过渡协议**，不作为文件或远控通道。TCP 连接以 ASCII `QDM1` 开始，随后进行 `Noise_XX_25519_ChaChaPoly_BLAKE2s`，prologue 为 UTF-8 `QingToolbox device management v1`；握手帧、Hello 与 `QDP1` 同形。双方在解密任何操作前核对对端完整静态公钥及 `discoveryId` 与本机配对记录或待同步撤销记录一致。加密控制帧仍为 2 字节大端长度，最大 1024 字节。

请求 JSON 为 `{"version":1,"action":"upgrade|demote|disconnect|disconnectNotice|battery|ping"}`；`battery` 另带 `percent:0..100`、`charging:boolean`。`upgrade`、`demote`、在线 `disconnect` 要求收到方在界面同意，随后发送加密 `A`（或拒绝 `R`）；发起端发送 `C`，接收端落盘并回 `D`，发起端收到 `D` 后落盘。完成时双方提示。`disconnectNotice` 用于离线时本机已撤销的记录：重遇设备时验证双方静态公钥后自动同步撤销并提示对方，不要求对方再批准本机撤销；发送方收到 `D` 才清除持久待同步记录。`battery` 只允许双方本机记录均为亲密设备时发送/接收，接收端回复 `D`；手机在设备互联服务运行且对端可达时约每 60 秒发送一次，超过 130 秒的读数视为过期。`ping` 只对已配对且密钥匹配的设备返回加密 `D`，不弹出授权框。PC 和 Android 发现已配对的连接或亲密设备后立即发起加密握手，之后约每 2 分钟复验一次；失败时约 15 秒后重试。双方仅在最近 3 分钟通过认证握手时显示“在线”，不得仅凭 mDNS 探测认定可信在线。未被发现、网络不可达的设备无法即时收到上线通知。

Android 通知转发使用同一已认证的 `QDM1` 通道，新增 `action: "notification"` 与 `appName`、`title`、`body` 文本字段；仍受 1024 字节加密帧上限约束，超长文本在手机端截断。手机只向本机记录为亲密、平台为 Windows、且该电脑的 `forwardNotifications` 开关已开启的可达设备发送；Windows 只接受其本机记录为亲密 Android 设备发来的通知，回加密 `D`。系统通知读取必须由用户在 Android 系统设置中明确授权。每台电脑的开关随手机配对记录持久保存，默认开启；降级为连接设备或断开后不再发送。通知内容不写入配对记录、设置、日志或磁盘，PC 仅保留有界的短时内存展示队列。若手机工具箱/设备互联服务未运行、授权关闭或对端离线，本功能不补发历史消息。

这个双端确认流程仍可能在“接收端落盘成功、最终 `D` 丢失”时产生暂时不一致。断开时双方先持久保存撤销记录，再清除本机配对；收到 `D` 之前断线可由已确认端主动同步撤销，原发起端也可以重试同一断开。升级/降级可由发起端重试，并要求接收端重新明确同意；未取得双端完成确认前，不得把另一端状态当成已授权。撤销同步的最终回执再次丢失时，可能只剩一侧显示待同步记录，但双方仍须保持不信任。发布前需补充断线注入与跨语言 golden vectors，并验证 Android 前台设备互联服务在目标系统上的通知、权限与停止行为。它不等于下面拟定的通用 `QDS1` 会话。

1. 新 TCP 连接以 ASCII `QDS1` 开始，和 `QDB1` 探测、`QDP1` 首次配对明确分流。端口可以共用，处理器不得混用权限状态。
2. 每次会话使用新鲜临时密钥执行 `Noise_XX_25519_ChaChaPoly_BLAKE2s`，prologue 独立为 `QingToolbox device session v1`。XX 握手仍使用第 2/3 条消息交换有界的 `SessionHello`（协议主/次版本、`discoveryId`、16 字节随机挑战）；第 1 条 payload 为空。
3. 握手结束后，在**任何能力消息或私有元数据**发送前，双方必须把从 Noise 得到的对端**完整静态公钥**与本机配对记录做常量时间比较；未配对、已撤销或密钥变化立即断开。`name`、`platform`、IP 不参与这项判断。
4. 发起端在加密 `session.hello` 中生成随机 `sessionId`，接受端在响应中原样回显；两端同时给出 `major=1`、`minor=0`、可提供的能力名列表及各自版本。主版本不一致立即返回 `INCOMPATIBLE_VERSION` 后断开；小版本取双方交集，未知能力不启用。不得降级到旧 QingTransfer 明文协议。
5. 每条连接使用新的 Noise transport cipher state；应用层还要检查会话 ID、方向内严格递增的消息序号与请求关联 ID。连接断开即销毁会话密钥，不缓存“已批准远控”以便下次静默恢复。Noise 已有逐方向 AEAD nonce，此处的消息序号主要用于业务去重、审计和误路由防护，不能取代加密验证。

### 5.1 拟定加密帧

`QDS1` 的握手帧继续使用 2 字节大端长度，长度限制 `1..1024`。进入 Noise transport 后，每条记录为 `u16_be(ciphertext_len) || NoiseCiphertext`；长度必须为 `1..61440`，超出即断开。解密后的明文结构如下，所有整数大端：

```text
kind: u8            // 1=CONTROL_JSON，2=FILE_CHUNK；其他值在 DCP/1.0 中拒绝
flags: u8           // DCP/1.0 固定为 0
streamId: u32       // 0=控制流；文件流使用本连接内非零 ID
seq: u64            // 各方向从 0 开始严格递增
payload: bytes      // 控制流为 UTF-8 JSON；文件流为二进制
```

控制 JSON 最多 16 KiB，重复 JSON 键、超长字符串和不在 schema 内的必填字段应拒绝；文件 chunk 最多 48 KiB。所有输入先验证长度再分配内存。控制消息公共字段：`type`、`requestId`（随机且本会话唯一）、`sessionId`、`payload`；响应另带 `replyTo` 和 `status`。未知 `type` 返回 `UNSUPPORTED_MESSAGE`，不执行。`streamId` 仅在后端创建的授权操作表中解析；远端不能自选本机文件句柄或系统对象。**上述帧格式是提案，尚无双端 golden vectors，不应在未冻结前宣称 Android 兼容。**

### 5.2 能力名与最小消息集

| 能力 | 主要消息 | 规则 |
| --- | --- | --- |
| `file.v1` | `file.offer` / `accept` / `reject` / `begin` / `finish` / `receipt` / `cancel` | 每次接收由接收端决定；传输进度只对本次授权可见 |
| `battery.v1` | `battery.subscribe` / `snapshot` / `unsubscribe` | 提供端逐设备检查共享开关，不因订阅请求自动开启 |
| `notification.v1` | `notification.subscribe` / `summary` / `otp` / `revoke` | 独立系统权限与逐设备授权；验证码受系统限制时不伪造 |
| `remote.view.v1` | `remote.request` / `approve` / `reject` / `start` / `stop` | 被控端每次显式确认观看权限与显示屏/区域 |
| `remote.input.v1` | `remote.upgrade` / `approve` / `input` / `stop` | 发送输入需独立批准，不能从“可观看”推导 |

能力列表只说明代码能处理对应消息，**不代表当前系统权限已授予或此对端已被授权**。请求若缺少能力交集应返回 `UNSUPPORTED_CAPABILITY`，缺本地授权应返回 `PERMISSION_DENIED`，不做静默降级。

## 6. 本机权限与三种关系

| 关系 | 发现阶段 | 文件接收 | 电量/通知 | 远控观看/输入 |
| --- | --- | --- | --- | --- |
| 陌生 | 仅公开昵称、平台和配对入口 | 禁止 | 禁止 | 禁止 |
| 连接 | 已验证公钥，显示在线状态 | 默认逐次确认 | 默认关闭；由数据所属端逐台打开，OTP 再单独开启 | 每次由被控端确认，观看和输入分别授权 |
| 亲密 | 已配对设备经双方同意后升级 | 首期仍逐次确认；自动接收需未来独立开关 | 电量可按约 1 分钟发送；通知摘要仍需后续实现、系统权限及独立开关 | 首期仍逐次确认；不得无人值守控制 Android |

将设备降级、撤销配对或关闭某项共享时，后端必须立即停止相应推送/会话并丢弃远端缓存；只更新 UI 标签不算撤权。配对后只能默认 `Connected`；“同 Wi-Fi”“同电脑名”或“同账号（未来可能存在）”都不能自动成为 `Intimate`。升级亲密必须双方同意；拒绝、超时和权限撤销应当是明确状态，不能显示成“网络错误”后继续重试高权限请求。

## 7. 文件传输 `file.v1`

1. 发送端通过本机文件选择器取得文件/文件夹。`file.offer` 只包含 `transferId`、条目数量、每条类型、相对路径和字节大小、总字节数等元数据；不发送本机绝对路径。接收端展示来源设备、数量、总量和目的地，由本机用户接受或拒绝。
2. 接收目标：Windows 使用本机选择的目录；Android 使用 Storage Access Framework 取得的 URI/目录授权。对端不得指定 `C:\...`、`content://...` 或任何绝对目的地。[Android 文档访问](https://developer.android.com/training/data-storage/shared/documents-files)。
3. 相对路径统一用 `/` 分隔，拒绝空段、`.`、`..`、绝对路径、反斜杠、NUL、驱动器前缀、Windows 保留设备名/尾随空格点号、大小写折叠后冲突，以及符号链接/重解析点逃逸。接收端对**最终路径**再次确认仍在用户选定根目录下；不能仅在 Vue 过滤。
4. 接收端 `accept` 后为本次传输分配非零 `streamId`。`FILE_CHUNK` 的 payload 为 `entryIndex:u32_be || offset:u64_be || bytes`，同一条目 offset 必须从 0 单调连续，`entryIndex` 必须对应已接受清单中的普通文件；首版不支持任意偏移恢复。发送/接收均流式读写，不整文件载入内存或 Base64 化。
5. 双端流式计算 SHA-256。`file.finish` 提交各条目的字节数与摘要；接收端只有在长度和摘要都匹配后才发成功 `file.receipt`，并把临时文件移到最终目标。哈希保证传输结果一致，**不证明文件安全无恶意内容**。
6. 取消、断线、摘要不符、空间不足或用户拒绝时，停止写入并清理未提交的临时文件。重名处理（跳过/改名/覆盖）只在接收端由用户选择；默认不得静默覆盖。批量传输要限制条目数、总大小和并发，具体上限在两端实现时固定并通过能力协商公布。

## 8. 远程观看与输入 `remote.*`：授权语义先冻结，媒体细节待样机

远控请求必须指明“观看”或“观看+输入”、被控显示屏/区域、控制端显示能力。被控端展示经过公钥校验的设备名、请求权限和明显的拒绝/结束入口；通过系统授权后才返回一次性的 `remoteSessionId`。观看授权不推出输入授权，改变控制能力要重新确认；Android 系统要求的录屏授权每次重新申请，不重用上次 token。[Android MediaProjection](https://developer.android.com/media/grow/media-projection)。

**画面**：控制端按等比适配显示，不拉伸；每帧携带远端原始宽高、旋转、显示屏 ID、时间戳和递增的 `displayEpoch`。发生旋转、多屏切换或分辨率变化时递增 epoch；来自旧 epoch 的输入应拒绝。双方协商编码/码率/帧率，断流时清空旧敏感画面。媒体承载、编码器和拥塞控制尚未真机验证，**不把视频字节塞进上面的控制 JSON，也不在本草案冻结视频 wire 格式**；未来媒体连接仍须绑定已认证的 `remoteSessionId` 并单独做安全审查。

**输入**：仅在已批准 `remote.input.v1` 会话内接受 allowlist（指针移动/按键/滚轮/触摸及有限导航动作），采用归一化坐标 `0..1` 并附 `displayEpoch`、序号、时间戳；后端验证边界、速率和会话归属。不得通过消息发送任意 shell 命令、快捷方式、系统 API 名称、绝对路径或 Accessibility 节点操作脚本。断线/锁屏/撤权/退出时释放所有按下的键和触点。Windows 注入受完整性级别限制，不承诺控制 UAC 安全桌面；Android 手势取决于用户启用的 AccessibilityService 与系统能力，不得声称可绕过受保护界面。[Windows SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)、[Android AccessibilityService](https://developer.android.com/reference/android/accessibilityservice/AccessibilityService)。

**手机控制端**：全屏可操作；系统 PiP 小窗只显示预览与极少的结束类动作，点按返回全屏后才恢复触控控制。PiP 不是覆盖其他应用的任意交互式悬浮层。[Android PiP](https://developer.android.com/develop/ui/views/picture-in-picture)。任何被控端都必须有本地可见的采集/控制提示和一键结束入口；系统停止录屏回调时立即结束会话并清除画面。

## 9. 状态与通知 `battery.v1` / `notification.v1`

- 电量消息只含百分比、充电状态和采样时间；由数据所属端节流采样，仅对已认证且授权的设备发送。在线显示要有过期时间，断线后不能永久显示“当前电量”。
- 通知默认只发摘要（应用名、标题、短正文、时间），不发操作 token、附件或可远程点击的系统 `PendingIntent`。Android 必须先获得用户授予的通知访问，再检查总开关、逐设备开关和可选应用白名单。[NotificationListenerService](https://developer.android.com/reference/android/service/notification/NotificationListenerService)。
- 亲密设备有权限且系统实际提供验证码时，可用短时 `notification.otp` 优先卡片展示；连接设备的 OTP 开关独立且默认关闭。OTP 不写常规日志、不长期持久化、不在断线后补发；收到者到期清除。Android 15 及后续系统可能对通知监听器隐去 OTP 和屏幕共享敏感内容，必须尊重这种隐去，不绕过系统保护或承诺一定能取到验证码。[Android 15 行为变更](https://developer.android.com/about/versions/15/behavior-changes-all)。
- 通知、屏幕、文件是三条独立授权链：允许文件传输不意味着允许看通知；允许看通知不意味着允许远控。

## 10. 错误、资源限制与兼容性

控制层统一返回稳定错误码和本地化展示文本，至少区分：`INCOMPATIBLE_VERSION`、`UNSUPPORTED_CAPABILITY`、`UNSUPPORTED_MESSAGE`、`UNPAIRED`、`KEY_CHANGED`、`USER_REJECTED`、`PERMISSION_DENIED`、`SYSTEM_PERMISSION_REQUIRED`、`TIMEOUT`、`PEER_OFFLINE`、`RATE_LIMITED`、`INVALID_FRAME`、`INVALID_PATH`、`INSUFFICIENT_SPACE`、`HASH_MISMATCH`、`CANCELLED`。远端提供的错误文本只作不可信诊断数据，不直接写入敏感日志或当作可执行指令。

- 预认证阶段限制连接数、握手时间、帧长度和并发配对；来自同一来源的失败请求退避。未知 magic/主版本、非法长度和加密验证失败直接断开，不尝试旧明文协议。
- 已认证连接闲置时定期心跳，超时清理资源；心跳不代表系统权限仍有效。网络切换后必须重新认证，对文件可重新发起新传输，但远控不能自动恢复。
- 实现不得把完整通知、视频帧、验证码、文件正文、私钥或 Noise 会话密钥写到普通日志。故障日志最多记操作类型、匿名会话 ID、稳定错误码和耗时。
- 协议演进先增 `minor` 与显式能力，新增必需字段或加密/帧格式变化升 `major`；双方不能靠猜测对端版本。旧配对公钥可在兼容的主版本间保留，但每次新会话仍验证；发现版本和应用线协议版本分别处理。

## 11. 双端验收门槛

1. **互操作向量**：冻结 discovery TXT、探测、`QDP1` 三条 Noise 消息、8 位校验码、拒绝/超时、`QDS1` 帧的 PC/Android golden vectors；包含大小端、UTF-8、边界长度和恶意输入。
2. **真实设备**：至少两台 PC 与两台 Android，在 PC↔PC、PC↔Android、Android↔Android 的不同发起方向测试；同机两个隔离身份只能验证状态机，不替代防火墙、权限、移动网络和锁屏测试。
3. **安全负例**：伪造 mDNS/TXT/nonce、端口僵尸、同 ID 换公钥、重放旧帧、未配对访问状态、单端批准、拒绝与取消、撤销后重连、路径穿越、超大帧、系统权限撤销，都必须被拒绝且不留下半完成记录。
4. **功能顺序**：先 Windows↔Android 发现与配对，再四向文件收发，之后 PC 被控、Android 被控/PiP，最后电量与通知。每阶段通过真实双端测试后再把对应能力加入公开协商列表。
5. **发布声明**：只把已在安装包中交付并验收的能力写成“支持”；开发版 UI、协议草案和单机测试不能代替发布验收。

## 12. 尚需在实现前定稿的问题

- Android 最低系统版本、目标 SDK 和分发渠道；局域网权限、后台发现、Android Keystore 的 X25519/封装策略要在这些目标设备上实测。
- `QDS1` 精确 `SessionHello` JSON schema、长度上限与跨语言测试向量；是否复用同一 TCP 连接承载文件，及大文件传输的公平调度。
- 媒体承载与编码选择、多屏和横竖屏协商、弱网拥塞控制；Android 远控输入能力及应用商店政策验证。
- Android/Windows 接收路径、冲突处理、总容量与并发上限；需避免“无限量接收”的默认设置。

这些问题不影响现有 Windows 发现与人工配对的安全边界，但未解决前不能称 `DCP/1` 完整实现。
