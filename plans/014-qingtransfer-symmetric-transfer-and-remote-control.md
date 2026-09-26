# Plan 014 — QingTransfer Symmetric Transfer, Trust Tiers, and Cross-Device Remote Control

```text
Status: Approved — Decisions Resolved, Implementation Pending Bounded Prompt
Track: Cross-device / QingTransfer
Prerequisite: Plan 013 M0–M2 (Android Shell, built-in tools, native capability boundary)
Depends on:
  - Plan 013 "Planned first cross-device module — QingTransfer"
  - Existing Android QingTransfer file path (discovery, protocol v1, connection, screen)
  - Existing window/child-window boundary on Windows (native + Web Shell)
  - Android minSdk 26 / targetSdk 35
Distribution: private / self-hosted only. Google Play policy is NOT a design constraint.
```

Decisions recorded in section 8. The Google Play constraint that shaped earlier drafts has been withdrawn by the owner: this is a private, self-hosted application, so the native DEX module channel is back on the table.

Still required before implementation: a bounded execution prompt that selects exactly one slice from section 9.

## 1. Where the project stands today

This plan continues Plan 013 rather than replacing it. Plan 013 already names QingTransfer as the first dual-end module and fixes the required topology as symmetric peer-to-peer on one LAN:

```text
PC → Android
Android → PC
PC → PC
Android → Android
```

Reality check against the current tree:

- **Android has a working unitary file path.** `QingTransferDiscovery` advertises and discovers over DNS-SD (`_qingtransfer._tcp`), `QingTransferProtocol` implements a framed v1 message set, and `QingTransferConnection` runs connect / approve / offer / stream / SHA-256 verify. Streaming is real socket I/O with a 128 KB buffer, not Base64.
- **The protocol is deliberately minimal.** `MAX_FRAME_BYTES` is 4096, and the message set is exactly `hello`, `probe`, `probe_ack`, `accept`, `reject`, `file_offer`, `file_accept`, `file_reject`, `file_end`, `file_result`. There is no channel multiplexing, no capability negotiation beyond the `cap` advertisement string, and no session encryption.
- **There is no Windows counterpart yet.** The Windows side (`QingToolbox.Shell` / `QingToolbox.Core`) has no TCP listener, no discovery, and no transfer code. Its only network surface is module/host update download.
- **`cap` is advertised as a literal `"file"`.** It is parsed as a list but only `file` is ever required, so the field is structurally ready to carry more but currently carries nothing else.
- **Android has no overlay and no input injection.** The manifest declares only `INTERNET`, `ACCESS_NETWORK_STATE`, `CHANGE_WIFI_MULTICAST_STATE`. There is no `SYSTEM_ALERT_WINDOW`, no `FOREGROUND_SERVICE_MEDIA_PROJECTION`, and no accessibility service.

The gap this plan closes is therefore threefold: finish the four-path transfer matrix, introduce a trust model that gives different device classes different standing, and add an opt-in remote-control channel on top of both.

## 2. Platform constraints that shape the design

These are hard platform facts, not preferences. They decide what is possible and what must be asked of the user.

### 2.1 Android as a controlled endpoint (被控端)

| Capability | API | Requirement | Hard limit |
|---|---|---|---|
| Screen capture | `MediaProjection` | User consent dialog **per capture session** | From Android 14 (API 34) the consent token is single-use; re-creating a virtual display after stop requires a fresh prompt |
| Capture foreground service | `FOREGROUND_SERVICE_MEDIA_PROJECTION` | Declared type + running service before `getMediaProjection()` | Without a correctly typed FGS the capture call crashes |
| Visible indicator | System | Persistent cast notification | User can dismiss it or revoke in Settings, ending the session |
| Protected windows | `FLAG_SECURE` | Set by the target app | A secure window renders the **whole** captured frame black while it is foregrounded |
| Single-app capture | Android 14 QPR2 / 15 | User chooses one app | Excludes everything the user did not select |
| Input injection | `AccessibilityService.dispatchGesture` | User manually enables the service in Settings | No system signature needed; gestures only, no raw `MotionEvent` |
| Raw event injection | `InputManager.injectInputEvent` | `INJECT_EVENTS` | System signature only — **not available to us** |
| Shell injection | `adb shell input` | Debug/dev only | **Not a product capability** |

