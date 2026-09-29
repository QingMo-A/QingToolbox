package com.qingtoolbox.android

import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.net.wifi.WifiManager
import android.os.Build
import android.os.BatteryManager
import android.util.AtomicFile
import java.io.File
import java.net.ServerSocket
import java.net.SocketTimeoutException
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

data class NearbyDevice(
    val serviceName: String,
    val discoveryId: String,
    val name: String,
    val platform: String,
    val address: String,
)

enum class DeviceDiscoveryError {
    UNSUPPORTED,
    START_FAILED,
    ADVERTISE_FAILED,
    SEARCH_FAILED,
}

data class DeviceDiscoverySnapshot(
    val ownName: String,
    val searching: Boolean = false,
    val available: Boolean = false,
    val error: DeviceDiscoveryError? = null,
    val nearby: List<NearbyDevice> = emptyList(),
)

/** Foreground-only built-in device discovery; legacy QingTransfer remains independent. */
class DeviceDiscoverySession(context: Context) {
    private val appContext = context.applicationContext
    private val nsd = appContext.getSystemService(NsdManager::class.java)
    private val ownName = Build.MODEL.trim().filterNot { it.isISOControl() }.take(64).ifEmpty { "Android" }
    internal val ownId = loadOrCreateDiscoveryId(appContext)
    internal val pairing = DevicePairingSession(appContext, ownId, ownName)
    private val peers = LinkedHashMap<String, NearbyDevice>()
    private val candidates = ConcurrentHashMap<String, DeviceDiscoveryProtocol.Candidate>()
    private val found = ConcurrentHashMap.newKeySet<String>()
    private val resolving = ConcurrentHashMap.newKeySet<String>()
    private val probing = ConcurrentHashMap.newKeySet<String>()
    private val workers = Executors.newFixedThreadPool(3) { task ->
        Thread(task, "qingdevice-probe").apply { isDaemon = true }
    }
    private val mutableSnapshot = MutableStateFlow(DeviceDiscoverySnapshot(ownName))
    val snapshot: StateFlow<DeviceDiscoverySnapshot> = mutableSnapshot
    private var discoveryListener: NsdManager.DiscoveryListener? = null
    private var registrationListener: NsdManager.RegistrationListener? = null
    private var server: ServerSocket? = null
    private var acceptThread: Thread? = null
    private var recheckThread: Thread? = null
    private var multicastLock: WifiManager.MulticastLock? = null
    @Volatile private var active = false
    @Volatile private var generation = 0
    @Volatile private var lastBatterySentAt = 0L
    private val owners = HashSet<String>()

    @Synchronized
    fun acquire(owner: String) {
        if (owners.add(owner) && owners.size == 1) start()
    }

    @Synchronized
    fun release(owner: String) {
        if (owners.remove(owner) && owners.isEmpty()) stop()
    }

    @Synchronized
    fun start() {
        if (active) return
        active = true
        generation++
        pairing.start()
        mutableSnapshot.value = DeviceDiscoverySnapshot(ownName, searching = true)
        val manager = nsd ?: return reportError(DeviceDiscoveryError.UNSUPPORTED)
        try {
            server = ServerSocket(0).apply { soTimeout = 500 }
            startListener(server!!)
            startRecheck()
            acquireMulticastLock()
            advertise(manager, server!!.localPort)
            browse(manager)
        } catch (_: Exception) {
            stop()
            reportError(DeviceDiscoveryError.START_FAILED)
        }
    }

    @Synchronized
    fun stop() {
        active = false
        generation++
        pairing.stop()
        discoveryListener?.let { runCatching { nsd?.stopServiceDiscovery(it) } }
        registrationListener?.let { runCatching { nsd?.unregisterService(it) } }
        discoveryListener = null
        registrationListener = null
        runCatching { server?.close() }
        server = null
        acceptThread?.interrupt()
        acceptThread = null
        recheckThread?.interrupt()
        recheckThread = null
        multicastLock?.let { runCatching { if (it.isHeld) it.release() } }
        multicastLock = null
        found.clear()
        resolving.clear()
        probing.clear()
        candidates.clear()
        peers.clear()
        mutableSnapshot.value = DeviceDiscoverySnapshot(ownName)
    }

    fun restart() {
        stop()
        start()
    }

    fun beginPair(discoveryId: String) {
        val candidate = candidates.values.firstOrNull { it.discoveryId == discoveryId } ?: return
        if (snapshot.value.nearby.none { it.discoveryId == discoveryId }) return
        pairing.begin(candidate)
    }

    fun beginAction(peerId: String, action: DeviceAction) {
        val record = pairing.snapshot.value.paired.firstOrNull { it.id == peerId } ?: return
        val candidate = candidates.values.firstOrNull { it.discoveryId == record.discoveryId }
        if (candidate == null || snapshot.value.nearby.none { it.discoveryId == record.discoveryId }) {
            if (action == DeviceAction.DISCONNECT) pairing.revokeOffline(peerId)
            return
        }
        pairing.beginAction(candidate, peerId, action)
    }

