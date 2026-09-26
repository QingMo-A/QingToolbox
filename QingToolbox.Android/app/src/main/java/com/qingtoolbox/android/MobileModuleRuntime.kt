package com.qingtoolbox.android

import android.content.Context
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.view.ViewGroup
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.webkit.WebViewClient
import java.io.ByteArrayInputStream
import java.io.File

internal const val MOBILE_MODULE_HOST = "module.qing.local"
internal const val MOBILE_MODULE_ORIGIN = "https://$MOBILE_MODULE_HOST"
internal const val MOBILE_MODULE_SHELL_PREFIX = "/shell/"
internal const val MOBILE_MODULE_BRIDGE_SCRIPT = "qing-bridge.js"
internal const val MOBILE_MODULE_BRIDGE_NAME = "qingHost"

/**
 * Holds every loaded module runtime.
 *
 * Loading is the shell's version of "into memory": the module keeps a live WebView whose
 * page state survives navigation, and unloading destroys it. Nothing here is persisted —
 * a fresh process starts with every module unloaded, which is exactly what the modules
 * page reports.
 */
class MobileModuleRuntime(context: Context) {
    private val appContext = context.applicationContext
    private val sessions = LinkedHashMap<String, MobileModuleSession>()
    private val lock = Any()
    private var themeCss: String = ""

    val loadedIds: Set<String>
        get() = synchronized(lock) { sessions.keys.toSet() }

    fun isLoaded(id: String): Boolean = synchronized(lock) { sessions.containsKey(id) }

    fun load(module: InstalledMobileModule): MobileModuleSession = synchronized(lock) {
        sessions[module.id]?.let { return it }
        return MobileModuleSession(appContext, module).also {
            it.themeCss = themeCss
            sessions[module.id] = it
        }
    }

    fun sessionFor(id: String): MobileModuleSession? = synchronized(lock) { sessions[id] }

    fun unload(id: String) {
        synchronized(lock) { sessions.remove(id) }?.dispose()
    }

    fun unloadAll() {
        val pending = synchronized(lock) {
            val copy = sessions.values.toList()
            sessions.clear()
            copy
        }
        pending.forEach { it.dispose() }
    }

    /** Pushes the current shell palette into every live module page. */
    fun updateTheme(css: String) {
        val targets = synchronized(lock) {
            if (css == themeCss) return
            themeCss = css
            sessions.values.toList()
        }
        targets.forEach { it.applyTheme(css) }
    }
}

/**
 * One loaded module: its WebView, its bridge and its pending picker request.
 *
 * The WebView is created lazily and kept alive across navigation, so returning to a loaded
 * module shows the same page state instead of reloading it.
 */
