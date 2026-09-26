package com.qingtoolbox.android

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Guards what the modules page shows when the user types or switches a filter.
 *
 * Search and filtering are pure functions over installed modules, so the behaviour is
 * pinned here rather than tested through the UI.
 */
class MobileModuleQueryTest {
    private val modules = listOf(
        module("qing.text-codec", "Text Codec", "Encode and decode Base64 or URL text."),
        module("qing.device-info", "Device Info", "Public device properties."),
        module("qing.qr-code", "QR Code", "Generate a QR code from text."),
        module("qing.file-hash", "File Hash", "Hash a local file."),
    )

    @Test
    fun listsEveryModuleInAlphabeticalOrder() {
        val visible = MobileModuleQuery.apply(modules, "", MobileModuleFilter.ALL, emptySet(), "en-US")

        assertEquals(
            listOf("Device Info", "File Hash", "QR Code", "Text Codec"),
            visible.map { it.displayName("en-US") },
        )
    }

    @Test
    fun splitsModulesByTheirLoadingState() {
        val loaded = setOf("qing.qr-code")

        assertEquals(
            listOf("qing.qr-code"),
            MobileModuleQuery.apply(modules, "", MobileModuleFilter.LOADED, loaded, "en-US").map { it.id },
        )
        assertEquals(
            listOf("qing.device-info", "qing.file-hash", "qing.text-codec"),
            MobileModuleQuery.apply(modules, "", MobileModuleFilter.NOT_LOADED, loaded, "en-US").map { it.id },
        )
        assertEquals(4, MobileModuleQuery.apply(modules, "", MobileModuleFilter.ALL, loaded, "en-US").size)
    }

    @Test
    fun matchesOnNameIdAndDescriptionCaseInsensitively() {
        fun search(query: String): List<String> =
            MobileModuleQuery.apply(modules, query, MobileModuleFilter.ALL, emptySet(), "en-US").map { it.id }

        assertEquals(listOf("qing.qr-code"), search("qr"))
        assertEquals(listOf("qing.qr-code"), search("QR"))
        assertEquals(listOf("qing.file-hash"), search("hash"))
        assertEquals(listOf("qing.device-info"), search("qing.device"))
        assertEquals(listOf("qing.text-codec"), search("base64"))
        assertTrue(search("nothing-matches-this").isEmpty())
        // A blank query is not a filter, and neither is surrounding whitespace.
        assertEquals(4, search("   ").size)
    }

    @Test
    fun combinesSearchAndLoadingFilter() {
        val loaded = setOf("qing.file-hash")

        assertEquals(
            listOf("qing.file-hash"),
            MobileModuleQuery.apply(modules, "file", MobileModuleFilter.LOADED, loaded, "en-US").map { it.id },
        )
        assertTrue(
            MobileModuleQuery.apply(modules, "file", MobileModuleFilter.NOT_LOADED, loaded, "en-US").isEmpty(),
        )
    }

    @Test
    fun resolvesNamesInTheShellLanguage() {
        val bilingual = InstalledMobileModule(
            manifest = manifestOf("qing.bilingual").copy(
                displayName = LocalizedModuleText.parse(
                    MobileJson.parse("""{"en-US":"Zebra","zh-CN":"阿尔法"}"""),
                ),
            ),
            directory = File("C:/modules/qing.bilingual"),
            sizeBytes = 10,
        )

        assertEquals("阿尔法", bilingual.displayName("zh-CN"))
        assertEquals("Zebra", bilingual.displayName("en-US"))
    }

    private fun module(id: String, name: String, description: String): InstalledMobileModule =
        InstalledMobileModule(
            manifest = manifestOf(id).copy(
                displayName = LocalizedModuleText.of(name),
                description = LocalizedModuleText.of(description),
            ),
            directory = File("C:/modules/$id"),
            sizeBytes = 1024,
        )

    private fun manifestOf(id: String) = MobileModuleManifest(
        schemaVersion = MobileModuleManifest.SCHEMA_VERSION,
        id = id,
        version = "0.1.0",
        apiVersion = MobileModuleManifest.API_VERSION,
        displayName = LocalizedModuleText.of("Module"),
        description = LocalizedModuleText.EMPTY,
        runtimeType = MobileModuleManifest.RUNTIME_WEB,
        entry = "web/index.html",
        glyph = MobileModuleManifest.DEFAULT_GLYPH,
        accent = MobileModuleManifest.DEFAULT_ACCENT,
        capabilities = emptyList(),
        payloadHash = "0".repeat(64),
    )
}
