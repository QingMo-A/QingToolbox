package com.qingtoolbox.android

import java.io.File
import java.nio.charset.StandardCharsets
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the shell's navigation-animation policy.
 *
 * The regression this guards against is a slow cross-fade, which is a source-level
 * property: `NavHost` has no runtime-visible handle on whether a transition was
 * configured, so the check reads the call site instead.
 */
class QingShellNavigationTest {

    @Test
    fun shellPassesExplicitlyEmptyTransitions() {
        val source = File(moduleRoot(), SHELL_SOURCE).readText(StandardCharsets.UTF_8)

        assertTrue(
            "NavHost must opt out of the default cross-fade explicitly.",
            source.contains("enterTransition = { ShellEnterTransition }"),
        )
        assertTrue(
            "NavHost must opt out of the default cross-fade explicitly.",
            source.contains("exitTransition = { ShellExitTransition }"),
        )
    }

    @Test
    fun startDestinationIsNotWrappedInAnErasingHelper() {
        val source = File(moduleRoot(), SHELL_SOURCE).readText(StandardCharsets.UTF_8)

        // NavHost has both a `String` and an `Any` overload. The `Any` overload expects a
        // KClass-registered graph and throws "Cannot find startDestination kotlin.String
        // from NavGraph" for a route-based one, so the argument must keep its static type.
        assertTrue(
            "startDestination must be the raw route string.",
            source.contains("startDestination = ShellDestination.Home.route"),
        )
        assertFalse(
            "Wrapping startDestination in a helper erases its type and binds the wrong NavHost overload.",
            source.contains("shellLaunchBackground"),
        )
    }

    @Test
    fun shellSourcesDoNotUseAdHocAnimationContainers() {
        listOf(SHELL_SOURCE, DEVICES_SOURCE).forEach { path ->
            val source = File(moduleRoot(), path).readText(StandardCharsets.UTF_8)
            listOf("Crossfade", "AnimatedContent", "AnimatedVisibility")
                .forEach { symbol ->
                    assertFalse(
                        "$path uses $symbol; the shell switches screens instantly.",
                        source.contains(symbol),
                    )
                }
        }
    }

    private fun moduleRoot(): File {
        var directory: File? = File(System.getProperty("user.dir") ?: ".")
        while (directory != null) {
            if (File(directory, "app/src/main/AndroidManifest.xml").isFile) return directory
            val nested = File(directory, "QingToolbox.Android")
            if (File(nested, "app/src/main/AndroidManifest.xml").isFile) return nested
            directory = directory.parentFile
        }
        error("Unable to locate the Android module")
    }

    private companion object {
        const val SHELL_SOURCE = "app/src/main/java/com/qingtoolbox/android/QingToolboxApp.kt"
        const val DEVICES_SOURCE = "app/src/main/java/com/qingtoolbox/android/QingTransferScreen.kt"
    }
}
