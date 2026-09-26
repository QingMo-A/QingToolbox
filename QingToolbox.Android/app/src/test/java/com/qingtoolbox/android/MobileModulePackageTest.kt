package com.qingtoolbox.android

import java.io.ByteArrayOutputStream
import java.util.zip.CRC32
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Guards the module package contract.
 *
 * A package is untrusted input that decides what runs inside the shell, so every rule the
 * importer relies on is pinned here: the manifest shape, the path rules, the payload digest
 * that ties a manifest to its own contents, and the refusal of anything that does not match.
 */
class MobileModulePackageTest {
    @Test
    fun readsAValidPackageAndExposesItsManifest() {
        val archive = MobileModuleArchive.read(packageBytes())

        assertEquals("qing.test-module", archive.manifest.id)
        assertEquals("0.1.0", archive.manifest.version)
        assertEquals("web/index.html", archive.manifest.entry)
        assertEquals(listOf("text.codec", "clipboard.write"), archive.manifest.capabilities)
        assertEquals("测试模块", archive.manifest.displayName.resolve("zh-CN"))
        assertEquals("Test module", archive.manifest.displayName.resolve("en-US"))
        assertEquals(2, archive.entries.size)
        assertTrue(archive.totalBytes > 0)
    }

    @Test
    fun rejectsAPackageWhoseContentsChangedAfterSigning() {
        val (manifest, payload) = signedManifest()
        val tampered = payload.map { (name, _) -> name to "<html>replaced</html>".toByteArray() }
        val bytes = zip(listOf(MobileModuleManifest.MANIFEST_ENTRY to manifest) + tampered)

        assertError(MobileModuleError.PAYLOAD_HASH_MISMATCH) { MobileModuleArchive.read(bytes) }
    }

    @Test
    fun rejectsAPackageWithoutAManifest() {
        assertError(MobileModuleError.MANIFEST_MISSING) {
            MobileModuleArchive.read(zip(listOf("web/index.html" to "<html></html>".toByteArray())))
        }
    }

    @Test
    fun rejectsPathsThatEscapeThePackageRoot() {
        val (manifest, _) = signedManifest()
        val bytes = zip(
            listOf(MobileModuleManifest.MANIFEST_ENTRY to manifest) +
                listOf("web/../../escape.js" to "x".toByteArray()),
        )

        assertError(MobileModuleError.ENTRY_UNSAFE) { MobileModuleArchive.read(bytes) }
    }

    @Test
    fun rejectsFilesAModuleMayNotShip() {
        val (manifest, _) = signedManifest()
        val bytes = zip(
            listOf(MobileModuleManifest.MANIFEST_ENTRY to manifest) +
                listOf("classes.dex" to byteArrayOf(1, 2, 3)),
        )

        assertError(MobileModuleError.ENTRY_NOT_ALLOWED) { MobileModuleArchive.read(bytes) }
    }

    @Test
    fun rejectsDuplicateEntries() {
        val (manifest, payload) = signedManifest()
        val duplicate = payload.first()
        val entries = listOf(MobileModuleManifest.MANIFEST_ENTRY to manifest) +
            payload + duplicate

        assertError(MobileModuleError.ENTRY_DUPLICATED) { MobileModuleArchive.read(rawZip(entries)) }
    }

    @Test
    fun treatsBytesThatAreNotAnArchiveAsAMissingManifest() {
        assertError(MobileModuleError.MANIFEST_MISSING) {
            MobileModuleArchive.read("this is not a zip".toByteArray())
        }
    }

    @Test
    fun rejectsAnOversizedArchiveBeforeReadingIt() {
        val bytes = ByteArray((MobileModuleArchive.MAX_ARCHIVE_BYTES + 1).toInt())

        assertError(MobileModuleError.ARCHIVE_TOO_LARGE) { MobileModuleArchive.read(bytes) }
    }