    private fun startListener(listener: ServerSocket) {
        acceptThread = Thread {
            val ownBytes = ByteArray(16) { index -> ownId.substring(index * 2, index * 2 + 2).toInt(16).toByte() }
            while (active) {
                try {
                    val socket = listener.accept()
                    var transferred = false
                    try {
                        socket.soTimeout = 1_000
                        val input = java.io.DataInputStream(socket.getInputStream())
                        val marker = ByteArray(4)
                        input.readFully(marker)
                        if (marker.contentEquals("QDB1".toByteArray(Charsets.US_ASCII))) {
                            val nonce = ByteArray(16)
                            input.readFully(nonce)
                            socket.getOutputStream().write("QDA1".toByteArray(Charsets.US_ASCII) + nonce + ownBytes)
                        } else if (marker.contentEquals("QDP1".toByteArray(Charsets.US_ASCII))) {
                            pairing.accept(socket)
                            transferred = true
                        } else if (marker.contentEquals("QDM1".toByteArray(Charsets.US_ASCII))) {
                            pairing.acceptAction(socket)
                            transferred = true
                        }
                    } finally {
                        if (!transferred) socket.close()
                    }
                } catch (_: SocketTimeoutException) {
                    // Check active again.
                } catch (_: Exception) {
                    if (!active) break
                }
            }
        }.apply { name = "qingdevice-listener"; isDaemon = true; start() }
    }

    private fun startRecheck() {
        recheckThread = Thread {
            while (active) {
                try {
                    Thread.sleep(12_000)
                } catch (_: InterruptedException) {
                    break
                }
                if (!active) break
                candidates.values.forEach(::scheduleProbe)
                pairing.refreshPresence()
                val nearbyIds = snapshot.value.nearby.mapTo(HashSet()) { it.discoveryId }
                candidates.values.filter { it.discoveryId in nearbyIds }.forEach(pairing::retryTombstone)
                sendBatteryIfDue()
            }
        }.apply { name = "qingdevice-recheck"; isDaemon = true; start() }
    }