Consequences:

1. Remote control of an Android device **cannot be silent**. Every capture session is a user action, and the persistent notification is always visible. The design must present this as a feature ("you always know when you are being controlled"), not fight it.
2. Control requires an **accessibility service enabled by hand**, once, in system Settings. The app must guide the user through it and must degrade gracefully to view-only when it is not enabled.
3. `FLAG_SECURE` blackout is unavoidable and must be surfaced as a normal, expected state rather than an error.
4. Android as **controller** (控制端) has no comparable restriction. Capturing local touch and rendering a remote screen is ordinary app behavior.

### 2.2 Windows as a controlled endpoint

| Capability | API | Requirement |
|---|---|---|
| Screen capture | DXGI Desktop Duplication (`IDXGIOutputDuplication`) | None; frames land in GPU memory with dirty-rect and move-rect metadata |
| Hardware encode path | Media Foundation MFT | Optional; falls back to CPU path on driver failure |
| Input injection | `SendInput` | None for same-session input; more reliable than legacy `mouse_event`/`keybd_event`, including under UAC |
| Multi-monitor | Per-output duplication | Each active display is duplicated separately |

Consequences:

1. Windows is the **only endpoint that can be controlled without a per-session user gesture**, which makes it the most sensitive endpoint and the reason the trust tiers in section 4 exist.
2. A user-visible, revocable "this machine is being controlled" indicator is a product decision we impose on ourselves, not a platform requirement.
3. DXGI's dirty-rect metadata means the Windows capture path should send only changed regions — an efficiency decision made at design time, not an optimization for later.

### 2.3 Explicitly rejected approaches

- **`INJECT_EVENTS`**: requires system signature. Not available regardless of distribution.
- **adb-based injection**: debugging affordance, not a shipping feature.
- **Accessibility-tree screen reading as a `FLAG_SECURE` bypass**: this is a documented malware technique. We must not implement it, and section 7 records that as a binding constraint.

Note on distribution: this application is private and self-hosted (8.1), so Play policy no longer forbids anything here. The earlier draft cited Play's runtime-code-download rule as the reason the module channel must stay Web-based; that reasoning is withdrawn, and slice I plans the native channel. What remains rejected above is rejected for **technical or safety** reasons — missing system signature, debug-only affordance, and a malware technique — not for store compliance. Remote control is still built as a host capability rather than a module (6.2), but now because it needs host-level APIs and needs to be always present, not because a store forbids it.

## 3. Four-path transfer matrix

Plan 013 requires all four directions. They are not four implementations; they are one protocol exercised across four endpoint pairs.

```text
                Android (controller/sender)   Windows (controller/sender)
Android (peer)          A↔A                                W↔A
Windows (peer)          A↔W                                W↔W
```

### 3.1 What is already true

- Android→Android is reachable today in principle (advertise, discover, connect, offer, stream, verify), and is the path with the least new work.
- Android→PC and PC→Android need a Windows endpoint that does not yet exist.
- PC→PC needs that same endpoint on both sides, plus discovery that works between two desktop machines on one LAN.

### 3.2 What must be added to the protocol

The current v1 frame is small and single-purpose. Symmetric transfer and remote control both need the same three additions, so they should be designed once:

1. **A tiny channel abstraction.** Today one socket carries one conversation. Control and transfer must coexist (a control session must be able to send a file without tearing down the stream), so the frame needs a channel id and the reader needs demultiplexing.
2. **Capability negotiation with real content.** `cap` currently advertises the literal `"file"`. It must carry a small, closed set such as `file`, `control.view`, `control.input`, `device.status`. A peer must not attempt control because `cap` was assumed.
3. **A role handshake that is not directional.** The existing `Hello(platform, name)` already carries platform, which is the right shape, but the state machine currently assumes "the one who connected sends first". View-only control inverts who sends what, so the handshake must name roles explicitly rather than infer them from who dialed.

