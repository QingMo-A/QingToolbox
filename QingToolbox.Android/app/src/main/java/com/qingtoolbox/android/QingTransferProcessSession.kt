package com.qingtoolbox.android

import android.content.Context
import android.os.Build
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** Process-scoped holder so an established socket survives navigation and document pickers. */
internal class QingTransferProcessSession internal constructor(context: Context) {
    private val _peers = MutableStateFlow<List<QingTransferPeer>>(emptyList())
    val peers: StateFlow<List<QingTransferPeer>> = _peers.asStateFlow()
    private val _discoveryState = MutableStateFlow(QingTransferDiscoveryState.IDLE)
    val discoveryState: StateFlow<QingTransferDiscoveryState> = _discoveryState.asStateFlow()

    val discovery = QingTransferDiscovery(
        context = context.applicationContext,
        onPeersChanged = { _peers.value = it },
        onStateChanged = { _discoveryState.value = it },
    )
    val connection = QingTransferConnection(
        context = context.applicationContext,
        discovery = discovery,
        friendlyName = QingTransferMetadata.sanitizeName(Build.MODEL),
        receivePreferences = QingTransferReceivePreferencesStore(context.applicationContext),
    )
}

internal object QingTransferProcessSessionStore {
    @Volatile
    private var current: QingTransferProcessSession? = null

    fun get(context: Context): QingTransferProcessSession = synchronized(this) {
        current ?: QingTransferProcessSession(context).also { current = it }
    }
}
