package com.qingtoolbox.android

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.Color
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.provider.OpenableColumns
import android.widget.Toast
import android.webkit.JavascriptInterface
import androidx.core.content.FileProvider
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.Base64
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

/** Raised for a call the shell refuses or cannot complete. The message reaches the page. */
class MobileModuleBridgeException(message: String) : IllegalStateException(message)

/**
 * The only way a module can reach Android.
 *
 * Every call is checked against the capabilities the module declared in its manifest, so a
 * page cannot use a channel the user never agreed to. Calls either answer immediately
 * (`call`) or later (`invoke`), and the later ones always resolve back into the page
 * through the session.
 */
class MobileModuleHostBridge(
    private val context: Context,
    private val module: InstalledMobileModule,
    private val session: MobileModuleSession,
) {
    private val mainHandler = Handler(Looper.getMainLooper())
    private val worker: ExecutorService = Executors.newSingleThreadExecutor { runnable ->
        Thread(runnable, "qing-module-${module.id}")
    }

    /** Answers a method that needs no user interaction. */
    @JavascriptInterface
    fun call(method: String, paramsJson: String): String = envelope {
        val params = parseParams(paramsJson)
        when (method) {
            "host.info" -> hostInfo()
            "toast.show" -> {
                showToast(params.stringField("text").orEmpty())
                "{}"
            }
            else -> {
                requireCapability(method)
                if (method in MobileModuleCapabilities.ASYNC_METHODS) {
                    throw MobileModuleBridgeException("Method '$method' answers later")
                }
                dispatch(method, params)
            }
        }
    }

    /** Starts a method whose answer arrives once the user or the system has replied. */
    @JavascriptInterface
    fun invoke(requestId: String, method: String, paramsJson: String): String = envelope {
        val params = parseParams(paramsJson)
        requireCapability(method)
        when (method) {
            "file.hash" -> {
                val algorithms = requestedAlgorithms(params)
                session.beginFilePick { uri ->
                    if (uri == null) {
                        session.respond(requestId, failureEnvelope("file-pick-cancelled"))
                        return@beginFilePick
                    }
                    worker.execute { session.respond(requestId, hashPickedFile(uri, algorithms)) }
                }
                "{}"
            }
            "graphics.share" -> {
                val dataUrl = params.stringField("dataUrl").orEmpty()
                val requestedName = params.stringField("name").orEmpty()
                // The share sheet is started from the main thread; the image is small enough
                // that decoding it there costs nothing measurable.
                mainHandler.post { session.respond(requestId, shareImage(dataUrl, requestedName)) }
                "{}"
            }
            else -> throw MobileModuleBridgeException("Method '$method' does not answer later")
        }
    }

    internal fun dispose() {
        worker.shutdownNow()
    }

    private fun hostInfo(): String = MobileJsonObject()
        .string("moduleId", module.id)
        .string("moduleVersion", module.version)
        .int("apiVersion", MobileModuleManifest.API_VERSION)
        .string("shellVersion", BuildConfig.VERSION_NAME)
        .string("locale", currentLanguageTag())
        .strings("capabilities", module.manifest.capabilities)
        .build()

    private fun dispatch(method: String, params: MobileJsonValue): String = when (method) {
        "text.codec" -> {
            val operation = when (val name = params.stringField("operation")) {
                "base64-encode" -> TextCodecOperation.BASE64_ENCODE
                "base64-decode" -> TextCodecOperation.BASE64_DECODE
                "url-encode" -> TextCodecOperation.URL_ENCODE
                "url-decode" -> TextCodecOperation.URL_DECODE
                else -> throw MobileModuleBridgeException("Unsupported text codec operation '$name'")
            }
            val result = try {
                TextCodec.convert(operation, params.stringField("input").orEmpty())
            } catch (exception: TextCodecException) {
                throw MobileModuleBridgeException(exception.error.name)
            }
            MobileJsonObject().string("result", result).build()
        }
        "device.snapshot" -> deviceSnapshot()
        "graphics.qr" -> {
            val text = params.stringField("text").orEmpty()
            if (text.isEmpty()) throw MobileModuleBridgeException("qr-input-empty")
            val matrix = try {
                QrCodeEncoder.encode(text)
            } catch (_: Exception) {
                throw MobileModuleBridgeException("qr-failed")
            }
            MobileJsonObject()
                .string("png", qrDataUrl(matrix))
                .int("modules", matrix.size)
                .build()
        }
        "clipboard.write" -> {
            copyToClipboard(params.stringField("text").orEmpty())
            "{}"
        }
        else -> throw MobileModuleBridgeException("Unknown method '$method'")
    }

    private fun requireCapability(method: String) {
        if (method in MobileModuleCapabilities.ALWAYS_ALLOWED) return
        val required = MobileModuleCapabilities.METHOD_CAPABILITIES[method]
            ?: throw MobileModuleBridgeException("Unknown method '$method'")
        if (required !in module.manifest.capabilities) {
            throw MobileModuleBridgeException("The module did not declare the '$required' capability")
        }
    }

    private fun requestedAlgorithms(params: MobileJsonValue): List<HashAlgorithm> {
        val requested = params.field("algorithms")?.asStringArrayOrNull() ?: return DEFAULT_ALGORITHMS
        val mapped = requested.mapNotNull { name ->
            when (name.trim().lowercase()) {
                "md5" -> HashAlgorithm.MD5
                "sha1", "sha-1" -> HashAlgorithm.SHA_1
                "sha256", "sha-256" -> HashAlgorithm.SHA_256
                "sha512", "sha-512" -> HashAlgorithm.SHA_512
                else -> null
            }
        }.distinct()
        return mapped.ifEmpty { DEFAULT_ALGORITHMS }
    }

    private fun hashPickedFile(uri: Uri, algorithms: List<HashAlgorithm>): String = envelope {
        val resolver = context.contentResolver
        var displayName = "file"
        var size = 0L
        resolver.query(uri, null, null, null, null)?.use { cursor ->
            if (cursor.moveToFirst()) {
                val nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                if (nameIndex >= 0 && !cursor.isNull(nameIndex)) {
                    displayName = cursor.getString(nameIndex) ?: displayName
                }
                val sizeIndex = cursor.getColumnIndex(OpenableColumns.SIZE)
                if (sizeIndex >= 0 && !cursor.isNull(sizeIndex)) {
                    size = cursor.getLong(sizeIndex)
                }
            }
        }

        val digests = resolver.openInputStream(uri)?.use { stream ->
            StreamingDigest.calculate(stream, algorithms)
        } ?: throw MobileModuleBridgeException("file-open-failed")

        val hashes = MobileJsonObject()
        digests.forEach { digest -> hashes.string(digest.algorithm.bridgeKey(), digest.value) }
        MobileJsonObject()
            .string("name", displayName)
            .string("size", size.toString())
            .objectValue("hashes", hashes.build())
            .build()
    }

    private fun shareImage(dataUrl: String, requestedName: String): String = envelope {
        val bytes = decodeDataUrl(dataUrl) ?: throw MobileModuleBridgeException("image-unavailable")
        val baseName = requestedName
            .filter { it.isLetterOrDigit() || it == '-' || it == '_' }
            .take(24)
            .ifEmpty { "module-image" }
        val name = "$baseName.png"
        val directory = File(context.cacheDir, SHARE_DIRECTORY).apply { mkdirs() }
        val file = File(directory, name)
        file.writeBytes(bytes)

        val uri = FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)
        val share = Intent(Intent.ACTION_SEND).apply {
            type = "image/png"
            putExtra(Intent.EXTRA_STREAM, uri)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        val chooser = Intent.createChooser(share, null).apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        context.startActivity(chooser)
        MobileJsonObject().string("name", name).build()
    }

    private fun deviceSnapshot(): String {
        val snapshot = DeviceInfoProvider.read(context)
        val unavailable = context.getString(R.string.unavailable)
        fun section(title: String, entries: List<Pair<String, String>>): String = MobileJsonObject()
            .string("title", title)
            .objectValue(
                "items",
                entries.joinToString(prefix = "[", postfix = "]") { (label, value) ->
                    MobileJsonObject().string("label", label).string("value", value).build()
                },
            )
            .build()

        val sections = listOf(
            section(
                "device",
                listOf(
                    "manufacturer" to snapshot.manufacturer,
                    "brand" to snapshot.brand,
                    "model" to snapshot.model,
                    "codename" to snapshot.device,
                    "product" to snapshot.product,
                ),
            ),
            section(
                "android",
                listOf(
                    "release" to snapshot.androidVersion,
                    "apiLevel" to snapshot.apiLevel.toString(),
                    "buildId" to snapshot.buildId,
                    "securityPatch" to DeviceInfoFormat.securityPatch(snapshot.securityPatch, unavailable),
                ),
            ),
            section(
                "hardware",
                listOf(
                    "abis" to DeviceInfoFormat.abiList(snapshot.supportedAbis, unavailable),
                    "processors" to snapshot.availableProcessors.toString(),
                ),
            ),
            section(
                "display",
                listOf(
                    "size" to DeviceInfoFormat.displaySize(snapshot.displayWidthPixels, snapshot.displayHeightPixels),
                    "density" to DeviceInfoFormat.density(snapshot.displayDensity),
                    "densityDpi" to DeviceInfoFormat.densityDpi(snapshot.displayDensityDpi),
                ),
            ),
            section(
                "app",
                listOf(
                    "shellVersion" to BuildConfig.VERSION_NAME,
                    "shellVersionCode" to snapshot.appVersionCode.toString(),
                    "shellPackage" to snapshot.appPackageName,
                ),
            ),
        )
        return MobileJsonObject()
            .objectValue("sections", sections.joinToString(prefix = "[", postfix = "]"))
            .build()
    }

    private fun qrDataUrl(matrix: QrMatrix): String {
        val size = matrix.size
        val pixels = IntArray(size * size)
        for (y in 0 until size) {
            for (x in 0 until size) {
                pixels[y * size + x] = if (matrix.isDark(x, y)) Color.BLACK else Color.WHITE
            }
        }
        val bitmap = Bitmap.createBitmap(size, size, Bitmap.Config.ARGB_8888).apply {
            setPixels(pixels, 0, size, 0, 0, size, size)
        }
        val stream = ByteArrayOutputStream()
        bitmap.compress(Bitmap.CompressFormat.PNG, 100, stream)
        bitmap.recycle()
        return "data:image/png;base64," + Base64.getEncoder().encodeToString(stream.toByteArray())
    }

    private fun decodeDataUrl(dataUrl: String): ByteArray? {
        val separator = dataUrl.indexOf(",")
        if (separator <= 0 || !dataUrl.startsWith("data:image/")) return null
        val payload = dataUrl.substring(separator + 1)
        return runCatching { Base64.getDecoder().decode(payload) }.getOrNull()
    }

    private fun copyToClipboard(text: String) {
        val manager = context.getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
            ?: throw MobileModuleBridgeException("clipboard-unavailable")
        mainHandler.post { manager.setPrimaryClip(ClipData.newPlainText("QingToolbox", text)) }
    }

    private fun showToast(message: String) {
        if (message.isBlank()) return
        mainHandler.post { Toast.makeText(context, message, Toast.LENGTH_SHORT).show() }
    }

    private fun parseParams(paramsJson: String): MobileJsonValue = try {
        if (paramsJson.isBlank()) MobileJsonValue.Obj(emptyMap()) else MobileJson.parse(paramsJson)
    } catch (_: MobileJsonException) {
        MobileJsonValue.Obj(emptyMap())
    }

    private fun envelope(block: () -> String): String = try {
        MobileJsonObject().objectValue("data", block()).build()
    } catch (exception: Exception) {
        failureEnvelope(exception.message ?: exception.javaClass.simpleName)
    }

    private fun failureEnvelope(message: String): String =
        MobileJsonObject().string("error", message).build()

    private fun currentLanguageTag(): String {
        val locales = context.resources.configuration.locales
        val locale = if (locales.isEmpty) java.util.Locale.getDefault() else locales[0]
        return locale.toLanguageTag()
    }

    private companion object {
        const val SHARE_DIRECTORY = "qr-share"

        val DEFAULT_ALGORITHMS = listOf(HashAlgorithm.MD5, HashAlgorithm.SHA_1, HashAlgorithm.SHA_256)
    }
}

/** Stable wire name for a hash algorithm, so a module never parses a display label. */
private fun HashAlgorithm.bridgeKey(): String = when (this) {
    HashAlgorithm.MD5 -> "md5"
    HashAlgorithm.SHA_1 -> "sha1"
    HashAlgorithm.SHA_256 -> "sha256"
    HashAlgorithm.SHA_512 -> "sha512"
}
