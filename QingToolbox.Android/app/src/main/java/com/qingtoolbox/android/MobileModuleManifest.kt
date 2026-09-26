package com.qingtoolbox.android

import androidx.annotation.StringRes
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.security.MessageDigest
import java.util.zip.ZipInputStream

/** Why a module package was rejected. Every reason maps to one user-facing sentence. */
enum class MobileModuleError(@StringRes val labelRes: Int) {
    ARCHIVE_TOO_LARGE(R.string.module_error_archive_too_large),
    ARCHIVE_UNREADABLE(R.string.module_error_archive_unreadable),
    ENTRY_UNSAFE(R.string.module_error_entry_unsafe),
    ENTRY_NOT_ALLOWED(R.string.module_error_entry_not_allowed),
    ENTRY_DUPLICATED(R.string.module_error_entry_duplicated),
    MANIFEST_MISSING(R.string.module_error_manifest_missing),
    MANIFEST_INVALID(R.string.module_error_manifest_invalid),
    RUNTIME_UNSUPPORTED(R.string.module_error_runtime_unsupported),
    API_UNSUPPORTED(R.string.module_error_api_unsupported),
    ENTRY_MISSING(R.string.module_error_entry_missing),
    PAYLOAD_HASH_MISMATCH(R.string.module_error_payload_hash_mismatch),
}

/** Raised when a package cannot be trusted. Carries a reason the UI can explain. */
class MobileModuleFormatException(
    val error: MobileModuleError,
    cause: Throwable? = null,
) : IllegalArgumentException(error.name, cause)

/**
 * A manifest field that may be translated.
 *
 * Manifests declare either a plain string (used for every language) or a map keyed by
 * language tag. Resolution prefers an exact tag, then the bare language, then the
 * untagged value, so a module never renders as an empty title.
 */
data class LocalizedModuleText(private val values: Map<String, String>) {
    val isEmpty: Boolean get() = values.isEmpty() || values.values.all { it.isBlank() }

    fun resolve(languageTag: String): String {
        if (values.isEmpty()) return ""
        val normalized = languageTag.trim().lowercase().replace('_', '-')
        values[normalized]?.takeIf { it.isNotBlank() }?.let { return it }
        val language = normalized.substringBefore('-')
        if (language.isNotEmpty()) {
            values.entries
                .firstOrNull { it.key.lowercase().substringBefore('-') == language && it.value.isNotBlank() }
                ?.let { return it.value }
        }
        values[UNDETERMINED]?.takeIf { it.isNotBlank() }?.let { return it }
        return values.entries
            .filter { it.value.isNotBlank() }
            .minByOrNull { it.key }
            ?.value
            ?: ""
    }

    companion object {
        private const val UNDETERMINED = "und"

        val EMPTY = LocalizedModuleText(emptyMap())

        fun of(value: String): LocalizedModuleText = LocalizedModuleText(mapOf(UNDETERMINED to value))

        fun parse(value: MobileJsonValue?): LocalizedModuleText {
            value?.asStringOrNull()?.let { return of(it) }
            val entries = value?.asObjectOrNull() ?: return EMPTY
            val values = entries.mapNotNull { (key, item) ->
                val text = item.asStringOrNull() ?: return@mapNotNull null
                key.trim().lowercase() to text
            }.toMap()
            return LocalizedModuleText(values)
        }
    }
}

/**
 * The contract a module package declares about itself.
 *
 * Field names mirror the desktop module protocol so both platforms can share one
 * vocabulary; `runtimeType` is the one field whose value set differs, because the mobile
 * shell currently ships only the interpreted (`web`) runtime.
 */