class MobileModuleSession internal constructor(
    private val context: Context,
    val module: InstalledMobileModule,
) {
    private val mainHandler = Handler(Looper.getMainLooper())
    private var createdWebView: WebView? = null
    private var bridge: MobileModuleHostBridge? = null
    private var pendingPick: ((Uri?) -> Unit)? = null
    private var filePicker: (() -> Unit)? = null

    internal var themeCss: String = ""

    val entryUrl: String = "$MOBILE_MODULE_ORIGIN/${module.manifest.entry}"

    private fun hostBridge(): MobileModuleHostBridge =
        bridge ?: MobileModuleHostBridge(context, module, this).also { bridge = it }

    /**
     * Returns the module's WebView, creating it on first use.
     *
     * The live view is cached rather than recreated, so a loaded module keeps its page
     * state when you navigate away and back. It is detached from whatever container held it
     * before, because Compose adds it to a new parent every time the screen is entered.
     */
    internal fun attach(hostContext: Context): WebView {
        createdWebView?.let { existing ->
            (existing.parent as? ViewGroup)?.removeView(existing)
            return existing
        }
        val view = createWebView(hostContext)
        createdWebView = view
        return view
    }

    private fun createWebView(hostContext: Context): WebView {
        val view = WebView(hostContext)
        view.settings.apply {
            javaScriptEnabled = true
            domStorageEnabled = true
            allowFileAccess = false
            allowContentAccess = false
            setSupportZoom(false)
            builtInZoomControls = false
            displayZoomControls = false
            javaScriptCanOpenWindowsAutomatically = false
            setGeolocationEnabled(false)
            mediaPlaybackRequiresUserGesture = true
        }
        view.webViewClient = MobileModuleWebClient(context, module.directory) { themeCss }
        view.addJavascriptInterface(hostBridge(), MOBILE_MODULE_BRIDGE_NAME)
        view.loadUrl(entryUrl)
        return view
    }

    /** Runs [css] against the live page so a theme change does not need a reload. */
    internal fun applyTheme(css: String) {
        val view = createdWebView ?: return
        mainHandler.post {
            view.evaluateJavascript(
                "(function(){var s=document.getElementById('qing-host-theme');" +
                    "if(!s){s=document.createElement('style');s.id='qing-host-theme';" +
                    "(document.head||document.documentElement).appendChild(s);}s.textContent=${MobileJson.quote(css)};})()",
                null,
            )
        }
    }

    internal fun registerFilePicker(picker: (() -> Unit)?) {
        filePicker = picker
    }

    internal fun deliverPickedFile(uri: Uri?) {
        val pending = pendingPick ?: return
        pendingPick = null
        pending(uri)
    }

    /** Asks the visible screen to open the system file picker on the bridge's behalf. */
    internal fun beginFilePick(onResult: (Uri?) -> Unit) {
        mainHandler.post {
            val picker = filePicker
            if (picker == null) {
                onResult(null)
                return@post
            }
            pendingPick = onResult
            picker()
        }
    }

    /** Answers a pending bridge request. Must be safe to call from any thread. */
    internal fun respond(requestId: String, payload: String) {
        val view = createdWebView ?: return
        mainHandler.post {
            view.evaluateJavascript(
                "window.__qingResolve(${MobileJson.quote(requestId)},${MobileJson.quote(payload)})",
                null,
            )
        }
    }

    fun dispose() {
        bridge?.dispose()
        bridge = null
        val view = createdWebView ?: return
        createdWebView = null
        val destroy: () -> Unit = {
            runCatching { view.removeJavascriptInterface(MOBILE_MODULE_BRIDGE_NAME) }
            runCatching { view.stopLoading() }
            runCatching { view.loadUrl("about:blank") }
            runCatching { view.destroy() }
            Unit
        }
        if (Looper.myLooper() == Looper.getMainLooper()) destroy() else mainHandler.post(destroy)
    }
}

/**
 * Serves a module from its own directory on disk, and nothing else.
 *
 * Every request for the module's origin is answered from the module folder or from the
 * shell's own web assets; any other host is refused outright, so an imported module is
 * offline by construction rather than by policy. HTML responses get the shell palette and
 * the bridge script injected before the module's own assets run.
 */
