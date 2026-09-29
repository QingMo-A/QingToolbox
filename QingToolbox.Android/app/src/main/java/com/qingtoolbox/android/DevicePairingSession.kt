package com.qingtoolbox.android

import android.content.Context
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

private const val PRESENCE_INTERVAL_NS = 120_000_000_000L
private const val PRESENCE_RETRY_NS = 15_000_000_000L
private const val PRESENCE_TTL_NS = 180_000_000_000L

/** Noise adds 16 authentication bytes; a management frame is capped at 1024. */
internal fun notificationManagementPayload(appName: String, title: String, body: String): ByteArray {
    var app = appName.take(80)
    var heading = title.take(160)
    var content = body.take(700)
    while (true) {
        val message = MobileJsonObject().int("version", 1).string("action", "notification")
            .string("appName", app).string("title", heading).string("body", content)
            .build().toByteArray(Charsets.UTF_8)
        if (message.size <= 1008) return message
        val cut = ((message.size - 1008) / 2).coerceAtLeast(1)
        when {
            content.isNotEmpty() -> content = content.dropLast(cut.coerceAtMost(content.length))
            heading.isNotEmpty() -> heading = heading.dropLast(cut.coerceAtMost(heading.length))
            app.isNotEmpty() -> app = app.dropLast(cut.coerceAtMost(app.length))
            else -> error("Notification frame cannot fit")
        }
    }
}

enum class DevicePairingError {
    UNAVAILABLE,
    CONNECTION_FAILED,
    HANDSHAKE_FAILED,
    ID_MISMATCH,
    ALREADY_PAIRED,
    KEY_CHANGED,
    TIMED_OUT,
    REMOTE_REJECTED,
    LOCAL_REJECTED,
    SAVE_FAILED,
    TOO_MANY,
}

enum class DeviceAction(val wireName: String) {
    UPGRADE("upgrade"),
    DISCONNECT("disconnect"),
    DEMOTE("demote"),
    DISCONNECT_NOTICE("disconnectNotice");

    companion object {
        fun fromWire(value: String?): DeviceAction? = entries.firstOrNull { it.wireName == value }
    }
}

data class PendingDeviceAction(
    val sessionId: String,
    val peerId: String,
    val name: String,
    val action: DeviceAction,
    val localApproved: Boolean = false,
    internal val decision: Boolean? = null,
)

data class DeviceActionNotice(val id: String, val name: String, val action: DeviceAction)
data class DeviceBattery(val peerId: String, val percent: Int, val charging: Boolean, val receivedAtMs: Long)

data class PendingDevicePair(
    val sessionId: String,
    val discoveryId: String,
    val name: String,
    val platform: String,
    val code: String,
    val incoming: Boolean,
    val localApproved: Boolean = false,
    internal val decision: Boolean? = null,
)

data class DevicePairingSnapshot(
    val error: DevicePairingError? = null,
    val pending: List<PendingDevicePair> = emptyList(),
    val paired: List<PairedDevice> = emptyList(),
    val actions: List<PendingDeviceAction> = emptyList(),
    val notices: List<DeviceActionNotice> = emptyList(),
    val batteries: List<DeviceBattery> = emptyList(),
    val revocations: List<PairedDevice> = emptyList(),
    val online: List<String> = emptyList(),
)

/** Pairing owns all sockets and authorization state; discovery only supplies endpoints. */
internal class DevicePairingSession(context: Context, private val ownId: String, private val ownName: String) {
    private val managementPrologue = "QingToolbox device management v1".toByteArray(Charsets.UTF_8)
    private val store = runCatching { DevicePairingStore(context) }.getOrNull()
    private val initialRecords = store?.let { runCatching { it.readRecords() }.getOrNull() }
    private val initialRevocations = store?.let { runCatching { it.readRevocations() }.getOrNull() }
    private val lock = Any()
    private val tombstones = initialRevocations?.associateBy { it.id }?.toMutableMap() ?: LinkedHashMap()
    private val records = initialRecords?.filterNot { it.id in tombstones }?.associateBy { it.id }
        ?.toMutableMap() ?: LinkedHashMap()
    private val pending = LinkedHashMap<String, PendingDevicePair>()
    private val actions = LinkedHashMap<String, PendingDeviceAction>()
    private val notices = ArrayList<DeviceActionNotice>()
    private val batteries = LinkedHashMap<String, DeviceBattery>()
    private val lastTombstoneAttempt = HashMap<String, Long>()
    private val lastPresenceAttempt = HashMap<String, Long>()
    private val lastAuthenticated = HashMap<String, Long>()
    private val outbound = HashSet<String>()
    private val sockets = ConcurrentHashMap.newKeySet<Socket>()
    private val workers = Executors.newCachedThreadPool { task ->
        Thread(task, "qingdevice-pairing").apply { isDaemon = true }
    }
    private val mutableSnapshot = MutableStateFlow(DevicePairingSnapshot(
        error = if (store == null || initialRecords == null || initialRevocations == null) DevicePairingError.UNAVAILABLE else null,
        paired = records.values.toList(),
        revocations = tombstones.values.toList(),
    ))
    val snapshot: StateFlow<DevicePairingSnapshot> = mutableSnapshot
    @Volatile private var active = false
    @Volatile private var generation = 0
    private var activeCount = 0