data class MobileModuleManifest(
    val schemaVersion: Int,
    val id: String,
    val version: String,
    val apiVersion: Int,
    val displayName: LocalizedModuleText,
    val description: LocalizedModuleText,
    val runtimeType: String,
    val entry: String,
    val glyph: String,
    val accent: String,
    val capabilities: List<String>,
    val payloadHash: String,
) {
    companion object {
        const val SCHEMA_VERSION = 1

        /** Contract version this shell implements. Bump only when the bridge changes. */
        const val API_VERSION = 1

        const val RUNTIME_WEB = "web"

        const val MANIFEST_ENTRY = "manifest.json"

        const val DEFAULT_GLYPH = "\u25A2"

        const val DEFAULT_ACCENT = "#7C5CFF"

        const val MAX_CAPABILITIES = 32

        val MODULE_ID_PATTERN = Regex("""^qing\.[a-z0-9]+(?:[.-][a-z0-9]+)*$""")

        val VERSION_PATTERN = Regex("""^[0-9A-Za-z][0-9A-Za-z.+-]{0,31}$""")

        val CAPABILITY_PATTERN = Regex("""^[a-z][a-z0-9]*(?:\.[a-z0-9]+)*$""")

        val ACCENT_PATTERN = Regex("""^#[0-9A-Fa-f]{6}$""")

        val HASH_PATTERN = Regex("""^[0-9a-f]{64}$""")

        private const val MAX_GLYPH_LENGTH = 4

        private const val HEX = "0123456789abcdef"

        fun parse(json: String): MobileModuleManifest {
            val root = try {
                MobileJson.parse(json)
            } catch (exception: MobileJsonException) {
                throw MobileModuleFormatException(MobileModuleError.MANIFEST_INVALID, exception)
            }
            return parse(root)
        }

        fun parse(root: MobileJsonValue): MobileModuleManifest {
            if (root.asObjectOrNull() == null) {
                throw MobileModuleFormatException(MobileModuleError.MANIFEST_INVALID)
            }

            val schemaVersion = root.intField("schemaVersion")
                ?: throw MobileModuleFormatException(MobileModuleError.MANIFEST_INVALID)
            if (schemaVersion != SCHEMA_VERSION) {
                throw MobileModuleFormatException(MobileModuleError.API_UNSUPPORTED)
            }

            val id = root.stringField("id")?.trim().orEmpty()
            if (!MODULE_ID_PATTERN.matches(id)) {
                throw MobileModuleFormatException(MobileModuleError.MANIFEST_INVALID)
            }

            val version = root.stringField("version")?.trim().orEmpty()
            if (!VERSION_PATTERN.matches(version)) {
                throw MobileModuleFormatException(MobileModuleError.MANIFEST_INVALID)
            }

            val apiVersion = root.intField("apiVersion")
                ?: throw MobileModuleFormatException(MobileModuleError.MANIFEST_INVALID)
            if (apiVersion != API_VERSION) {
                throw MobileModuleFormatException(MobileModuleError.API_UNSUPPORTED)
            }

            val displayName = LocalizedModuleText.parse(root.field("displayName"))
            if (displayName.isEmpty) {
                throw MobileModuleFormatException(MobileModuleError.MANIFEST_INVALID)
            }

            val runtimeType = root.stringField("runtimeType")?.trim().orEmpty()
            if (runtimeType != RUNTIME_WEB) {
                throw MobileModuleFormatException(MobileModuleError.RUNTIME_UNSUPPORTED)
            }

            val entry = root.stringField("entry")?.trim().orEmpty()
            if (!MobileModulePaths.isSafeRelativePath(entry) || !entry.endsWith(".html")) {
                throw MobileModuleFormatException(MobileModuleError.MANIFEST_INVALID)
            }

            val glyph = root.stringField("glyph")
                ?.trim()
                ?.takeIf { it.isNotEmpty() && it.length <= MAX_GLYPH_LENGTH }
                ?: DEFAULT_GLYPH

            val accent = root.stringField("accent")
                ?.trim()
                ?.takeIf { ACCENT_PATTERN.matches(it) }
                ?: DEFAULT_ACCENT

            val declaredCapabilities = root.field("capabilities")?.let { value ->
                if (value is MobileJsonValue.Null) emptyList()
                else value.asStringArrayOrNull()
                    ?: throw MobileModuleFormatException(MobileModuleError.MANIFEST_INVALID)
            } ?: emptyList()
            val capabilities = declaredCapabilities.map { it.trim() }.distinct()
            if (capabilities.size > MAX_CAPABILITIES || capabilities.any { !CAPABILITY_PATTERN.matches(it) }) {
                throw MobileModuleFormatException(MobileModuleError.MANIFEST_INVALID)
            }

            val payloadHash = root.stringField("payloadHash")?.trim().orEmpty()
            if (!HASH_PATTERN.matches(payloadHash)) {
                throw MobileModuleFormatException(MobileModuleError.MANIFEST_INVALID)
            }

            return MobileModuleManifest(
                schemaVersion = schemaVersion,
                id = id,
                version = version,
                apiVersion = apiVersion,
                displayName = displayName,
                description = LocalizedModuleText.parse(root.field("description")),
                runtimeType = runtimeType,
                entry = entry,
                glyph = glyph,
                accent = accent,
                capabilities = capabilities,
                payloadHash = payloadHash,
            )
        }

        /**
         * Digests every payload entry of a package.
         *
         * The packer and the shell must agree byte for byte, so the algorithm is fixed:
         * for each entry except the manifest, in code-point order of the entry name, feed
         * the name, a newline, the hex SHA-256 of the entry contents and another newline
         * into one SHA-256. The manifest itself is excluded because it carries the result.
         */
        fun payloadDigest(entries: List<Pair<String, ByteArray>>): String {
            val digest = MessageDigest.getInstance("SHA-256")
            entries
                .filter { it.first != MANIFEST_ENTRY }
                .sortedBy { it.first }
                .forEach { (name, bytes) ->
                    digest.update(name.toByteArray(Charsets.UTF_8))
                    digest.update('\n'.code.toByte())
                    digest.update(sha256Hex(bytes).toByteArray(Charsets.US_ASCII))
                    digest.update('\n'.code.toByte())
                }
            return digest.digest().toHex()
        }

        fun sha256Hex(bytes: ByteArray): String =
            MessageDigest.getInstance("SHA-256").digest(bytes).toHex()

        private fun ByteArray.toHex(): String = buildString(size * 2) {
            this@toHex.forEach { byte ->
                val value = byte.toInt() and 0xff
                append(HEX[value ushr 4])
                append(HEX[value and 0x0f])
            }
        }
    }
}

