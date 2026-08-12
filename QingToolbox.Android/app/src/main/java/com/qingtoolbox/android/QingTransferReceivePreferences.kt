package com.qingtoolbox.android

import android.content.Context

internal data class QingTransferReceivePreferences(
    val defaultTreeUri: String? = null,
    val useDefaultDirectory: Boolean = false,
    val autoAccept: Boolean = false,
)

internal class QingTransferReceivePreferencesStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences("qing_transfer_receive", Context.MODE_PRIVATE)

    fun read(): QingTransferReceivePreferences = QingTransferReceivePreferences(
        defaultTreeUri = preferences.getString(KEY_URI, null),
        useDefaultDirectory = preferences.getBoolean(KEY_USE_DEFAULT, false),
        autoAccept = preferences.getBoolean(KEY_AUTO_ACCEPT, false),
    )

    fun write(value: QingTransferReceivePreferences) {
        preferences.edit()
            .putString(KEY_URI, value.defaultTreeUri)
            .putBoolean(KEY_USE_DEFAULT, value.useDefaultDirectory)
            .putBoolean(KEY_AUTO_ACCEPT, value.autoAccept)
            .apply()
    }

    private companion object {
        const val KEY_URI = "default_tree_uri"
        const val KEY_USE_DEFAULT = "use_default_directory"
        const val KEY_AUTO_ACCEPT = "auto_accept"
    }
}

internal object QingTransferReceivePolicy {
    fun automaticAcceptAllowed(settings: QingTransferReceivePreferences, destinationValid: Boolean): Boolean =
        settings.autoAccept && settings.useDefaultDirectory && !settings.defaultTreeUri.isNullOrBlank() && destinationValid

    fun nextFileName(name: String, existing: Set<String>): String {
        val dot = name.lastIndexOf('.')
        val base = if (dot > 0) name.substring(0, dot) else name
        val extension = if (dot > 0) name.substring(dot) else ""
        for (index in 0 until 1000) {
            val suffix = if (index == 0) "" else " ($index)"
            val candidate = base + suffix + extension
            if (candidate !in existing) return candidate
        }
        return name
    }
}
