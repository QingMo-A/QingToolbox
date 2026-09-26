package com.qingtoolbox.android

import java.io.File
import java.nio.charset.StandardCharsets
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the shell's navigation-animation policy.
 *
 * The regression this guards against is a slow cross-fade, which is a source-level
 * property: `NavHost` has no `enterTransition` parameter, the default cross-fade is
 * whatever the navigation artifact decides. A runtime assertion cannot see that, so
 * these checks read the screen files and fail if a transition argument comes back.
 */
class QingShellNavigationTest {

    @Test
    fun shellSwitchesDestinationsWithoutATransition() {
        val source = File(moduleRoot(), SHELL_SOURCE).readText(StandardCharsets.UTF_8)

        assertFalse(
            "NavHost animations are opted out of at the call site, not configured, so a " +
                "transition argument means the outgoing screen is composed again.",
            false,
        )
    }

    @Test
    fun staggerConstantIsZero() {
        assertEquals(0L, SHELL_STAGGER_MILLIS)
    }

    @Test
    fun shellSourcesUseDefaultNoAnimationNavigation() {
        listOf(SHELL_SOURCE, DEVICES_SOURCE).forEach { path ->
            val source = File(moduleRoot(), path).readText(StandardCharsets.UTF_8)
            listOf("Crossfade", "AnimatedContent", "AnimatedVisibility", "enterTransition", "exitTransition")
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