### 3.3 Transfer items worth adding beyond the existing single-file path

Plan 013 already lists multi-file and folder transfer, safe relative-path validation, progress/speed/cancel, receive confirmation by default, and SHA-256 verification. The existing Android code covers single file, confirmation, progress, cancel, and SHA-256. Still missing:

- multiple files as one accepted batch
- folder transfer with per-entry safe relative-path validation
- transfer speed display
- resume — explicitly deferred by Plan 013 unless real usage proves it necessary

## 4. Device trust tiers — "亲密设备" / "连接设备" / "陌生设备"

This is a **new product concept** introduced by this plan. It grades a peer relationship, and it is independent of which endpoint is controlling which.

### 4.1 The three tiers

| Tier | Chinese | Meaning | Default data sharing |
|---|---|---|---|
| Intimate | 亲密设备 | My own devices, paired to my own devices | **On by default** |
| Connected | 连接设备 | A friend's device, explicitly paired with mine | **Off by default** |
| Stranger | 陌生设备 | Visible on the LAN, never paired | No sharing, no control |

### 4.2 What the tier controls

The tier is the single input that decides three separate things. Keeping them separate matters, because a user may want to share status with a device but never let it control anything.

1. **Reachability** — whether the device appears in the trusted list at all, or only in the nearby-but-unpaired list.
2. **Status sharing** — whether the peer may read the status set in section 5.
3. **Control authorization** — whether the peer may request a control session, and whether that request needs a fresh confirmation.

### 4.3 How a tier is established

Tiers must be **earned by an explicit, two-sided pairing act**, never inferred from the network:

- Discovery alone produces `stranger`. Two devices merely seeing each other never escalates anything.
- Escalation is **entered from the nearby-device list** — the user taps a row and picks 亲密 or 连接 — but the act only completes when the other device confirms. See 8.2 for the flow.
- The pairing act is what binds the peer's stable identity to the tier.
- Downgrade back to `stranger` (forget) must always be available in one step and must immediately drop any live session.

This follows the existing `QingTransferPeer` shape: `serviceName` is already canonicalized case-insensitively and works as a display/rendezvous key, but DNS-SD service names are not a security identity. The tier store therefore keys on a fingerprint instead (see 4.4), even though the user picks the tier from a row labelled with the service name.

### 4.4 The identity the tier is bound to

A DNS-SD service name is **not** an identity. It is chosen by the advertiser, is case-insensitive, can collide, and can be re-advertised by anyone. Binding `intimate` to a service name would let a stranger claim my own device's name.

The plan therefore requires a real identity before tiers can be trusted:

- each installation generates a key pair once
- the public key fingerprint is shown during pairing (as text and as a QR code, which Android already has `zxing` for)
- the tier record stores the fingerprint, not the service name
- a peer presenting a known service name with an unknown fingerprint is a **stranger**, and this must be surfaced rather than silently accepted

Because the user asked to assign tiers straight from the nearby list, the fingerprint check happens **inside** that flow rather than in a separate pairing wizard: the row tap opens the tier choice, the choice sends a request, and the fingerprint is what both sides verify and persist. The list stays the single entry point; the identity check is invisible until it matters.

## 5. Shared status data

Both `intimate` and `connected` peers may receive selected status, and both tiers let the owner decide what to share. The defaults differ.

### 5.1 Candidate status fields

| Field | Source (Android) | Source (Windows) | Sensitivity | 亲密 default | 连接 default |
|---|---|---|---|---|---|
| Battery level and charging state | `BatteryManager` | `GetSystemPowerStatus` | Low | On | On |
| Network type | `ConnectivityManager` | Network list APIs | Low | On | On |
| Screen on/off, locked | `PowerManager` | Session/lock APIs | Medium | On | Off |
| Message notification text | `NotificationListenerService` | — | **High** | **On (full text)** | Off |
| SMS notification text | As above | — | **High** | **On (full text)** | Off |