    @Test
    fun rejectsAPackageWhoseDeclaredPageIsMissing() {
        val manifest = manifestBytes(fields(payloadHash = MobileModuleManifest.payloadDigest(emptyList())))

        assertError(MobileModuleError.ENTRY_MISSING) {
            MobileModuleArchive.read(zip(listOf(MobileModuleManifest.MANIFEST_ENTRY to manifest)))
        }
    }

    @Test
    fun acceptsAManifestThatNamesNoCapabilities() {
        val (manifest, payload) = signedManifest(capabilities = emptyList())
        val archive = MobileModuleArchive.read(
            zip(listOf(MobileModuleManifest.MANIFEST_ENTRY to manifest) + payload),
        )

        assertTrue(archive.manifest.capabilities.isEmpty())
    }

    @Test
    fun rejectsManifestFieldsTheShellCannotHonour() {
        val base = fields()

        assertError(MobileModuleError.RUNTIME_UNSUPPORTED) {
            MobileModuleArchive.read(singleEntry(base.copy(runtimeType = "process")))
        }
        assertError(MobileModuleError.API_UNSUPPORTED) {
            MobileModuleArchive.read(singleEntry(base.copy(apiVersion = 2)))
        }
        assertError(MobileModuleError.API_UNSUPPORTED) {
            MobileModuleArchive.read(singleEntry(base.copy(schemaVersion = 2)))
        }
        assertError(MobileModuleError.MANIFEST_INVALID) {
            MobileModuleArchive.read(singleEntry(base.copy(id = "TextCodec")))
        }
        assertError(MobileModuleError.MANIFEST_INVALID) {
            MobileModuleArchive.read(singleEntry(base.copy(entry = "web/index.htm")))
        }
        assertError(MobileModuleError.MANIFEST_INVALID) {
            MobileModuleArchive.read(singleEntry(base.copy(capabilities = listOf("Text Codec"))))
        }
        assertError(MobileModuleError.MANIFEST_INVALID) {
            MobileModuleArchive.read(singleEntry(base.copy(displayNameJson = "null")))
        }
        assertError(MobileModuleError.MANIFEST_INVALID) {
            MobileModuleArchive.read(singleEntry(base.copy(version = "")))
        }
        assertError(MobileModuleError.MANIFEST_INVALID) {
            MobileModuleArchive.read(singleEntry(base.copy(payloadHash = "short")))
        }
    }

    @Test
    fun payloadDigestIgnoresTheManifestAndFollowsEntryNames() {
        val withManifest = listOf(
            MobileModuleManifest.MANIFEST_ENTRY to "{}".toByteArray(),
            "web/index.html" to "a".toByteArray(),
            "web/app.js" to "b".toByteArray(),
        )
        val reordered = listOf(
            "web/app.js" to "b".toByteArray(),
            "web/index.html" to "a".toByteArray(),
        )

        assertEquals(
            MobileModuleManifest.payloadDigest(withManifest),
            MobileModuleManifest.payloadDigest(reordered),
        )
        assertNotEquals(
            MobileModuleManifest.payloadDigest(reordered),
            MobileModuleManifest.payloadDigest(
                listOf("web/index.html" to "a".toByteArray(), "web/app.js" to "c".toByteArray()),
            ),
        )
    }

    @Test
    fun resolvesLocalizedTextWithSensibleFallbacks() {
        val localized = LocalizedModuleText.parse(
            MobileJson.parse("""{"en-US":"English only","zh-CN":"仅中文"}"""),
        )

        assertEquals("仅中文", localized.resolve("zh-TW"))
        assertEquals("English only", localized.resolve("en-GB"))
        assertEquals("English only", localized.resolve("fr-FR"))
        assertEquals("plain", LocalizedModuleText.parse(MobileJsonValue.Str("plain")).resolve("de-DE"))
        assertTrue(LocalizedModuleText.parse(MobileJsonValue.Null).isEmpty)
    }

    @Test
    fun everyPackageErrorExplainsItself() {
        MobileModuleError.entries.forEach { error ->
            assertTrue("missing label for $error", error.labelRes != 0)
        }
    }