/**
 * A validated module package, held in memory.
 *
 * Reading is fully separated from writing to disk: nothing is extracted until the whole
 * package has been checked, so a rejected package can never leave partial files behind.
 */
class MobileModuleArchive private constructor(
    val manifest: MobileModuleManifest,
    val entries: List<Pair<String, ByteArray>>,
) {
    val totalBytes: Long get() = entries.sumOf { it.second.size.toLong() }

    companion object {
        const val MAX_ARCHIVE_BYTES = 8L * 1024 * 1024
        const val MAX_ENTRY_BYTES = 4L * 1024 * 1024
        const val MAX_TOTAL_BYTES = 16L * 1024 * 1024
        const val MAX_ENTRIES = 512

        fun read(bytes: ByteArray): MobileModuleArchive {
            if (bytes.size > MAX_ARCHIVE_BYTES) {
                throw MobileModuleFormatException(MobileModuleError.ARCHIVE_TOO_LARGE)
            }

            val entries = LinkedHashMap<String, ByteArray>()
            var total = 0L
            try {
                ZipInputStream(ByteArrayInputStream(bytes)).use { zip ->
                    while (true) {
                        val entry = zip.nextEntry ?: break
                        val name = entry.name
                        if (entry.isDirectory) {
                            zip.closeEntry()
                            continue
                        }
                        when {
                            !MobileModulePaths.isSafeRelativePath(name) ->
                                throw MobileModuleFormatException(MobileModuleError.ENTRY_UNSAFE)
                            name != MobileModuleManifest.MANIFEST_ENTRY &&
                                !MobileModulePaths.isAllowedEntry(name) ->
                                throw MobileModuleFormatException(MobileModuleError.ENTRY_NOT_ALLOWED)
                            entries.containsKey(name) ->
                                throw MobileModuleFormatException(MobileModuleError.ENTRY_DUPLICATED)
                            entries.size >= MAX_ENTRIES ->
                                throw MobileModuleFormatException(MobileModuleError.ARCHIVE_TOO_LARGE)
                        }

                        val content = zip.readEntryBytes()
                        total += content.size
                        if (content.size > MAX_ENTRY_BYTES || total > MAX_TOTAL_BYTES) {
                            throw MobileModuleFormatException(MobileModuleError.ARCHIVE_TOO_LARGE)
                        }
                        entries[name] = content
                        zip.closeEntry()
                    }
                }
            } catch (exception: MobileModuleFormatException) {
                throw exception
            } catch (exception: Exception) {
                throw MobileModuleFormatException(MobileModuleError.ARCHIVE_UNREADABLE, exception)
            }

            val manifestBytes = entries[MobileModuleManifest.MANIFEST_ENTRY]
                ?: throw MobileModuleFormatException(MobileModuleError.MANIFEST_MISSING)
            val manifest = MobileModuleManifest.parse(manifestBytes.toString(Charsets.UTF_8))

            val entryFile = entries[manifest.entry]
                ?: throw MobileModuleFormatException(MobileModuleError.ENTRY_MISSING)
            if (entryFile.isEmpty()) {
                throw MobileModuleFormatException(MobileModuleError.ENTRY_MISSING)
            }

            val ordered = entries.entries.map { it.key to it.value }
            if (MobileModuleManifest.payloadDigest(ordered) != manifest.payloadHash) {
                throw MobileModuleFormatException(MobileModuleError.PAYLOAD_HASH_MISMATCH)
            }

            return MobileModuleArchive(manifest, ordered)
        }

        private fun ZipInputStream.readEntryBytes(): ByteArray {
            val buffer = ByteArrayOutputStream()
            val chunk = ByteArray(16 * 1024)
            var written = 0
            while (true) {
                val read = read(chunk)
                if (read <= 0) break
                written += read
                if (written > MAX_ENTRY_BYTES) {
                    throw MobileModuleFormatException(MobileModuleError.ARCHIVE_TOO_LARGE)
                }
                buffer.write(chunk, 0, read)
            }
            return buffer.toByteArray()
        }
    }
}
