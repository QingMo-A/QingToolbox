package com.qingtoolbox.android

import java.io.File
import java.nio.charset.StandardCharsets
import org.junit.Assert.assertEquals
import org.junit.Test

class ResourceCompletenessTest {
    @Test
    fun englishAndSimplifiedChineseStringResourcesHaveMatchingKeys() {
        val moduleRoot = findModuleRoot()
        val english = stringKeys(File(moduleRoot, "app/src/main/res/values/strings.xml"))
        val simplifiedChinese = stringKeys(
            File(moduleRoot, "app/src/main/res/values-zh-rCN/strings.xml"),
        )
        assertEquals(english, simplifiedChinese)
    }

    private fun findModuleRoot(): File {
        var directory: File? = File(System.getProperty("user.dir") ?: ".")
        while (directory != null) {
            if (File(directory, "app/src/main/res/values/strings.xml").isFile) return directory
            val nestedModule = File(directory, "QingToolbox.Android")
            if (File(nestedModule, "app/src/main/res/values/strings.xml").isFile) return nestedModule
            directory = directory.parentFile
        }
        error("Unable to locate the Android module resources")
    }

    private fun stringKeys(file: File): Set<String> = Regex("""name="([^"]+)"""")
        .findAll(file.readText(StandardCharsets.UTF_8))
        .map { it.groupValues[1] }
        .toSortedSet()
}