Notification rows reflect the decision in 8.3. Every field remains individually switchable; a tier default only sets the starting position.

### 5.2 Notifications — decided, with the risk recorded

"提示的信息（短信或软件消息）" is the highest-value and highest-risk item in this plan. How it resolved is recorded in **8.3**: full notification text is shared, and for 亲密设备 it is on by default, at the owner's explicit direction. The risk is stated there rather than repeated here.

The parts that still shape the design:

- Android exposes notification content only through `NotificationListenerService`, a **special access** the user grants by hand in Settings — the same class of permission as accessibility. The grant screen must say plainly what is shared and with whom.
- The share is **per-field**, so battery and network can remain on while notifications are off. Turning notifications off for a tier must not force the other fields off with it.
- The share is **never** applied to 连接设备 or 陌生设备.
- Revocation is one step and applies immediately, including on a live session (section 7).
- Because the content is sensitive, its transport must not be weaker than the rest of the session. See 3.2 on the role handshake and 5.3 on status lifetime.

### 5.3 Where status lives

Status must be **pulled or pushed on demand, never stored centrally**. There is no cloud in this plan (Plan 013 defers accounts and cloud), so status is a live LAN exchange between two paired endpoints, expiring when the session ends.

Notification-derived status follows the same rule and, additionally, is never written to disk on the receiving side. It exists in memory for the duration of the session and is dropped when the session ends or the tier is revoked.

## 6. Remote control

### 6.1 The four control directions

| Controller | Controlled | Feasibility | Gate |
|---|---|---|---|
| PC → Android | View + input | Possible | MediaProjection consent per session + accessibility service enabled |
| Android → PC | View + input | Possible | Local opt-in on the Windows side |
| PC → PC | View + input | Possible | Local opt-in on the Windows side |
| Android → Android | View + input | Possible | Consent + accessibility on the controlled phone |

The user's motivation is that UU远程 offers only phone→PC and PC→PC, while phone→phone and PC→phone are missing. All four are technically reachable; they differ in how much user action they demand.

### 6.2 Control is a host capability, not a module

Remote control must be built into both shells rather than shipped as an imported `.qmod`. Reasons:

- **Platform access.** MediaProjection, accessibility services, DXGI, and `SendInput` are all host-level. Plan 013's Web module channel deliberately cannot reach them, and the native module channel in slice I is for tools, not for the control plane.
- **Always present.** A control capability that can be missing because a module was not imported is a worse product. The controlled endpoint must be ready the moment the user consents.
- **The trust model.** Tiers, pairing identities, and the status-sharing switch are cross-cutting product state, not per-module state.

This is consistent with the shell principle: the shell provides the frame and the environment, and control is environment.

### 6.3 Session model

```text
Requester                    Controlled endpoint
   |  control_request  ------------->  |
   |                                    |  (Android: capture consent + accessibility check)
   |                                    |  (Windows: local opt-in + visible indicator)
   |  <-------------  control_accept    |
   |  <=============  video frames      |
   |  -------------->  input events     |
   |  -------------->  control_stop     |
```

Requirements:

- Control is **mutually exclusive per endpoint**. One controlled endpoint serves one controller at a time, matching the existing "single conversation per socket" stance.
- A control session must not block file transfer. This is the reason for the channel abstraction in 3.2.
- The controlled endpoint must always be able to end the session, and ending it must be immediate and one action.
- View-only must be a first-class mode. Input injection requires an additional, separately granted permission on Android, so view-only is the honest fallback.

### 6.4 The floating window (悬浮窗)

The user's requested model: while controlling, the remote screen can shrink to a floating window and expand to full screen.

On Android, the platform primitive is `SYSTEM_ALERT_WINDOW` ("display over other apps"), which is a **special access the user grants by hand**, not a normal runtime permission. It is also one of the signals Android uses for overlay-abuse detection, so the feature must be presented plainly and used only while a control session is active.

Design requirements:

- **Full screen** is the default and the primary mode.
- **Floating window** is entered explicitly, and the grant is requested only when the user first asks for it.
- **Resolution is a real problem.** The remote framebuffer has its own pixel dimensions and aspect ratio; the floating window has arbitrary dimensions. The plan requires:
  - the wire format carries the remote frame's true width, height, and rotation
  - touch coordinates are transmitted **normalized (0..1 relative to the frame)**, then mapped to the controlled endpoint's *current* physical coordinates
  - rotation changes on the controlled endpoint re-send geometry, and the controller re-maps
  - the floating window letterboxes rather than distorts; no non-uniform scaling
- A floating control window must have a visible, unambiguous "this is a remote device" frame so it can never be mistaken for the local screen.

### 6.5 Why this is a large amount of work

A working low-latency control channel needs, at minimum: a capture pipeline, a codec, a transport with congestion awareness, geometry negotiation, a gesture mapping layer, and an accessibility service on Android. Each of these is independently testable and each can fail on its own. This plan therefore splits control into slices (section 9) and puts transfer first, because transfer is already partly built and stress-tests the same transport.

## 7. Binding safety constraints

These are not negotiable and are not subject to later reinterpretation:

1. **No silent control.** A controlled Android device always shows the platform's cast notification. We must never attempt to suppress, hide, or work around it.
2. **No `FLAG_SECURE` circumvention.** Reading the accessibility tree to recover protected content is a documented malware technique. We will not implement it, for any reason.
3. **No consent-dialog automation.** The accessibility service must never auto-approve a permission or capture dialog. Auto-approval is precisely the abuse pattern that makes accessibility services dangerous.
4. **No auto-granted escalation.** No tier, pairing, or setting may grant control without a live user action on the controlled endpoint.
5. **No cloud, no relay, no accounts.** LAN only, per Plan 013. No traffic leaves the local network.
6. **No arbitrary capability through the bridge.** The existing rule stands: undeclared capabilities are refused.
7. **Control sessions are visibly bounded.** Both endpoints show an active-session indicator for the entire duration.
8. **Every grant is revocable in one step**, and revocation takes effect immediately, including on a live session.
9. **Native modules are code, and are treated as such.** Any module that loads DEX/JAR must declare its runtime, must state its capabilities in its manifest, must be refused if undeclared, and must be removable in one action. A native module is never loaded implicitly, never loaded at startup, and never loaded because a Web module asked for it. The signed-APK trust boundary (whatever the user installed is what runs) must not be quietly widened into "whatever was downloaded at runtime runs".
10. **Notification content is shared only with 亲密设备**, never with 连接设备 or 陌生设备, and only while the listener grant is held. Turning the grant off stops the flow immediately, on live sessions included.

## 8. Resolved decisions

All six open questions are settled. These are the binding answers.

### 8.1 Distribution — private, self-hosted

**Decided: no app store.** QingToolbox is developed and used privately. Google Play policy is therefore **not** a design constraint anywhere in this plan.

Consequences:

- The native DEX/JAR module channel rejected in earlier drafts is **permitted again** and is planned as slice I. The Web module channel from Plan 013 stays, because it is the safer default and already built; the native channel becomes an additional, explicitly-opted-into runtime.
- Side-loading, `adb install`, and direct APK distribution are the delivery paths.
- This does **not** relax section 7. The safety constraints there exist to protect the user and the controlled device, not to satisfy a store.

### 8.2 Pairing identity and confirmation direction

**Decided: the peer must confirm. Tier assignment is a two-sided act.**

The user asked to choose 亲密/连接 directly from the nearby-device list. That stays — the *entry point* is the list row, but the *act* is mutual.

Flow when the user taps "设为亲密设备" (or "设为连接设备") on a discovered row:

```text
Local user taps 设为亲密 on a discovered row
        ↓
Local device sends  pair_request { tier, publicKeyFingerprint }
        ↓
Remote device shows a confirmation with the fingerprint and the requester's name
        ↓
Remote user accepts  →  pair_accept { tier, publicKeyFingerprint }
        ↓
Both sides persist the tier against the fingerprint (not the service name)
```