    private fun sendBatteryIfDue() {
        val now = System.currentTimeMillis()
        if (now - lastBatterySentAt < 60_000L) return
        val trusted = pairing.snapshot.value.paired.filter { it.intimate }
        if (trusted.isEmpty()) return
        val nearbyIds = snapshot.value.nearby.mapTo(HashSet()) { it.discoveryId }
        val targets = trusted.mapNotNull { peer ->
            candidates.values.firstOrNull { it.discoveryId == peer.discoveryId && it.discoveryId in nearbyIds }
                ?.let { peer to it }
        }
        if (targets.isEmpty()) return
        val status = appContext.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED)) ?: return
        val level = status.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
        val scale = status.getIntExtra(BatteryManager.EXTRA_SCALE, -1)
        if (level < 0 || scale <= 0) return
        val percent = (level * 100 / scale).coerceIn(0, 100)
        val state = status.getIntExtra(BatteryManager.EXTRA_STATUS, -1)
        val charging = state == BatteryManager.BATTERY_STATUS_CHARGING ||
            state == BatteryManager.BATTERY_STATUS_FULL
        lastBatterySentAt = now
        targets.forEach { (peer, candidate) ->
            pairing.sendBattery(candidate, peer.id, percent, charging)
        }
    }

    fun forwardNotification(appName: String, title: String, body: String) {
        if (!active) return
        val nearbyIds = snapshot.value.nearby.mapTo(HashSet()) { it.discoveryId }
        pairing.snapshot.value.paired.filter {
            it.intimate && it.platform == "windows" && it.forwardNotifications &&
                it.discoveryId in nearbyIds
        }.forEach { peer ->
            candidates.values.firstOrNull { it.discoveryId == peer.discoveryId }
                ?.let { pairing.sendNotification(it, peer.id, appName, title, body) }
        }
    }

    private fun scheduleProbe(candidate: DeviceDiscoveryProtocol.Candidate) {
        val key = candidate.serviceName
        if (!probing.add(key)) return
        val currentGeneration = generation
        workers.execute {
            try {
                val confirmed = DeviceDiscoveryProtocol.probe(candidate)
                if (!active || generation != currentGeneration || !found.contains(key) ||
                    candidates[key] != candidate) return@execute
                synchronized(peers) {
                    if (confirmed == null) peers.remove(key)
                    else if (peers.size < 64 || peers.containsKey(key)) {
                        peers[key] = NearbyDevice(key, candidate.discoveryId, candidate.name,
                            candidate.platform, confirmed.hostAddress ?: "")
                    }
                    publishPeers()
                }
                if (confirmed != null) {
                    pairing.probeOnline(candidate)
                    pairing.retryTombstone(candidate)
                }
            } finally {
                probing.remove(key)
            }
        }
    }

    private fun advertise(manager: NsdManager, port: Int) {
        val info = NsdServiceInfo().apply {
            serviceName = "QingToolbox-$ownId"
            serviceType = DeviceDiscoveryProtocol.serviceType
            this.port = port
            setAttribute("v", "1")
            setAttribute("id", ownId)
            setAttribute("name", ownName)
            setAttribute("pf", "android")
        }
        val listener = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(serviceInfo: NsdServiceInfo) {
                if (active) mutableSnapshot.value = mutableSnapshot.value.copy(available = true)
            }
            override fun onRegistrationFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                if (active) reportError(DeviceDiscoveryError.ADVERTISE_FAILED)
            }
            override fun onServiceUnregistered(serviceInfo: NsdServiceInfo) = Unit
            override fun onUnregistrationFailed(serviceInfo: NsdServiceInfo, errorCode: Int) = Unit
        }
        registrationListener = listener
        manager.registerService(info, NsdManager.PROTOCOL_DNS_SD, listener)
    }

    private fun browse(manager: NsdManager) {
        val listener = object : NsdManager.DiscoveryListener {
            override fun onDiscoveryStarted(serviceType: String) {
                if (active) mutableSnapshot.value = mutableSnapshot.value.copy(searching = false)
            }
            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                if (!active || !serviceInfo.serviceType.contains("_qingdevice._tcp", ignoreCase = true)) return
                val key = serviceInfo.serviceName.lowercase()
                found.add(key)
                if (!resolving.add(key)) return
                val resolvedGeneration = generation
                val resolveListener = object : NsdManager.ResolveListener {
                    override fun onServiceResolved(resolved: NsdServiceInfo) {
                        resolving.remove(key)
                        if (!active || generation != resolvedGeneration || !found.contains(key)) return
                        val attributes = resolved.attributes.mapValues { it.value.toString(Charsets.UTF_8) }
                        val addresses = if (Build.VERSION.SDK_INT >= 34) resolved.hostAddresses else listOfNotNull(resolved.host)
                        val candidate = DeviceDiscoveryProtocol.parse(
                            key, attributes, addresses, resolved.port, ownId,
                        ) ?: return
                        if (generation != resolvedGeneration) return
                        candidates[key] = candidate
                        scheduleProbe(candidate)
                    }
                    override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                        resolving.remove(key)
                    }
                }
                try {
                    @Suppress("DEPRECATION")
                    manager.resolveService(serviceInfo, resolveListener)
                } catch (_: Exception) {
                    resolving.remove(key)
                }
            }
            override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                val key = serviceInfo.serviceName.lowercase()
                found.remove(key)
                candidates.remove(key)
                synchronized(peers) {
                    peers.remove(key)
                    publishPeers()
                }
            }
            override fun onDiscoveryStopped(serviceType: String) = Unit
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                if (active) reportError(DeviceDiscoveryError.SEARCH_FAILED)
            }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) = Unit
        }
        discoveryListener = listener
        manager.discoverServices(DeviceDiscoveryProtocol.serviceType, NsdManager.PROTOCOL_DNS_SD, listener)
    }

    private fun publishPeers() {
        mutableSnapshot.value = mutableSnapshot.value.copy(nearby = peers.values.sortedBy { it.name.lowercase() })
    }

    private fun reportError(error: DeviceDiscoveryError) {
        mutableSnapshot.value = mutableSnapshot.value.copy(searching = false, error = error)
    }

    private fun acquireMulticastLock() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) return
        val wifi = appContext.getSystemService(WifiManager::class.java) ?: return
        multicastLock = wifi.createMulticastLock("qingdevice-discovery").apply {
            setReferenceCounted(false)
            acquire()
        }
    }

    companion object {
        private fun loadOrCreateDiscoveryId(context: Context): String {
            val idFile = AtomicFile(File(context.noBackupFilesDir, "device-discovery-id-v1"))
            val pattern = Regex("[0-9a-f]{32}")
            val saved = runCatching { idFile.openRead().bufferedReader(Charsets.US_ASCII).use { it.readText().trim() } }
                .getOrNull()
            if (saved != null && pattern.matches(saved)) return saved
            val legacy = context.getSharedPreferences("device-discovery-v1", Context.MODE_PRIVATE)
            val id = legacy.getString("id", null)?.takeIf(pattern::matches)
                ?: DeviceDiscoveryProtocol.newId()
            val output = idFile.startWrite()
            try {
                output.write(id.toByteArray(Charsets.US_ASCII))
                idFile.finishWrite(output)
            } catch (error: Exception) {
                idFile.failWrite(output)
                throw error
            }
            legacy.edit().remove("id").apply()
            return id
        }
    }
}

internal object DeviceDiscoverySessionStore {
    @Volatile private var instance: DeviceDiscoverySession? = null

    fun get(context: Context): DeviceDiscoverySession = instance ?: synchronized(this) {
        instance ?: DeviceDiscoverySession(context.applicationContext).also { instance = it }
    }
}
