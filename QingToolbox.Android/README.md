# QingToolbox Android

The first-class Android shell for QingToolbox: a native Kotlin + Jetpack Compose + Material 3
app with one `MainActivity`, Compose Navigation, and a ViewModel-backed unidirectional state
flow.

## Shell and modules

The app follows the same product rule as the desktop host: **the shell provides the frame, and
every visible tool arrives as an imported module**, not as code baked into the shell.

- The shell owns four destinations — Home, Modules, Devices and Settings — under the `shell/`
  route prefix. Device connectivity is deliberately not a module: it is core plumbing shared
  with the desktop host.
- Modules are `.qmod` packages (see [`../android_modules`](../android_modules)). The user picks
  one with `+` in the top bar; it is verified, copied into app-private storage and listed as
  **not loaded**. Importing never executes anything — loading is a separate decision the user
  makes on the module page.
- A module runs as a web page served from the shell, and reaches Android only through a
  capability bridge. It declares what it needs in its manifest; anything undeclared is refused.
  Requests to any host other than the module's own package are refused too, so a module is
  offline by construction. See [`docs/MOBILE_MODULE_RUNTIME.md`](docs/MOBILE_MODULE_RUNTIME.md).

### Where things live

| Concern | File |
| --- | --- |
| Manifest model, package validation, payload digest | `MobileModuleManifest.kt` |
| Dependency-free JSON reader/writer | `MobileJson.kt` |
| Import, scan and delete on disk | `MobileModuleStore.kt` |
| Loaded runtimes, offline asset serving, theme injection | `MobileModuleRuntime.kt` |
| Capability bridge exposed to a module page | `MobileModuleHostBridge.kt` |
| Capability names and their user-facing labels | `MobileModuleCapabilities.kt` |
| Compose host for a loaded module | `MobileModuleWeb.kt` |
| Module model, search and loading filter | `MobileModuleModel.kt`, `MobileModuleQuery.kt` |
| Shell pages and navigation | `QingToolboxApp.kt` |
| Shell web assets injected into every module page | `app/src/main/assets/shell/` |

## Local build

1. Install JDK 17 (or a newer supported JDK), Android SDK Platform 35, and Android build tools,
   then point `local.properties` at the SDK (`sdk.dir=...`).
2. The repository has no Gradle wrapper yet, so these commands require Gradle 8.8+ on `PATH` (or
   your own wrapper).
3. From this directory, run `gradle :app:testDebugUnitTest` for the unit tests, which cover the
   module package contract, the capability rules, the module search and filter, and the
   resources. `PackagedAndroidModulesTest` reads the real packages in `../android_modules`, so
   the Python packer and the Kotlin importer are checked against each other.
4. Run `gradle :app:assembleDebug` to produce
   `app/build/outputs/apk/debug/build-<yyyyMMdd-HHmmss>.apk`. The untimestamped
   `app-debug.apk` is written too, but the stamped copy is the one to keep: it
   is what tells two builds apart once a package sits on a device.
5. With a device or emulator connected, install it with
   `adb install -r app/build/outputs/apk/debug/build-<yyyyMMdd-HHmmss>.apk`.

## Status

Implemented: installable shell, four-destination navigation with correct system back handling,
module import through the system file picker, the module list with search and a loading-state
filter, per-module detail pages with load / unload / delete, the offline module runtime and its
capability bridge, local appearance and language preferences, and QingTransfer device discovery
and file transfer with the desktop host.

Not implemented: the native (out-of-process DEX) module channel, module updates, cloud or
account sync, background services, and Root or hook-framework capabilities.
