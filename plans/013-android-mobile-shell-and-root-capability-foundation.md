# Plan 013 — Android Mobile Shell and Root Capability Foundation

Status: **Direction Approved — implementation requires a separate bounded Codex prompt**

## Goal

Build QingToolbox for Android as a first-class mobile product: keep the same QingToolbox product concepts and future cross-device contracts, while using UI and system interactions that feel native to Android rather than shrinking the Windows workspace.

The first milestone is the Android toolbox itself. Cross-device file transfer and Root/LSPosed-oriented modules are planned follow-up capabilities, not prerequisites for the first installable mobile build.

## Product principles

- Android-first; iOS is out of scope for this phase.
- The mobile app must work on ordinary non-Root Android devices.
- Root, KernelSU/Magisk/APatch, Xposed/LSPosed-compatible frameworks, and other elevated capabilities are optional capability providers, never a startup requirement.
- Share semantics/contracts when there is a real cross-platform need; do not refactor the existing Windows Core wholesale just to claim code sharing.
- Desktop and Android may render the same product concept differently. Android navigation, touch targets, system back behavior, permissions, file pickers, notifications, and sheets should follow Android conventions.
- Existing Windows B1/B2.1 runtime, Web activation protocol, host asset validation, native fallback, installer, and release boundaries remain frozen unless a P0/P1 issue requires changes.

## M0 — Installable Android Shell

Single objective: produce a QingToolbox Android app that installs, starts, navigates correctly, persists its basic settings, and can be exercised on a real phone.

Initial visible surface:

- Home
- Tools
- Devices
- Settings
- Android-style bottom navigation and top app bar
- Theme/appearance support aligned with the QingToolbox appearance concepts where practical
- Correct system back handling
- Basic app/about/version surface
- Debug APK build and a documented local build path

M0 explicitly does **not** implement a mobile module runtime, `.qmod` compatibility, cross-device transfer, account/cloud sync, background service infrastructure, or Root hooks.

## M1 — Useful built-in mobile tools

After M0 is stable, add a small set of ordinary mobile tools that do not require Root. Prefer visible, independently testable functions such as text/encoding/JSON/hash/QR/file utilities rather than infrastructure work.

Do not create a generic mobile plugin runtime before there is at least one real plugin use case that proves the required contract.

## M2 — Android native capability boundary

Introduce native Android capabilities only as demanded by actual tools:

- Storage Access Framework / file picker
- system share intents
- clipboard
- camera/QR scanning
- notifications
- foreground/background execution only where a real feature requires it

Keep capability access explicit. Normal mobile tools must not accidentally inherit elevated privileges.

## Optional Root / hook-framework capability

Root-oriented modules are a supported future direction, including modules intended specifically for modified/Root Android devices.

Design constraint:

- the main QingToolbox Android app remains usable without Root;
- Root detection and privileged operations sit behind an Android-specific capability boundary;
- hook-framework integration is optional and replaceable rather than baked into the mobile Shell;
- framework/version compatibility is checked at execution time and surfaced to the user;
- dangerous or device-specific operations must require explicit user action and clear failure reporting;
- Root-only modules should declare their requirements (for example Android-only, Root required, hook framework required) instead of silently failing on unsupported devices.

Do not implement this compatibility layer during M0 unless a concrete M0 feature strictly requires it.

## Planned first cross-device module — QingTransfer

After the Android toolbox and mobile module/capability boundaries are proven, QingTransfer is the preferred first dual-end module.

Required topology is symmetric peer-to-peer on the same LAN:

- PC → Android
- Android → PC
- PC → PC
- Android → Android

Every endpoint is a peer that may send and receive; the protocol must not encode "PC server / phone client" assumptions.

First useful transfer scope:

- nearby peer discovery with a manual/QR fallback
- first-time pairing and trusted-device state
- single file, multiple files, and folder transfer
- streaming I/O instead of Base64 or whole-file buffering
- transfer progress, speed, current item, cancel
- receive confirmation by default
- SHA-256 verification after transfer
- safe relative-path validation for folder transfers

Deferred from the first transfer version:

- internet relay
- cloud storage
- accounts
- multi-device broadcast
- resumable/chunk protocol unless real usage proves it necessary
- clipboard/text/image extensions beyond the file-transfer objective

Shared transfer models/protocol semantics should be extracted only when implementing this real two-platform feature.

## Architecture decision rule

Do not choose abstractions because they might be useful later. For each shared/mobile boundary, require a real consumer on Android or a real cross-device feature.

The Android implementation technology may differ from the Windows presentation stack. The deciding criteria are Android UX quality, access to Android platform APIs, maintainability, packaging reliability, and the ability to keep cross-device contracts clean — not maximum UI-code reuse.

## Execution order

1. Finish the current bounded Desktop appearance/control-style work and stop expanding it.
2. Implement M0 Android Shell as one product milestone.
3. Validate the APK on a real Android device and correct only concrete P0/P1 issues.
4. Add a small M1 built-in-tool slice.
5. Introduce M2 native capabilities only when demanded by those tools.
6. Design the mobile module contract from real module requirements.
7. Implement QingTransfer as the first planned dual-end module.
8. Add optional Root/hook-framework-specific modules only after the normal Android toolbox remains stable.

## Testing policy

For M0/M1 tasks, prefer targeted Android build/install checks and tests for directly affected code. Do not attach the Windows full release/installer gate to ordinary Android UI work.

A full cross-platform release gate is justified only when preparing an actual Android release or modifying shared contracts that affect both desktop and mobile.

## Non-goals for this plan

- rewriting the Windows Shell
- replacing the current Windows module runtime
- forcing Windows and Android to share identical UI code
- making Root mandatory
- inventing a universal cross-platform plugin system before real modules need it
- implementing iOS
- introducing cloud infrastructure
