package com.qingtoolbox.android

import java.io.File

/**
 * One module that is installed in the shell's private module directory.
 *
 * The model carries no Android types on purpose: the module list, its search and its
 * filters are pure data transformations, and keeping them that way makes them verifiable
 * in plain JVM unit tests.
 */
data class InstalledMobileModule(
    val manifest: MobileModuleManifest,
    val directory: File,
    val sizeBytes: Long,
) {
    val id: String get() = manifest.id

    val version: String get() = manifest.version

    fun displayName(languageTag: String): String = manifest.displayName.resolve(languageTag)

    fun description(languageTag: String): String = manifest.description.resolve(languageTag)
}

/** Loading state of an installed module. Loading is a runtime fact, never persisted. */
enum class MobileModuleLoadState {
    /** The module is on disk but nothing of it is held in memory. */
    NOT_LOADED,

    /** The module runtime is alive in memory and keeps its state while you navigate away. */
    LOADED,
}

/**
 * Path rules shared by the shell and the module packer.
 *
 * A module package is untrusted input, so every entry name is rejected unless it is a
 * plain relative path below the package root. Android 14 already refuses `..` and leading
 * `/` in zip entries, but the shell must not depend on the platform to police its own
 * package format.
 */
object MobileModulePaths {
    /** Directory below the app's private files directory that holds installed modules. */
    const val MODULES_DIRECTORY = "modules"

    /** Prefixes an entry name may use. Everything else is dropped before extraction. */
    val ALLOWED_ENTRY_PREFIXES = listOf("web/")

    fun isSafeRelativePath(path: String): Boolean {
        if (path.isBlank()) return false
        if (path.startsWith("/")) return false
        if (path.contains('\\')) return false
        if (path.contains('\u0000')) return false
        val segments = path.split('/')
        if (segments.any { it.isEmpty() || it == "." || it == ".." }) return false
        // A drive letter or a scheme in an entry name means the package author is
        // thinking about the host file system, not about a package.
        if (segments.first().endsWith(":")) return false
        return true
    }

    fun isAllowedEntry(path: String): Boolean =
        ALLOWED_ENTRY_PREFIXES.any { path.startsWith(it) }
}