    fun start() {
        synchronized(lock) {
            active = true
            generation++
            publish()
        }
    }

    fun stop() {
        synchronized(lock) {
            active = false
            generation++
            activeCount = 0
            pending.clear()
            actions.clear()
            outbound.clear()
            lastPresenceAttempt.clear()
            lastAuthenticated.clear()
            publish()
        }
        sockets.forEach { runCatching { it.close() } }
    }

    fun begin(candidate: DeviceDiscoveryProtocol.Candidate) {
        if (!reserve(candidate.discoveryId)) return
        val currentGeneration = generation
        workers.execute {
            try {
                val socket = candidate.addresses.firstNotNullOfOrNull { address ->
                    Socket().let { trial ->
                        try {
                            trial.connect(InetSocketAddress(address, candidate.port), 5_000)
                            trial
                        } catch (_: Exception) {
                            trial.close()
                            null
                        }
                    }
                } ?: throw PairFailure(DevicePairingError.CONNECTION_FAILED)
                socket.use {
                    sockets.add(it)
                    it.soTimeout = 5_000
                    it.getOutputStream().write("QDP1".toByteArray(Charsets.US_ASCII))
                    runPair(it, true, candidate.discoveryId, currentGeneration)
                }
            } catch (failure: PairFailure) {
                if (failure.error != DevicePairingError.LOCAL_REJECTED) report(failure.error, currentGeneration)
            } catch (_: Exception) {
                report(DevicePairingError.HANDSHAKE_FAILED, currentGeneration)
            } finally {
                sockets.removeIf { it.isClosed }
                release(candidate.discoveryId, currentGeneration)
            }
        }
    }

    fun accept(socket: Socket) {
        if (!reserve(null)) {
            socket.close()
            return
        }
        val currentGeneration = generation
        workers.execute {
            try {
                socket.use {
                    sockets.add(it)
                    it.soTimeout = 5_000
                    runPair(it, false, null, currentGeneration)
                }
            } catch (failure: PairFailure) {
                if (failure.error != DevicePairingError.LOCAL_REJECTED) report(failure.error, currentGeneration)
            } catch (_: Exception) {
                report(DevicePairingError.HANDSHAKE_FAILED, currentGeneration)
            } finally {
                sockets.remove(socket)
                release(null, currentGeneration)
            }
        }
    }

    fun beginAction(candidate: DeviceDiscoveryProtocol.Candidate, peerId: String, action: DeviceAction) {
        synchronized(lock) {
            val record = records[peerId] ?: return
            if (record.discoveryId != candidate.discoveryId) return
            if (action == DeviceAction.UPGRADE && record.intimate) return
        }
        if (!reserve(candidate.discoveryId)) return
        val currentGeneration = generation
        workers.execute {
            try {
                val socket = candidate.addresses.firstNotNullOfOrNull { address ->
                    Socket().let { trial ->
                        try {
                            trial.connect(InetSocketAddress(address, candidate.port), 5_000)
                            trial
                        } catch (_: Exception) { trial.close(); null }
                    }
                } ?: run {
                    if (action == DeviceAction.DISCONNECT) { revokeOffline(peerId); return@execute }
                    throw PairFailure(DevicePairingError.CONNECTION_FAILED)
                }
                socket.use {
                    sockets.add(it)
                    it.soTimeout = 5_000
                    it.getOutputStream().write("QDM1".toByteArray(Charsets.US_ASCII))
                    runAction(it, true, candidate.discoveryId, peerId, action, currentGeneration)
                }
            } catch (failure: PairFailure) {
                if (failure.error != DevicePairingError.LOCAL_REJECTED) report(failure.error, currentGeneration)
            } catch (_: Exception) {
                report(DevicePairingError.HANDSHAKE_FAILED, currentGeneration)
            } finally {
                sockets.removeIf { it.isClosed }
                release(candidate.discoveryId, currentGeneration)
            }
        }
    }