private class MobileModuleWebClient(
    private val context: Context,
    private val moduleDirectory: File,
    private val themeCssProvider: () -> String,
) : WebViewClient() {
    private val canonicalRoot: File? by lazy { runCatching { moduleDirectory.canonicalFile }.getOrNull() }

    override fun shouldInterceptRequest(
        view: WebView,
        request: WebResourceRequest,
    ): WebResourceResponse? {
        val url = request.url ?: return refused(404, "Not Found")
        if (url.host != MOBILE_MODULE_HOST) return refused(403, "Forbidden")
        val path = url.path ?: return refused(404, "Not Found")

        if (path.startsWith(MOBILE_MODULE_SHELL_PREFIX)) {
            val asset = path.removePrefix(MOBILE_MODULE_SHELL_PREFIX)
            return serveAsset(asset) ?: refused(404, "Not Found")
        }

        val relative = path.removePrefix("/")
        if (!MobileModulePaths.isSafeRelativePath(relative)) return refused(403, "Forbidden")
        val root = canonicalRoot ?: return refused(404, "Not Found")
        val target = runCatching { File(root, relative).canonicalFile }.getOrNull()
            ?: return refused(404, "Not Found")
        if (!target.path.startsWith(root.path + File.separator) || !target.isFile) {
            return refused(404, "Not Found")
        }

        val mime = MobileModuleMime.of(target.name)
        val bytes = runCatching { target.readBytes() }.getOrNull() ?: return refused(404, "Not Found")
        val payload = if (mime == "text/html") {
            injectHostAssets(bytes.toString(Charsets.UTF_8)).toByteArray(Charsets.UTF_8)
        } else {
            bytes
        }
        return response(mime, payload)
    }

    private fun serveAsset(relative: String): WebResourceResponse? {
        if (relative.isEmpty() || !MobileModulePaths.isSafeRelativePath(relative)) return null
        val bytes = runCatching { context.assets.open("shell/$relative").use { it.readBytes() } }.getOrNull()
            ?: return null
        return response(MobileModuleMime.of(relative), bytes)
    }

    private fun injectHostAssets(html: String): String {
        val injection = buildString {
            append("<style id=\"qing-host-theme\">").append(themeCssProvider()).append("</style>")
            append("<style id=\"qing-host-base\">").append(shellBaseCss(context)).append("</style>")
            append("<script src=\"").append(MOBILE_MODULE_SHELL_PREFIX)
                .append(MOBILE_MODULE_BRIDGE_SCRIPT).append("\"></script>")
        }
        val head = Regex("<head[^>]*>", RegexOption.IGNORE_CASE).find(html) ?: return injection + html
        val insertAt = head.range.last + 1
        return html.substring(0, insertAt) + injection + html.substring(insertAt)
    }

    private fun response(mime: String, bytes: ByteArray): WebResourceResponse {
        val textual = mime.startsWith("text/") ||
            mime.contains("javascript") ||
            mime.contains("json") ||
            mime.contains("xml") ||
            mime.contains("svg")
        return WebResourceResponse(
            mime,
            if (textual) "UTF-8" else null,
            200,
            "OK",
            mapOf("Cache-Control" to "no-store"),
            ByteArrayInputStream(bytes),
        )
    }

    private fun refused(status: Int, reason: String): WebResourceResponse = WebResourceResponse(
        "text/plain",
        "UTF-8",
        status,
        reason,
        mapOf("Cache-Control" to "no-store"),
        ByteArrayInputStream(ByteArray(0)),
    )
}

/** Only the file types a web module may legitimately ship. */
internal object MobileModuleMime {
    fun of(name: String): String = when (name.substringAfterLast('.', "").lowercase()) {
        "html", "htm" -> "text/html"
        "css" -> "text/css"
        "js", "mjs" -> "application/javascript"
        "json" -> "application/json"
        "svg" -> "image/svg+xml"
        "png" -> "image/png"
        "jpg", "jpeg" -> "image/jpeg"
        "gif" -> "image/gif"
        "webp" -> "image/webp"
        "ico" -> "image/x-icon"
        "txt" -> "text/plain"
        "woff" -> "font/woff"
        "woff2" -> "font/woff2"
        "ttf" -> "font/ttf"
        "otf" -> "font/otf"
        "wasm" -> "application/wasm"
        else -> "application/octet-stream"
    }
}

/** The shell stylesheet, read once and reused for every module page. */
private var cachedShellBaseCss: String? = null

internal fun shellBaseCss(context: Context): String {
    cachedShellBaseCss?.let { return it }
    val css = runCatching {
        context.assets.open("shell/module-base.css").use { it.readBytes().toString(Charsets.UTF_8) }
    }.getOrDefault("")
    cachedShellBaseCss = css
    return css
}
