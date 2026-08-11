# QingToolbox Android M0

This directory contains the first-class Android shell for QingToolbox. It is a native Kotlin + Jetpack Compose + Material 3 app with one `MainActivity`, Compose Navigation, and a ViewModel-backed unidirectional state flow.

## Local build

1. Install JDK 17 (or a newer supported JDK), Android SDK Platform 35, and Android build tools.
2. From this directory, run `gradle :app:testDebugUnitTest` for the focused preference tests.
3. Run `gradle :app:assembleDebug` to produce `app/build/outputs/apk/debug/app-debug.apk`.
4. With a device or emulator connected, install it with `adb install -r app/build/outputs/apk/debug/app-debug.apk`.

The project intentionally has no WebView, cross-platform UI runtime, mobile module runtime, Root hook, or cross-device transfer implementation. M0 only establishes the installable mobile shell and its local appearance preference.
