package com.qingtoolbox.android

import androidx.annotation.StringRes

/**
 * The capability channel between a module and the shell.
 *
 * A module never touches Android APIs directly. It declares what it needs in its manifest
 * and the shell either performs the work on its behalf or refuses the call, so a module
 * cannot use a capability it did not ask the user about.
 */
object MobileModuleCapabilities {
    const val TEXT_CODEC = "text.codec"
    const val DEVICE_INFO = "device.info"
    const val FILE_HASH = "file.hash"
    const val GRAPHICS_QR = "graphics.qr"
    const val GRAPHICS_SHARE = "graphics.share"
    const val CLIPBOARD_WRITE = "clipboard.write"

    /** Bridge methods every module may call without declaring a capability. */
    val ALWAYS_ALLOWED = setOf("host.info", "toast.show")

    /** Bridge method to the capability it requires. */
    val METHOD_CAPABILITIES: Map<String, String> = linkedMapOf(
        "text.codec" to TEXT_CODEC,
        "device.snapshot" to DEVICE_INFO,
        "file.hash" to FILE_HASH,
        "graphics.qr" to GRAPHICS_QR,
        "graphics.share" to GRAPHICS_SHARE,
        "clipboard.write" to CLIPBOARD_WRITE,
    )

    /** Methods that answer later, once the user or the system has replied. */
    val ASYNC_METHODS = setOf("file.hash", "graphics.share")

    /** Human label for a declared capability, or `null` when the shell does not know it. */
    @StringRes
    fun labelRes(capability: String): Int? = when (capability) {
        TEXT_CODEC -> R.string.capability_text_codec
        DEVICE_INFO -> R.string.capability_device_info
        FILE_HASH -> R.string.capability_file_hash
        GRAPHICS_QR -> R.string.capability_graphics_qr
        GRAPHICS_SHARE -> R.string.capability_graphics_share
        CLIPBOARD_WRITE -> R.string.capability_clipboard_write
        else -> null
    }
}