    fun acceptAction(socket: Socket) {
        if (!reserve(null)) { socket.close(); return }
        val currentGeneration = generation
        workers.execute {
            try {
                socket.use {
                    sockets.add(it)
                    it.soTimeout = 5_000
                    runAction(it, false, null, null, null, currentGeneration)
                }
            } catch (failure: PairFailure) {
                if (failure.error != DevicePairingError.LOCAL_REJECTED) report(failure.error, currentGeneration)
            } catch (_: Exception) {
                report(DevicePairingError.HANDSHAKE_FAILED, currentGeneration)
            } finally {
                sockets.remove(socket)
                release(null, currentGeneration)
            }
        }
    }

    fun sendBattery(candidate: DeviceDiscoveryProtocol.Candidate, peerId: String, percent: Int, charging: Boolean) {
        if (percent !in 0..100) return
        synchronized(lock) {
            val record = records[peerId] ?: return
            if (!record.intimate || record.discoveryId != candidate.discoveryId) return
        }
        if (!reserve(candidate.discoveryId)) return
        val currentGeneration = generation
        workers.execute {
            try {
                val socket = candidate.addresses.firstNotNullOfOrNull { address ->
                    Socket().let { trial ->
                        try { trial.connect(InetSocketAddress(address, candidate.port), 5_000); trial }
                        catch (_: Exception) { trial.close(); null }
                    }
                } ?: return@execute
                socket.use {
                    sockets.add(it)
                    it.soTimeout = 5_000
                    it.getOutputStream().write("QDM1".toByteArray(Charsets.US_ASCII))
                    runAction(it, true, candidate.discoveryId, peerId, null, currentGeneration, percent to charging)
                }
            } catch (_: Exception) {
                // Battery is best effort; the next scheduled sample retries.
            } finally {
                sockets.removeIf { it.isClosed }
                release(candidate.discoveryId, currentGeneration)
            }
        }
    }

    fun setNotificationForwarding(peerId: String, enabled: Boolean): Boolean {
        val availableStore = store ?: return false
        synchronized(lock) {
            val peer = records[peerId] ?: return false
            if (!peer.intimate || peer.platform != "windows") return false
            val updated = records.toMutableMap().apply {
                put(peerId, peer.copy(forwardNotifications = enabled))
            }
            return try {
                availableStore.writeRecords(updated.values.toList())
                records.clear(); records.putAll(updated)
                publish()
                true
            } catch (_: Exception) {
                mutableSnapshot.value = mutableSnapshot.value.copy(error = DevicePairingError.SAVE_FAILED)
                false
            }
        }
    }

    fun sendNotification(candidate: DeviceDiscoveryProtocol.Candidate, peerId: String,
                         appName: String, title: String, body: String) {
        synchronized(lock) {
            val peer = records[peerId] ?: return
            if (!active || !peer.intimate || peer.platform != "windows" ||
                !peer.forwardNotifications || peer.discoveryId != candidate.discoveryId) return
        }
        if (appName.length > 80 || title.length > 160 || body.length > 700) return
        if (!reserveBackground()) return
        val currentGeneration = generation
        workers.execute {
            try {
                val socket = candidate.addresses.firstNotNullOfOrNull { address ->
                    Socket().let { trial ->
                        try { trial.connect(InetSocketAddress(address, candidate.port), 5_000); trial }
                        catch (_: Exception) { trial.close(); null }
                    }
                } ?: return@execute
                socket.use {
                    sockets.add(it)
                    it.soTimeout = 5_000
                    it.getOutputStream().write("QDM1".toByteArray(Charsets.US_ASCII))
                    runAction(it, true, candidate.discoveryId, peerId, null, currentGeneration,
                        notification = Triple(appName, title, body))
                }
            } catch (_: Exception) {
                // Notification forwarding is best effort and never prompts the user.
            } finally {
                sockets.removeIf { it.isClosed }
                release(null, currentGeneration)
            }
        }
    }

    /** Best-effort authenticated heartbeat for both connected and intimate peers. */
    fun probeOnline(candidate: DeviceDiscoveryProtocol.Candidate) {
        val (peerId, attemptTime) = synchronized(lock) {
            val record = records.values.firstOrNull { it.discoveryId == candidate.discoveryId } ?: return
            val now = System.nanoTime()
            if (lastPresenceAttempt[record.id]?.let { now - it < PRESENCE_INTERVAL_NS } == true) return
            if (!active || activeCount >= 4) return
            activeCount++
            lastPresenceAttempt[record.id] = now
            record.id to now
        }
        val currentGeneration = generation
        workers.execute {
            var acknowledged = false
            try {
                val socket = candidate.addresses.firstNotNullOfOrNull { address ->
                    Socket().let { trial ->
                        try { trial.connect(InetSocketAddress(address, candidate.port), 5_000); trial }
                        catch (_: Exception) { trial.close(); null }
                    }
                } ?: return@execute
                socket.use {
                    sockets.add(it)
                    it.soTimeout = 5_000
                    it.getOutputStream().write("QDM1".toByteArray(Charsets.US_ASCII))
                    runAction(it, true, candidate.discoveryId, peerId, null, currentGeneration, presence = true)
                    acknowledged = true
                }
            } catch (_: Exception) {
                // Offline peers are expected; discovery will retry without showing a pairing error.
            } finally {
                if (!acknowledged) synchronized(lock) {
                    if (active && generation == currentGeneration && lastPresenceAttempt[peerId] == attemptTime) {
                        lastPresenceAttempt[peerId] = System.nanoTime() - (PRESENCE_INTERVAL_NS - PRESENCE_RETRY_NS)
                    }
                }
                sockets.removeIf { it.isClosed }
                release(null, currentGeneration)
            }
        }
    }

