package com.qingtoolbox.android

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test

/**
 * Proves the shell accepts the packages this repository ships.
 *
 * The packer is Python and the importer is Kotlin, and the two only agree if the payload
 * digest is computed identically on both sides. Reading the real archives here is the one
 * check that catches a drift between the tool that signs a module and the shell that has to
 * trust it.
 */
class PackagedAndroidModulesTest {
    @Test
    fun everyPackagedModuleSurvivesImport() {
        val directory = findModuleDirectory()
        assumeTrue("this checkout has no android_modules directory", directory != null)
        val packages = requireNotNull(directory).listFiles { file -> file.name.endsWith(PACKAGE_SUFFIX) }
            .orEmpty()
            .sortedBy { it.name }
        assumeTrue("this checkout has no packaged modules", packages.isNotEmpty())

        packages.forEach { file ->
            val archive = MobileModuleArchive.read(file.readBytes())

            assertEquals(
                "package file name must start with the module id",
                true,
                file.name.startsWith(archive.manifest.id),
            )
            assertEquals(MobileModuleManifest.RUNTIME_WEB, archive.manifest.runtimeType)
            assertEquals(MobileModuleManifest.SCHEMA_VERSION, archive.manifest.schemaVersion)
            assertEquals(MobileModuleManifest.API_VERSION, archive.manifest.apiVersion)
            assertTrue(
                "${archive.manifest.id} must declare at least one capability",
                archive.manifest.capabilities.isNotEmpty(),
            )
            assertTrue(
                "${archive.manifest.id} must declare an English or untagged name",
                archive.manifest.displayName.resolve("en-US").isNotBlank(),
            )
            archive.manifest.capabilities.forEach { capability ->
                assertTrue(
                    "${archive.manifest.id} declares an unknown capability $capability",
                    MobileModuleCapabilities.METHOD_CAPABILITIES.containsValue(capability),
                )
            }
        }
    }

    @Test
    fun theCatalogDescribesExactlyTheShippedPackages() {
        val directory = findModuleDirectory()
        assumeTrue("this checkout has no android_modules directory", directory != null)
        val root = requireNotNull(directory)
        val catalogFile = File(root, "index.json")
        assumeTrue("this checkout has no module catalog", catalogFile.isFile)

        val catalog = MobileJson.parse(catalogFile.readText(Charsets.UTF_8))
        val modules = catalog.field("modules")?.asArrayOrNull().orEmpty()
        assertTrue("the catalog must list at least one module", modules.isNotEmpty())

        modules.forEach { entry ->
            val fileName = entry.stringField("file")
            assertTrue("catalog entry without a file", fileName != null)
            val packageFile = File(root, requireNotNull(fileName))
            assertTrue("catalog points at a missing package: $fileName", packageFile.isFile)

            assertEquals(
                "catalog size must match $fileName",
                packageFile.length(),
                entry.intField("sizeBytes")?.toLong(),
            )
            assertEquals(
                "catalog digest must match $fileName",
                MobileModuleManifest.sha256Hex(packageFile.readBytes()),
                entry.stringField("sha256"),
            )

            // The catalog must describe the same module the package carries.
            val archive = MobileModuleArchive.read(packageFile.readBytes())
            assertEquals(archive.manifest.id, entry.stringField("id"))
            assertEquals(archive.manifest.version, entry.stringField("version"))
            assertEquals(archive.manifest.payloadHash, entry.stringField("payloadHash"))
        }
    }

    private fun findModuleDirectory(): File? {
        var directory: File? = File(System.getProperty("user.dir") ?: ".")
        while (directory != null) {
            val candidate = File(directory, MODULES_DIRECTORY_NAME)
            if (File(candidate, "index.json").isFile) return candidate
            directory = directory.parentFile
        }
        return null
    }

    private companion object {
        const val MODULES_DIRECTORY_NAME = "android_modules"
        const val PACKAGE_SUFFIX = ".qmod"
    }
}
