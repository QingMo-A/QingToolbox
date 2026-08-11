package com.qingtoolbox.android

import java.io.File
import java.nio.charset.StandardCharsets
import org.junit.Assert.assertTrue
import org.junit.Test

class StartupContractTest {
    @Test
    fun appCompatActivityUsesAnAppCompatThemeAndStoresLocales() {
        val moduleRoot = findModuleRoot()
        val themes = File(moduleRoot, "app/src/main/res/values/themes.xml")
            .readText(StandardCharsets.UTF_8)
        val manifest = File(moduleRoot, "app/src/main/AndroidManifest.xml")
            .readText(StandardCharsets.UTF_8)

        assertTrue(themes.contains("parent=\"Theme.AppCompat.Light.NoActionBar\""))
        assertTrue(manifest.contains("androidx.appcompat.app.AppLocalesMetadataHolderService"))
        assertTrue(manifest.contains("android:name=\"autoStoreLocales\""))
        assertTrue(manifest.contains("android:value=\"true\""))
    }

    private fun findModuleRoot(): File {
        var directory: File? = File(System.getProperty("user.dir") ?: ".")
        while (directory != null) {
            if (File(directory, "app/src/main/AndroidManifest.xml").isFile) return directory
            val nestedModule = File(directory, "QingToolbox.Android")
            if (File(nestedModule, "app/src/main/AndroidManifest.xml").isFile) return nestedModule
            directory = directory.parentFile
        }
        error("Unable to locate the Android module")
    }
}