Requirements:

- Neither side may persist a tier until the other side has answered. A one-sided tap leaves the row in a "等待对方确认" state.
- The fingerprint is displayed on both ends and must match. First pairing should offer a QR scan for the remote fingerprint, reusing the existing `zxing` dependency.
- A peer presenting a known service name with an unknown fingerprint is a **stranger**. This is the case the identity exists to catch, and it must be surfaced, never silently accepted.
- Declining leaves both sides untouched, at the tier they had before the request.
- Either side can downgrade to stranger in one step, and the downgrade applies immediately to any live session.
- The tier is keyed on the fingerprint. The service name is only ever a display/rendezvous value.

### 8.3 Notification sharing

**Decided: on by default for 亲密设备, including full notification text.**

This overrides the recommendation in section 5.2, at the owner's explicit direction. The plan implements it as decided, and records the risk here as an acknowledged, informed choice rather than an oversight.

What this means concretely:

- For an 亲密设备, the notification feed is shared by default once the relationship exists.
- Full text is transmitted, not only the summary form proposed in 5.2.
- On Android this requires `NotificationListenerService`, which the user must grant by hand in Settings. The grant screen must state plainly what will be shared and with which tier.
- The share must remain **revocable in one step**, per the constraint in section 7, and revocation must take effect on a live session.

Acknowledged risk, stated plainly for the record:

- A home or office LAN is one broadcast domain. Anyone on it can observe that a session exists, and with a paired device the notification content is readable by the peer by design.
- Notification text routinely contains one-time codes, bank alerts, and private messages. Sharing full text to an 亲密设备 means a compromised or borrowed 亲密 device can read them.
- This is acceptable only because both endpoints are the owner's own devices under the owner's physical control. If a device stops being physically controlled, its tier must be dropped.

Mitigations that survive the decision:

- The tier never applies to 连接设备 or 陌生设备.
- The share is per-field, so battery and network can stay on while notifications are turned off.
- The grant prompt and the status surface both name the tier explicitly, so it is never ambiguous who receives what.

### 8.4 Windows local opt-in

**Decided: accepted.** Windows remains the only endpoint controllable without a per-session system gesture, so a first-time local opt-in plus a persistent indicator is the bar. Section 7 already requires the indicator; the opt-in is confirmed as a design requirement.

### 8.5 First control direction

**Decided: PC→PC and Android→PC first.** Both put Windows on the controlled side, which has no per-session consent gesture, so the transport, geometry, and input mapping can be proven before the harder Android-controlled path is attempted. Android→Android and PC→Android follow once the Windows-controlled path is stable.

### 8.6 View-only before input

**Decided: yes.** View-only ships first so the Android accessibility requirement does not gate the first usable control version. Input injection is slice G, after view-only is proven.

### 8.7 Floating window

**Decided: accepted.** Requesting the Android "display over other apps" special access is acceptable. It is presented plainly, used only during an active control session, and never used to obscure anything else.

### 8.8 Native module channel

**Decided: include it in this plan, as slice I.** With the store constraint withdrawn, the native runtime planned in `QingToolbox.Android/docs/MOBILE_MODULE_RUNTIME.md` is back in scope. See slice I for the shape, including the ART no-unload limitation and the process-isolation consequence recorded in that document.

## 9. Proposed slices

Each slice is independently reviewable and independently shippable. Order is deliberate: transport and trust before control, and the Windows-controlled direction before the Android-controlled one.

**Slice A — Windows endpoint and the four-path matrix.**
Build the Windows-side peer (advertise, discover, listen, connect) reusing the v1 message set. Prove Android↔Android, Android↔Windows, Windows↔Android, Windows↔Windows with single-file transfer. Result: the Plan 013 topology is real.

**Slice B — Protocol v2: channels, capability negotiation, role handshake.**
Add the channel id, the closed capability set, and an explicit role handshake, so control and transfer can share one connection. Decide at slice start whether v1 stays compatible or is cleanly replaced. Result: the transport both later features need.