    private fun assertError(expected: MobileModuleError, block: () -> Unit) {
        val failure = assertThrows(MobileModuleFormatException::class.java) { block() }
        assertEquals(expected, failure.error)
    }

    /** A manifest with its payload digested into it, plus the payload it describes. */
    private fun signedManifest(
        payload: Map<String, String> = mapOf("web/index.html" to "<html></html>"),
        capabilities: List<String> = listOf("text.codec", "clipboard.write"),
    ): Pair<ByteArray, List<Pair<String, ByteArray>>> {
        val entries = payload.map { (name, text) -> name to text.toByteArray() }
        val hash = MobileModuleManifest.payloadDigest(entries)
        return manifestBytes(fields(payloadHash = hash, capabilities = capabilities)) to entries
    }

    private fun packageBytes(): ByteArray {
        val (manifest, payload) = signedManifest()
        return zip(listOf(MobileModuleManifest.MANIFEST_ENTRY to manifest) + payload)
    }

    private fun singleEntry(fields: ManifestFields): ByteArray =
        zip(listOf(MobileModuleManifest.MANIFEST_ENTRY to manifestBytes(fields)))

    private fun fields(
        schemaVersion: Int = MobileModuleManifest.SCHEMA_VERSION,
        id: String = "qing.test-module",
        version: String = "0.1.0",
        apiVersion: Int = MobileModuleManifest.API_VERSION,
        displayNameJson: String = """{"en-US":"Test module","zh-CN":"测试模块"}""",
        runtimeType: String = MobileModuleManifest.RUNTIME_WEB,
        entry: String = "web/index.html",
        glyph: String = "\u25A2",
        accent: String = "#7C5CFF",
        capabilities: List<String> = listOf("text.codec", "clipboard.write"),
        payloadHash: String = MobileModuleManifest.payloadDigest(
            listOf("web/index.html" to "<html></html>".toByteArray()),
        ),
    ): ManifestFields = ManifestFields(
        schemaVersion,
        id,
        version,
        apiVersion,
        displayNameJson,
        runtimeType,
        entry,
        glyph,
        accent,
        capabilities,
        payloadHash,
    )

    private fun manifestBytes(fields: ManifestFields): ByteArray = MobileJsonObject()
        .int("schemaVersion", fields.schemaVersion)
        .string("id", fields.id)
        .string("version", fields.version)
        .int("apiVersion", fields.apiVersion)
        .objectValue("displayName", fields.displayNameJson)
        .string("description", "A module used by the shell test suite.")
        .string("runtimeType", fields.runtimeType)
        .string("entry", fields.entry)
        .string("glyph", fields.glyph)
        .string("accent", fields.accent)
        .strings("capabilities", fields.capabilities)
        .string("payloadHash", fields.payloadHash)
        .build()
        .toByteArray()

    private fun zip(entries: List<Pair<String, ByteArray>>): ByteArray {
        val buffer = ByteArrayOutputStream()
        ZipOutputStream(buffer).use { zip ->
            entries.forEach { (name, content) ->
                zip.putNextEntry(ZipEntry(name))
                zip.write(content)
                zip.closeEntry()
            }
        }
        return buffer.toByteArray()
    }

    /**
     * Writes stored (uncompressed) entries straight into local records.
     *
     * `ZipOutputStream` refuses to write the same name twice, and the shell must still
     * reject such an archive, so this builds one by hand.
     */
    private fun rawZip(entries: List<Pair<String, ByteArray>>): ByteArray {
        val buffer = ByteArrayOutputStream()
        val checksum = CRC32()
        entries.forEach { (name, content) ->
            val nameBytes = name.toByteArray()
            checksum.reset()
            checksum.update(content)
            val header = ByteArrayOutputStream()
            header.writeIntLe(0x04034b50)
            header.writeShortLe(20)
            header.writeShortLe(0)
            header.writeShortLe(0)
            header.writeShortLe(0)
            header.writeShortLe(0x21)
            header.writeIntLe(checksum.value.toInt())
            header.writeIntLe(content.size)
            header.writeIntLe(content.size)
            header.writeShortLe(nameBytes.size)
            header.writeShortLe(0)
            buffer.write(header.toByteArray())
            buffer.write(nameBytes)
            buffer.write(content)
        }
        return buffer.toByteArray()
    }