    fun refreshPresence() {
        synchronized(lock) {
            val before = mutableSnapshot.value.online
            val now = System.nanoTime()
            lastAuthenticated.entries.removeAll { (id, time) ->
                id !in records || now - time >= PRESENCE_TTL_NS
            }
            if (before != lastAuthenticated.keys.sorted()) publish()
        }
    }

    fun revokeOffline(peerId: String): Boolean {
        val availableStore = store ?: return false
        synchronized(lock) {
            val removed = records[peerId] ?: return false
            val updatedTombstones = tombstones.toMutableMap().apply { put(peerId, removed) }
            try { availableStore.writeRevocations(updatedTombstones.values.toList()) }
            catch (_: Exception) {
                mutableSnapshot.value = mutableSnapshot.value.copy(error = DevicePairingError.SAVE_FAILED)
                return false
            }
            val recordsSaved = runCatching {
                availableStore.writeRecords(records.values.filterNot { it.id == peerId })
            }.isSuccess
            tombstones.clear(); tombstones.putAll(updatedTombstones)
            records.remove(peerId)
            batteries.remove(peerId)
            lastAuthenticated.remove(peerId)
            lastPresenceAttempt.remove(peerId)
            notices.add(DeviceActionNotice(DeviceDiscoveryProtocol.newId(), removed.name, DeviceAction.DISCONNECT))
            if (notices.size > 16) notices.removeAt(0)
            publish()
            if (!recordsSaved) mutableSnapshot.value = mutableSnapshot.value.copy(error = DevicePairingError.SAVE_FAILED)
            return true
        }
    }

    fun retryTombstone(candidate: DeviceDiscoveryProtocol.Candidate) {
        val peer = synchronized(lock) {
            val found = tombstones.values.firstOrNull { it.discoveryId == candidate.discoveryId } ?: return
            val now = System.currentTimeMillis()
            if (now - (lastTombstoneAttempt[found.id] ?: 0L) < 30_000L) return
            lastTombstoneAttempt[found.id] = now
            found
        }
        if (!reserve(candidate.discoveryId)) return
        val currentGeneration = generation
        workers.execute {
            try {
                val socket = candidate.addresses.firstNotNullOfOrNull { address ->
                    Socket().let { trial ->
                        try { trial.connect(InetSocketAddress(address, candidate.port), 5_000); trial }
                        catch (_: Exception) { trial.close(); null }
                    }
                } ?: return@execute
                socket.use {
                    sockets.add(it)
                    it.soTimeout = 5_000
                    it.getOutputStream().write("QDM1".toByteArray(Charsets.US_ASCII))
                    runAction(it, true, candidate.discoveryId, peer.id, DeviceAction.DISCONNECT_NOTICE, currentGeneration)
                }
            } catch (_: Exception) {
                // The next discovery interval retries the persisted revocation.
            } finally {
                sockets.removeIf { it.isClosed }
                release(candidate.discoveryId, currentGeneration)
            }
        }
    }