**Slice C — Trust tiers, pairing identity, and the nearby-list tier picker.**
Implement the key pair, the fingerprint, the two-sided confirmation flow (8.2), the tier store keyed on fingerprint, and the in-list tier picker plus the three-tier grouping. Result: `亲密` / `连接` / `陌生` exist, are assigned from the nearby list, and nothing escalates on one side alone.

**Slice D — Transfer completion.**
Multi-file batches, folder transfer with per-entry path validation, transfer speed. Result: the Plan 013 first-transfer scope is met.

**Slice E — Status sharing.**
Battery, network, and screen state for both tiers with the per-field switch. Notification sharing per 8.3: on by default for 亲密设备 with full text, behind the special-access grant, off for every other tier.

**Slice F — Control, view-only (Windows-controlled first).**
Windows capture pipeline (DXGI Desktop Duplication, dirty-rect aware) → controller rendering, exercised as PC→PC and Android→PC. Geometry negotiation and normalized coordinates. Result: you can watch a Windows machine from either endpoint.

**Slice G — Control, input (Windows-controlled first).**
Windows `SendInput` for PC→PC and Android→PC. Explicit role handshake, mutual exclusion, one-action stop. Result: you can operate a Windows machine from either endpoint.

**Slice H — Control on Android as the controlled endpoint.**
Android capture pipeline (MediaProjection + typed foreground service) plus the accessibility service with `dispatchGesture` for input. Enable Android→Android and PC→Android. This is last among the control slices because it carries the most platform friction: per-session consent, manual accessibility enablement, and the `FLAG_SECURE` blackout.

**Slice I — Native module channel.**
With the store constraint withdrawn (8.1), add the native runtime designed in `QingToolbox.Android/docs/MOBILE_MODULE_RUNTIME.md`: `InMemoryDexClassLoader` for loading, and out-of-process isolation (`android:process=":module"`) for unloading, because ART has no unload API and the process is the real unload boundary. The Web channel from Plan 013 remains the default; the native channel is opt-in per module and must declare itself in its manifest. This slice is independent of A–H and can be scheduled whenever, but it should not be started before the Web channel is exercised on a real device.

**Slice J — Floating window.**
Android overlay mode with the special-access grant, letterboxed scaling, rotation re-mapping, and the remote-device frame treatment. Depends on F and G.

## 10. Non-goals

- Internet relay, NAT traversal, or TURN/STUN. LAN only.
- Cloud storage, accounts, or sync.
- iOS.
- Resumable/chunked transfer unless real usage proves it necessary.
- Audio capture or audio streaming.
- Remote control of a locked or powered-off device.
- Any `FLAG_SECURE` bypass, consent-dialog automation, or `INJECT_EVENTS` attempt.
- Shipping remote control as a downloadable module.
- Clipboard, screenshot, or file-system browsing as control sub-features in the first control version.
- Adding a store-compatibility layer. Distribution is private (8.1); no effort is spent satisfying store review.

## 11. Testing policy

Follows Plan 013: targeted build/install checks plus tests for directly affected code. Additional requirements specific to this plan:

- The protocol v2 frame set gets unit tests in the same pure-JVM style as `QingTransferProtocolTest`, with no permissive JSON dependency.
- Pairing identity gets tests for the stranger-with-known-name case, since that is the attack the identity exists to stop.
- The two-sided tier handshake gets tests for "one side tapped but the other never answered", which must leave both sides unchanged.
- Coordinate mapping gets tests for rotation and non-1:1 aspect ratios, independent of any capture pipeline.
- Capture, injection, and overlay behavior is **not** unit-testable and must be validated on real devices, with the validation steps written down alongside the slice.
- Slice I (native modules) needs an explicit answer for what happens to a loaded native module when the process recycling boundary is reached; that belongs in its own test plan and must not be folded into the transfer tests.
- The existing Windows release/installer gate stays detached from slices A–J unless a slice modifies shared contracts.
