package com.qingtoolbox.android

import android.content.Context
import android.net.Uri
import java.io.File
import java.io.IOException
import java.util.UUID

/** Result of a successful import. [replaced] is true when the same id was already installed. */
data class MobileModuleInstall(
    val module: InstalledMobileModule,
    val replaced: Boolean,
)

/**
 * Owns the shell's private module directory.
 *
 * The directory is the registry: a module is installed exactly when its folder holds a
 * valid manifest, so the shell never keeps a second list that could drift from the disk.
 * Imports are staged in a hidden folder and moved into place only after the whole package
 * has been verified, which keeps a failed import from leaving a half-written module behind.
 */
class MobileModuleStore(context: Context) {
    private val appContext = context.applicationContext

    val root: File
        get() = File(appContext.filesDir, MobileModulePaths.MODULES_DIRECTORY)

    fun listInstalled(): List<InstalledMobileModule> {
        val directory = root
        if (!directory.isDirectory) return emptyList()
        return directory.listFiles()
            .orEmpty()
            .asSequence()
            .filter { it.isDirectory && !it.name.startsWith(STAGING_PREFIX) }
            .mapNotNull { readInstalled(it) }
            .sortedBy { it.id }
            .toList()
    }

    fun readPackagedBytes(uri: Uri): ByteArray {
        val resolver = appContext.contentResolver
        val bytes = try {
            resolver.openInputStream(uri)?.use { stream ->
                val buffer = java.io.ByteArrayOutputStream()
                val chunk = ByteArray(64 * 1024)
                var total = 0
                while (true) {
                    val read = stream.read(chunk)
                    if (read <= 0) break
                    total += read
                    if (total > MobileModuleArchive.MAX_ARCHIVE_BYTES) {
                        throw MobileModuleFormatException(MobileModuleError.ARCHIVE_TOO_LARGE)
                    }
                    buffer.write(chunk, 0, read)
                }
                buffer.toByteArray()
            }
        } catch (exception: MobileModuleFormatException) {
            throw exception
        } catch (exception: Exception) {
            throw MobileModuleFormatException(MobileModuleError.ARCHIVE_UNREADABLE, exception)
        }
        if (bytes == null) {
            throw MobileModuleFormatException(MobileModuleError.ARCHIVE_UNREADABLE)
        }
        return bytes
    }

    fun install(bytes: ByteArray): MobileModuleInstall {
        val archive = MobileModuleArchive.read(bytes)
        val directory = root
        if (!directory.isDirectory && !directory.mkdirs()) {
            throw MobileModuleFormatException(MobileModuleError.ARCHIVE_UNREADABLE)
        }

        val staging = File(directory, STAGING_PREFIX + UUID.randomUUID())
        try {
            if (!staging.mkdirs()) {
                throw MobileModuleFormatException(MobileModuleError.ARCHIVE_UNREADABLE)
            }
            archive.entries.forEach { (name, content) ->
                val target = File(staging, name)
                target.parentFile?.mkdirs()
                target.writeBytes(content)
            }

            val destination = File(directory, archive.manifest.id)
            val replaced = destination.exists()
            if (replaced && !destination.deleteRecursively()) {
                throw mobileModuleStorageFailure("Unable to replace the installed module")
            }
            if (!staging.renameTo(destination)) {
                throw mobileModuleStorageFailure("Unable to move the imported module into place")
            }

            val installed = readInstalled(destination)
                ?: throw mobileModuleStorageFailure("Unable to read back the imported module")
            return MobileModuleInstall(installed, replaced)
        } catch (exception: MobileModuleFormatException) {
            throw exception
        } catch (exception: IOException) {
            throw MobileModuleFormatException(MobileModuleError.ARCHIVE_UNREADABLE, exception)
        } finally {
            if (staging.exists()) staging.deleteRecursively()
        }
    }

    /**
     * Removes a module from storage.
     *
     * The caller must unload the module first: an unlinked dex or asset directory that
     * ART still holds a handle on would fail later. For the web runtime unloading means the
     * WebView is destroyed, which is what [MobileModuleRuntime.unload] does.
     */
    fun delete(module: InstalledMobileModule) {
        if (!module.directory.exists()) return
        if (!module.directory.deleteRecursively()) {
            throw mobileModuleStorageFailure("Unable to delete the module directory")
        }
    }

    private fun readInstalled(directory: File): InstalledMobileModule? {
        val manifestFile = File(directory, MobileModuleManifest.MANIFEST_ENTRY)
        if (!manifestFile.isFile) return null
        val manifest = try {
            MobileModuleManifest.parse(manifestFile.readText(Charsets.UTF_8))
        } catch (_: Exception) {
            return null
        }
        return InstalledMobileModule(
            manifest = manifest,
            directory = directory,
            sizeBytes = directory.walkTopDown().filter { it.isFile }.sumOf { it.length() },
        )
    }

    private fun mobileModuleStorageFailure(message: String): MobileModuleFormatException =
        MobileModuleFormatException(MobileModuleError.ARCHIVE_UNREADABLE, IOException(message))

    private companion object {
        const val STAGING_PREFIX = ".staging-"
    }
}