    private fun runAction(
        socket: Socket,
        initiator: Boolean,
        expectedId: String?,
        expectedKey: String?,
        outgoingAction: DeviceAction?,
        currentGeneration: Int,
        battery: Pair<Int, Boolean>? = null,
        presence: Boolean = false,
        notification: Triple<String, String, String>? = null,
    ) {
        val availableStore = store ?: throw PairFailure(DevicePairingError.UNAVAILABLE)
        val privateKey = availableStore.loadPrivateKey()
        val result = try {
            DevicePairingProtocol.handshake(
                socket, privateKey, DevicePairingProtocol.Hello(ownId, ownName, "android"),
                initiator, managementPrologue,
            )
        } finally { privateKey.fill(0) }
        result.use { channel ->
            val peerId = channel.remoteKey.toHex()
            val record = synchronized(lock) { records[peerId] ?: tombstones[peerId] }
                ?: throw PairFailure(DevicePairingError.ID_MISMATCH)
            if (record.discoveryId != channel.remote.discoveryId ||
                (expectedId != null && expectedId != record.discoveryId) ||
                (expectedKey != null && expectedKey != peerId)
            ) throw PairFailure(DevicePairingError.ID_MISMATCH)
            synchronized(lock) {
                if (peerId in records && active && generation == currentGeneration) {
                    lastAuthenticated[peerId] = System.nanoTime()
                    publish()
                }
            }
            if (initiator) {
                if (notification != null) {
                    if (synchronized(lock) { records[peerId]?.let {
                        it.intimate && it.platform == "windows" && it.forwardNotifications
                    } } != true) throw PairFailure(DevicePairingError.ID_MISMATCH)
                    channel.sendMessage(socket, notificationManagementPayload(
                        notification.first, notification.second, notification.third))
                    if (!channel.receiveMessage(socket).contentEquals(byteArrayOf('D'.code.toByte()))) {
                        throw PairFailure(DevicePairingError.HANDSHAKE_FAILED)
                    }
                    return
                }
                if (presence) {
                    channel.sendMessage(socket, MobileJsonObject().int("version", 1)
                        .string("action", "ping").build().toByteArray(Charsets.UTF_8))
                    if (!channel.receiveMessage(socket).contentEquals(byteArrayOf('D'.code.toByte()))) {
                        throw PairFailure(DevicePairingError.HANDSHAKE_FAILED)
                    }
                    return
                }
                if (battery != null) {
                    if (synchronized(lock) { records[peerId]?.intimate != true }) {
                        throw PairFailure(DevicePairingError.ID_MISMATCH)
                    }
                    val message = MobileJsonObject().int("version", 1).string("action", "battery")
                        .int("percent", battery.first).bool("charging", battery.second)
                        .build().toByteArray(Charsets.UTF_8)
                    channel.sendMessage(socket, message)
                    if (!channel.receiveMessage(socket).contentEquals(byteArrayOf('D'.code.toByte()))) {
                        throw PairFailure(DevicePairingError.HANDSHAKE_FAILED)
                    }
                    return
                }
                val action = outgoingAction ?: throw PairFailure(DevicePairingError.HANDSHAKE_FAILED)
                val message = MobileJsonObject().int("version", 1).string("action", action.wireName)
                    .build().toByteArray(Charsets.UTF_8)
                channel.sendMessage(socket, message)
                if (action == DeviceAction.DISCONNECT_NOTICE) {
                    if (!channel.receiveMessage(socket).contentEquals(byteArrayOf('D'.code.toByte()))) {
                        throw PairFailure(DevicePairingError.HANDSHAKE_FAILED)
                    }
                    clearTombstone(peerId)
                    return
                }
                socket.soTimeout = 95_000
                val decision = channel.receiveMessage(socket)
                if (decision.contentEquals(byteArrayOf('R'.code.toByte()))) {
                    throw PairFailure(DevicePairingError.REMOTE_REJECTED)
                }
                if (!decision.contentEquals(byteArrayOf('A'.code.toByte()))) {
                    throw PairFailure(DevicePairingError.HANDSHAKE_FAILED)
                }
                if (!active || generation != currentGeneration) throw PairFailure(DevicePairingError.CONNECTION_FAILED)
                channel.sendMessage(socket, byteArrayOf('C'.code.toByte()))
                socket.soTimeout = 5_000
                if (!channel.receiveMessage(socket).contentEquals(byteArrayOf('D'.code.toByte()))) {
                    throw PairFailure(DevicePairingError.HANDSHAKE_FAILED)
                }
                applyAction(peerId, action, record.name)
            } else {
                val request = MobileJson.parse(String(channel.receiveMessage(socket), Charsets.UTF_8))
                if (request.stringField("action") == DeviceAction.DISCONNECT_NOTICE.wireName) {
                    if (request.intField("version") != 1) throw PairFailure(DevicePairingError.HANDSHAKE_FAILED)
                    applyDisconnectNotice(peerId, record.name)
                    channel.sendMessage(socket, byteArrayOf('D'.code.toByte()))
                    return
                }
                val alreadyRevoked = synchronized(lock) { peerId !in records }
                if (alreadyRevoked && request.stringField("action") == DeviceAction.DISCONNECT.wireName) {
                    // A previously approved disconnect can be retried after
                    // its completion frame was lost, without asking again.
                    if (request.intField("version") != 1) throw PairFailure(DevicePairingError.HANDSHAKE_FAILED)
                    channel.sendMessage(socket, byteArrayOf('A'.code.toByte()))
                    if (!channel.receiveMessage(socket).contentEquals(byteArrayOf('C'.code.toByte()))) {
                        throw PairFailure(DevicePairingError.HANDSHAKE_FAILED)
                    }
                    channel.sendMessage(socket, byteArrayOf('D'.code.toByte()))
                    return
                }
                if (alreadyRevoked) throw PairFailure(DevicePairingError.ID_MISMATCH)
                if (request.stringField("action") == "ping") {
                    if (request.intField("version") != 1) throw PairFailure(DevicePairingError.HANDSHAKE_FAILED)
                    channel.sendMessage(socket, byteArrayOf('D'.code.toByte()))
                    return
                }
                if (request.stringField("action") == "battery") {
                    val percent = request.intField("percent")
                    val charging = request.field("charging")?.asBoolOrNull()
                    if (request.intField("version") != 1 ||
                        synchronized(lock) { records[peerId]?.intimate != true } ||
                        percent == null || percent !in 0..100 || charging == null
                    ) throw PairFailure(DevicePairingError.HANDSHAKE_FAILED)
                    synchronized(lock) {
                        batteries[peerId] = DeviceBattery(peerId, percent, charging, System.currentTimeMillis())
                        publish()
                    }
                    channel.sendMessage(socket, byteArrayOf('D'.code.toByte()))
                    return
                }
                val action = DeviceAction.fromWire(request.stringField("action"))
                    ?: throw PairFailure(DevicePairingError.HANDSHAKE_FAILED)
                if (request.intField("version") != 1 ||
                    action == DeviceAction.DISCONNECT_NOTICE
                ) throw PairFailure(DevicePairingError.HANDSHAKE_FAILED)
                val sessionId = DeviceDiscoveryProtocol.newId()
                synchronized(lock) {
                    if (!active || generation != currentGeneration) return
                    if (actions.values.any { it.peerId == peerId }) throw PairFailure(DevicePairingError.TOO_MANY)
                    actions[sessionId] = PendingDeviceAction(sessionId, peerId, record.name, action)
                    publish()
                }
                val decision = try { awaitActionDecision(sessionId, currentGeneration) }
                    finally { synchronized(lock) { actions.remove(sessionId); publish() } }
                channel.sendMessage(socket, byteArrayOf(if (decision) 'A'.code.toByte() else 'R'.code.toByte()))
                if (!decision) return
                socket.soTimeout = 5_000
                if (!channel.receiveMessage(socket).contentEquals(byteArrayOf('C'.code.toByte()))) {
                    throw PairFailure(DevicePairingError.HANDSHAKE_FAILED)
                }
                applyAction(peerId, action, record.name)
                channel.sendMessage(socket, byteArrayOf('D'.code.toByte()))
            }
        }
    }