    private fun ByteArrayOutputStream.writeShortLe(value: Int) {
        write(value and 0xff)
        write((value ushr 8) and 0xff)
    }

    private fun ByteArrayOutputStream.writeIntLe(value: Int) {
        write(value and 0xff)
        write((value ushr 8) and 0xff)
        write((value ushr 16) and 0xff)
        write((value ushr 24) and 0xff)
    }

    private data class ManifestFields(
        val schemaVersion: Int,
        val id: String,
        val version: String,
        val apiVersion: Int,
        val displayNameJson: String,
        val runtimeType: String,
        val entry: String,
        val glyph: String,
        val accent: String,
        val capabilities: List<String>,
        val payloadHash: String,
    )
}

/** Manifest-level rejection, exercised on the document itself so the rules stay readable. */
class MobileModuleManifestTest {
    @Test
    fun acceptsAPlainNameAndAMappedName() {
        val manifest = MobileModuleManifest.parse(
            """
            {
              "schemaVersion": 1,
              "id": "qing.device-info",
              "version": "1.2.3",
              "apiVersion": 1,
              "displayName": "Device Info",
              "runtimeType": "web",
              "entry": "web/index.html",
              "payloadHash": "${"0".repeat(64)}"
            }
            """.trimIndent(),
        )

        assertEquals("Device Info", manifest.displayName.resolve("en-US"))
        assertEquals("Device Info", manifest.displayName.resolve("zh-CN"))
        assertTrue(manifest.capabilities.isEmpty())
        assertEquals(MobileModuleManifest.DEFAULT_GLYPH, manifest.glyph)
        assertEquals(MobileModuleManifest.DEFAULT_ACCENT, manifest.accent)
    }

    @Test
    fun fallsBackToDefaultsForCosmeticFields() {
        val manifest = MobileModuleManifest.parse(
            """
            {
              "schemaVersion": 1,
              "id": "qing.qr-code",
              "version": "0.1.0",
              "apiVersion": 1,
              "displayName": "QR",
              "runtimeType": "web",
              "entry": "web/index.html",
              "glyph": "   ",
              "accent": "javascript:alert(1)",
              "payloadHash": "${"a".repeat(64)}"
            }
            """.trimIndent(),
        )

        assertEquals(MobileModuleManifest.DEFAULT_GLYPH, manifest.glyph)
        assertEquals(MobileModuleManifest.DEFAULT_ACCENT, manifest.accent)
    }

    @Test
    fun rejectsMalformedJsonWithoutLeakingTheCause() {
        val failure = assertThrows(MobileModuleFormatException::class.java) {
            MobileModuleManifest.parse("{ not json")
        }

        assertEquals(MobileModuleError.MANIFEST_INVALID, failure.error)
    }

    @Test
    fun refusesIdsThatDoNotUseTheSharedModuleNamespace() {
        listOf("text-codec", "qing.", "qing.TextCodec", "other.text-codec", "").forEach { id ->
            val failure = assertThrows(MobileModuleFormatException::class.java) {
                MobileModuleManifest.parse(
                    """{"schemaVersion":1,"id":"$id","version":"0.1.0","apiVersion":1,""" +
                        """"displayName":"X","runtimeType":"web","entry":"web/index.html",""" +
                        """"payloadHash":"${"0".repeat(64)}"}""",
                )
            }

            assertEquals(
                "id '$id' must be refused",
                MobileModuleError.MANIFEST_INVALID,
                failure.error,
            )
        }
    }
}
