package com.qingtoolbox.android

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.ext.SdkExtensions
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.ConcurrentHashMap

data class QingTransferPeer(
    val serviceName: String,
    val displayName: String,
    val platform: String,
    val protocolVersion: String,
    val capabilities: List<String>,
    val addresses: List<String>,
    val port: Int,
    val online: Boolean = true,
    val lastSeen: Long? = null,
)

internal class QingTransferPeerTable {
    private val peers = LinkedHashMap<String, QingTransferPeer>()

    @Synchronized
    fun upsert(peer: QingTransferPeer): Boolean {
        val key = normalize(peer.serviceName)
        if (peers[key] == peer) return false
        peers[key] = peer
        return true
    }

    @Synchronized
    fun remove(serviceName: String): Boolean = peers.remove(normalize(serviceName)) != null

    @Synchronized
    fun snapshot(): List<QingTransferPeer> = peers.values.sortedBy { it.displayName.lowercase() }

    @Synchronized
    fun clear() = peers.clear()

    private fun normalize(serviceName: String): String = serviceName.trim().trimEnd('.').lowercase()
}

enum class QingTransferDiscoveryState {
    IDLE,
    SEARCHING,
    READY,
    ERROR,
}

/** Event-driven Android DNS-SD session. It owns only a temporary advertisement socket. */
class QingTransferDiscovery(
    context: Context,
    private val onPeersChanged: (List<QingTransferPeer>) -> Unit,
    private val onStateChanged: (QingTransferDiscoveryState) -> Unit,
) {
    @Volatile
    var onIncomingSocket: ((Socket) -> Unit)? = null
    private val appContext = context.applicationContext
    private val nsd = appContext.getSystemService(NsdManager::class.java)
    private val peers = QingTransferPeerTable()
    private val resolving = ConcurrentHashMap.newKeySet<String>()
    private val resolveListeners = ConcurrentHashMap<String, NsdManager.ResolveListener>()
    private var registrationListener: NsdManager.RegistrationListener? = null
    private var discoveryListener: NsdManager.DiscoveryListener? = null
    private var actualServiceName: String? = null
    private var expectedServiceName: String? = null
    private var serverSocket: ServerSocket? = null
    private var acceptThread: Thread? = null
    private var multicastLock: WifiManager.MulticastLock? = null
    private var active = false
    private var localPort = 0

    @Synchronized
    fun start() {
        if (active) return
        active = true
        onStateChanged(QingTransferDiscoveryState.SEARCHING)
        openEphemeralListener()
        acquireLegacyMulticastLock()
        startDiscovery()
        startAdvertisement()
    }

    @Synchronized
    fun stop() {
        if (!active) return
        active = false
        registrationListener?.let { runCatching { nsd?.unregisterService(it) } }
        discoveryListener?.let { runCatching { nsd?.stopServiceDiscovery(it) } }
        stopOutstandingResolutions()
        registrationListener = null
        discoveryListener = null
        actualServiceName = null
        expectedServiceName = null
        resolving.clear()
        resolveListeners.clear()
        runCatching { serverSocket?.close() }
        serverSocket = null
        acceptThread?.interrupt()
        acceptThread = null
        multicastLock?.let { lock -> runCatching { if (lock.isHeld) lock.release() } }
        multicastLock = null
        peers.clear()
        onPeersChanged(emptyList())
        onStateChanged(QingTransferDiscoveryState.IDLE)
    }

    @Synchronized
    fun restart() {
        stop()
        start()
    }

    fun snapshot(): List<QingTransferPeer> = peers.snapshot()

    private fun openEphemeralListener() {
        runCatching {
            serverSocket = ServerSocket(0)
            localPort = serverSocket?.localPort ?: 0
            val socket = serverSocket ?: return
            acceptThread = Thread {
                while (active && !Thread.currentThread().isInterrupted) {
                    try {
                        val client = socket.accept()
                        val handler = onIncomingSocket
                        if (!active || handler == null) client.close() else handler(client)
                    } catch (_: Exception) {
                        if (active) continue
                        break
                    }
                }
            }.apply {
                name = "qingtransfer-listener"
                isDaemon = true
                start()
            }
        }.onFailure { onStateChanged(QingTransferDiscoveryState.ERROR) }
    }

    private fun acquireLegacyMulticastLock() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) return
        val wifi = appContext.getSystemService(WifiManager::class.java) ?: return
        runCatching {
            multicastLock = wifi.createMulticastLock("qingtransfer-discovery").apply {
                setReferenceCounted(false)
                acquire()
            }
        }
    }

    private fun startAdvertisement() {
        val manager = nsd ?: return
        if (localPort !in 1..65535) return
        val friendlyName = QingTransferMetadata.sanitizeName(Build.MODEL)
        val info = NsdServiceInfo().apply {
            serviceName = friendlyName
            serviceType = QingTransferMetadata.androidServiceType
            port = localPort
            setAttribute("v", "1")
            setAttribute("pf", "android")
            setAttribute("name", friendlyName)
            setAttribute("cap", "file")
        }
        expectedServiceName = QingTransferMetadata.fullServiceName(friendlyName)
        val listener = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(serviceInfo: NsdServiceInfo) {
                if (!active) return
                actualServiceName = QingTransferMetadata.fullServiceName(serviceInfo.serviceName)
                expectedServiceName = null
                removePeer(actualServiceName!!)
            }
            override fun onRegistrationFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                expectedServiceName = null
                if (active) onStateChanged(QingTransferDiscoveryState.ERROR)
            }
            override fun onServiceUnregistered(serviceInfo: NsdServiceInfo) = Unit
            override fun onUnregistrationFailed(serviceInfo: NsdServiceInfo, errorCode: Int) = Unit
        }
        registrationListener = listener
        runCatching {
            if (supportsExecutorNsdApi()) {
                manager.registerService(info, NsdManager.PROTOCOL_DNS_SD, appContext.mainExecutor, listener)
            } else {
                manager.registerService(info, NsdManager.PROTOCOL_DNS_SD, listener)
            }
        }
            .onFailure { if (active) onStateChanged(QingTransferDiscoveryState.ERROR) }
    }

    private fun startDiscovery() {
        val manager = nsd ?: return
        val listener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) = Unit
            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                if (!active || !serviceInfo.serviceType.contains(QingTransferMetadata.serviceType, ignoreCase = true)) return
                val name = QingTransferMetadata.fullServiceName(serviceInfo.serviceName)
                if (isSelf(name) || !resolving.add(name)) return
                resolve(manager, serviceInfo, name)
            }
            override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                removePeer(QingTransferMetadata.fullServiceName(serviceInfo.serviceName))
            }
            override fun onDiscoveryStopped(serviceType: String) = Unit
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                runCatching { manager.stopServiceDiscovery(this) }
                if (active) onStateChanged(QingTransferDiscoveryState.ERROR)
            }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) = Unit
        }
        discoveryListener = listener
        runCatching {
            if (supportsExecutorNsdApi()) {
                manager.discoverServices(
                    QingTransferMetadata.androidServiceType,
                    NsdManager.PROTOCOL_DNS_SD,
                    null,
                    appContext.mainExecutor,
                    listener,
                )
            } else {
                manager.discoverServices(QingTransferMetadata.androidServiceType, NsdManager.PROTOCOL_DNS_SD, listener)
            }
        }
            .onFailure { if (active) onStateChanged(QingTransferDiscoveryState.ERROR) }
    }

    @Suppress("DEPRECATION")
    private fun resolve(manager: NsdManager, found: NsdServiceInfo, serviceName: String) {
        val listener = object : NsdManager.ResolveListener {
                override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                    resolving.remove(serviceName)
                    resolveListeners.remove(serviceName)
                    if (!active || isSelf(serviceName)) return
                    val attributes = serviceInfo.attributes.mapValues { (_, value) -> value.toString(Charsets.UTF_8) }
                    val resolvedName = QingTransferMetadata.fullServiceName(serviceInfo.serviceName)
                    val peer = QingTransferMetadata.parse(
                        serviceName = resolvedName,
                        fields = attributes,
                        addresses = listOfNotNull(serviceInfo.host?.hostAddress),
                        port = serviceInfo.port,
                        lastSeen = System.currentTimeMillis(),
                    )
                    if (peer is QingTransferMetadata.ParseResult.Valid) upsertPeer(peer.peer)
                    else removePeer(serviceName)
                }
                override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                    resolving.remove(serviceName)
                    resolveListeners.remove(serviceName)
                    if (active) removePeer(serviceName)
                }
            }
        resolveListeners[serviceName] = listener
        runCatching {
            if (supportsExecutorNsdApi()) {
                manager.resolveService(found, appContext.mainExecutor, listener)
            } else {
                @Suppress("DEPRECATION")
                manager.resolveService(found, listener)
            }
        }.onFailure {
            resolving.remove(serviceName)
            resolveListeners.remove(serviceName)
            if (active) removePeer(serviceName)
        }
    }

    private fun upsertPeer(peer: QingTransferPeer) {
        peers.upsert(peer)
        onPeersChanged(snapshot())
        onStateChanged(QingTransferDiscoveryState.READY)
    }

    private fun removePeer(serviceName: String) {
        peers.remove(serviceName)
        onPeersChanged(snapshot())
    }

    private fun isSelf(serviceName: String): Boolean =
        actualServiceName?.equals(serviceName, ignoreCase = true) == true ||
            expectedServiceName?.equals(serviceName, ignoreCase = true) == true

    @Suppress("NewApi")
    private fun stopOutstandingResolutions() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE ||
            (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R &&
                SdkExtensions.getExtensionVersion(Build.VERSION_CODES.TIRAMISU) >= 7)
        ) {
            resolveListeners.values.forEach { listener -> runCatching { nsd?.stopServiceResolution(listener) } }
        }
    }

    /** T extension 3 exposes the executor NSD overloads on supported pre-33 devices. */
    @Suppress("NewApi")
    private fun supportsExecutorNsdApi(): Boolean =
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU ||
            (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R &&
                SdkExtensions.getExtensionVersion(Build.VERSION_CODES.TIRAMISU) >= 3)

}