    private fun awaitActionDecision(sessionId: String, currentGeneration: Int): Boolean {
        val deadline = System.nanoTime() + 90_000_000_000L
        while (System.nanoTime() < deadline) {
            if (!active || generation != currentGeneration) throw PairFailure(DevicePairingError.CONNECTION_FAILED)
            synchronized(lock) { actions[sessionId]?.decision }?.let { return it }
            Thread.sleep(70)
        }
        throw PairFailure(DevicePairingError.TIMED_OUT)
    }

    private fun applyAction(peerId: String, action: DeviceAction, name: String) {
        if (action == DeviceAction.DISCONNECT) {
            // Preserve a durable revocation on both ends until it is synced.
            if (!revokeOffline(peerId)) throw PairFailure(DevicePairingError.SAVE_FAILED)
            return
        }
        val availableStore = store ?: throw PairFailure(DevicePairingError.UNAVAILABLE)
        synchronized(lock) {
            val old = records[peerId] ?: throw PairFailure(DevicePairingError.ID_MISMATCH)
            val updated = records.toMutableMap()
            when (action) {
                DeviceAction.UPGRADE -> updated[peerId] = old.copy(intimate = true)
                DeviceAction.DEMOTE -> updated[peerId] = old.copy(intimate = false)
                DeviceAction.DISCONNECT, DeviceAction.DISCONNECT_NOTICE ->
                    throw PairFailure(DevicePairingError.HANDSHAKE_FAILED)
            }
            try { availableStore.writeRecords(updated.values.toList()) }
            catch (_: Exception) { throw PairFailure(DevicePairingError.SAVE_FAILED) }
            records.clear(); records.putAll(updated)
            notices.add(DeviceActionNotice(DeviceDiscoveryProtocol.newId(), name, action))
            if (notices.size > 16) notices.removeAt(0)
            publish()
        }
    }

    private fun clearTombstone(peerId: String) {
        val availableStore = store ?: throw PairFailure(DevicePairingError.UNAVAILABLE)
        synchronized(lock) {
            if (!tombstones.containsKey(peerId)) return
            val updated = tombstones.toMutableMap().apply { remove(peerId) }
            try {
                // A previous paired-file write may have failed. Ensure the
                // peer is durably absent before clearing its safety record.
                availableStore.writeRecords(records.values.toList())
                availableStore.writeRevocations(updated.values.toList())
            }
            catch (_: Exception) { throw PairFailure(DevicePairingError.SAVE_FAILED) }
            tombstones.clear(); tombstones.putAll(updated)
            lastTombstoneAttempt.remove(peerId)
            publish()
        }
    }

    private fun applyDisconnectNotice(peerId: String, name: String) {
        val availableStore = store ?: throw PairFailure(DevicePairingError.UNAVAILABLE)
        synchronized(lock) {
            val updatedRecords = records.toMutableMap().apply { remove(peerId) }
            val updatedTombstones = tombstones.toMutableMap().apply { remove(peerId) }
            try {
                availableStore.writeRecords(updatedRecords.values.toList())
            } catch (_: Exception) { throw PairFailure(DevicePairingError.SAVE_FAILED) }
            try {
                if (peerId in tombstones) availableStore.writeRevocations(updatedTombstones.values.toList())
            } catch (_: Exception) {
                // The paired file is already cleared; retain the revocation
                // in memory and on disk, and retry acknowledgement later.
                records.clear(); records.putAll(updatedRecords)
                publish()
                throw PairFailure(DevicePairingError.SAVE_FAILED)
            }
            records.clear(); records.putAll(updatedRecords)
            tombstones.clear(); tombstones.putAll(updatedTombstones)
            batteries.remove(peerId)
            lastAuthenticated.remove(peerId)
            lastPresenceAttempt.remove(peerId)
            notices.add(DeviceActionNotice(DeviceDiscoveryProtocol.newId(), name, DeviceAction.DISCONNECT))
            if (notices.size > 16) notices.removeAt(0)
            publish()
        }
    }

    fun decide(sessionId: String, approve: Boolean) {
        synchronized(lock) {
            val old = pending[sessionId] ?: return
            if (old.decision != null) return
            pending[sessionId] = old.copy(localApproved = approve, decision = approve)
            publish()
        }
    }

    fun decideAction(sessionId: String, approve: Boolean) {
        synchronized(lock) {
            val old = actions[sessionId] ?: return
            if (old.decision != null) return
            actions[sessionId] = old.copy(localApproved = approve, decision = approve)
            publish()
        }
    }

    fun clearError() {
        synchronized(lock) { mutableSnapshot.value = mutableSnapshot.value.copy(error = null) }
    }

    fun setIntimate(peerId: String, intimate: Boolean) {
        val availableStore = store ?: return
        synchronized(lock) {
            val current = records[peerId] ?: return
            val updated = records.toMutableMap().apply { put(peerId, current.copy(intimate = intimate)) }
            try {
                availableStore.writeRecords(updated.values.toList())
                records.clear()
                records.putAll(updated)
                publish()
            } catch (_: Exception) {
                mutableSnapshot.value = mutableSnapshot.value.copy(error = DevicePairingError.SAVE_FAILED)
            }
        }
    }

    fun revoke(peerId: String) {
        val availableStore = store ?: return
        synchronized(lock) {
            if (!records.containsKey(peerId)) return
            val updated = records.toMutableMap().apply { remove(peerId) }
            try {
                availableStore.writeRecords(updated.values.toList())
                records.clear()
                records.putAll(updated)
                lastAuthenticated.remove(peerId)
                lastPresenceAttempt.remove(peerId)
                publish()
            } catch (_: Exception) {
                mutableSnapshot.value = mutableSnapshot.value.copy(error = DevicePairingError.SAVE_FAILED)
            }
        }
    }

    private fun reserve(outboundId: String?): Boolean = synchronized(lock) {
        if (!active || store == null || initialRecords == null || initialRevocations == null) {
            mutableSnapshot.value = mutableSnapshot.value.copy(error = DevicePairingError.UNAVAILABLE)
            return@synchronized false
        }
        if (activeCount >= 4 || (outboundId != null && outboundId in outbound)) {
            mutableSnapshot.value = mutableSnapshot.value.copy(error = DevicePairingError.TOO_MANY)
            return@synchronized false
        }
        activeCount++
        if (outboundId != null) outbound.add(outboundId)
        mutableSnapshot.value = mutableSnapshot.value.copy(error = null)
        true
    }

    private fun reserveBackground(): Boolean = synchronized(lock) {
        if (!active || activeCount >= 4 || store == null) return@synchronized false
        activeCount++
        true
    }

    private fun release(outboundId: String?, currentGeneration: Int) {
        synchronized(lock) {
            if (generation != currentGeneration) return
            activeCount = (activeCount - 1).coerceAtLeast(0)
            if (outboundId != null) outbound.remove(outboundId)
        }
    }

    private fun runPair(socket: Socket, initiator: Boolean, expectedId: String?, currentGeneration: Int) {
        val availableStore = store ?: throw PairFailure(DevicePairingError.UNAVAILABLE)
        val privateKey = availableStore.loadPrivateKey()
        val result = try {
            DevicePairingProtocol.handshake(
                socket, privateKey,
                DevicePairingProtocol.Hello(ownId, ownName, "android"), initiator,
            )
        } finally {
            privateKey.fill(0)
        }
        result.use { pair ->
            if (pair.remote.discoveryId == ownId ||
                (expectedId != null && pair.remote.discoveryId != expectedId)
            ) throw PairFailure(DevicePairingError.ID_MISMATCH)
            val peerId = pair.remoteKey.toHex()
            val sessionId = DeviceDiscoveryProtocol.newId()
            synchronized(lock) {
                if (!active || generation != currentGeneration) return
                if (records.containsKey(peerId)) throw PairFailure(DevicePairingError.ALREADY_PAIRED)
                if (records.values.any { it.discoveryId == pair.remote.discoveryId && it.id != peerId }) {
                    throw PairFailure(DevicePairingError.KEY_CHANGED)
                }
                if (pending.values.any { it.discoveryId == pair.remote.discoveryId }) {
                    throw PairFailure(DevicePairingError.TOO_MANY)
                }
                pending[sessionId] = PendingDevicePair(
                    sessionId, pair.remote.discoveryId, pair.remote.name,
                    pair.remote.platform, pair.code, !initiator,
                )
                publish()
            }
            try {
                awaitConfirmations(socket, pair, sessionId, currentGeneration)
                synchronized(lock) {
                    if (!active || generation != currentGeneration) return
                    if (records.size >= 128 || records.containsKey(peerId)) {
                        throw PairFailure(DevicePairingError.TOO_MANY)
                    }
                    val added = PairedDevice(peerId, pair.remote.discoveryId, pair.remote.name, pair.remote.platform)
                    try {
                        availableStore.writeRecords(records.values.toList() + added)
                    } catch (_: Exception) {
                        throw PairFailure(DevicePairingError.SAVE_FAILED)
                    }
                    records[peerId] = added
                    lastAuthenticated[peerId] = System.nanoTime()
                    publish()
                }
            } finally {
                synchronized(lock) {
                    pending.remove(sessionId)
                    publish()
                }
            }
        }
    }

    private fun awaitConfirmations(
        socket: Socket,
        pair: DevicePairingProtocol.Result,
        sessionId: String,
        currentGeneration: Int,
    ) {
        socket.soTimeout = 90_000
        val remote = CompletableFuture<Boolean>()
        workers.execute {
            try { remote.complete(pair.receive(socket)) }
            catch (error: Exception) { remote.completeExceptionally(error) }
        }
        val deadline = System.nanoTime() + 90_000_000_000L
        var sent = false
        var remoteApproved = false
        while (System.nanoTime() < deadline) {
            if (!active || generation != currentGeneration) throw PairFailure(DevicePairingError.CONNECTION_FAILED)
            val decision = synchronized(lock) { pending[sessionId]?.decision }
            if (decision != null && !sent) {
                pair.send(socket, decision)
                sent = true
                if (!decision) throw PairFailure(DevicePairingError.LOCAL_REJECTED)
            }
            if (remote.isDone && !remoteApproved) {
                remoteApproved = try { remote.get() }
                catch (_: Exception) { throw PairFailure(DevicePairingError.CONNECTION_FAILED) }
                if (!remoteApproved) throw PairFailure(DevicePairingError.REMOTE_REJECTED)
            }
            if (sent && remoteApproved) return
            Thread.sleep(70)
        }
        throw PairFailure(DevicePairingError.TIMED_OUT)
    }

    private fun report(error: DevicePairingError, currentGeneration: Int) {
        synchronized(lock) {
            if (active && generation == currentGeneration) {
                mutableSnapshot.value = mutableSnapshot.value.copy(error = error)
            }
        }
    }

    private fun publish() {
        mutableSnapshot.value = mutableSnapshot.value.copy(
            pending = pending.values.toList(),
            paired = records.values.sortedBy { it.name.lowercase() },
            actions = actions.values.toList(),
            notices = notices.toList(),
            batteries = batteries.values.toList(),
            revocations = tombstones.values.toList(),
            online = lastAuthenticated.keys.filter { it in records }.sorted(),
        )
    }

    private fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it.toInt() and 0xff) }
    private class PairFailure(val error: DevicePairingError) : Exception()
}
