package com.qingtoolbox.android

import android.content.ContentResolver
import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract
import androidx.documentfile.provider.DocumentFile
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withTimeoutOrNull
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.security.MessageDigest
import android.util.Log

internal enum class QingTransferConnectionState { IDLE, CONNECTING, WAITING_APPROVAL, CONNECTED }

internal enum class QingTransferErrorCode {
    UNABLE_TO_CONNECT,
    PEER_REJECTED,
    INVALID_PROTOCOL,
    TRANSFER_FAILED,
    CANCELED,
    DISCONNECTED,
}

internal data class QingTransferFileOffer(val name: String, val size: Long)

internal data class QingTransferProgress(
    val name: String,
    val completed: Long,
    val total: Long,
    val receiving: Boolean,
)

private data class QingTransferPendingSend(
    val uri: Uri,
    val name: String,
    val size: Long,
)

internal class QingTransferConnection(
    private val context: Context,
    private val discovery: QingTransferDiscovery,
    private val friendlyName: String,
    private val receivePreferences: QingTransferReceivePreferencesStore? = null,
) {
    private fun stage(message: String) = Log.d("QingTransferStage", message)
    private val resolver: ContentResolver = context.applicationContext.contentResolver
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val _state = MutableStateFlow(QingTransferConnectionState.IDLE)
    val state: StateFlow<QingTransferConnectionState> = _state.asStateFlow()
    private val _incomingPeer = MutableStateFlow<QingTransferPeer?>(null)
    val incomingPeer: StateFlow<QingTransferPeer?> = _incomingPeer.asStateFlow()
    private val _incomingOffer = MutableStateFlow<QingTransferFileOffer?>(null)
    val incomingOffer: StateFlow<QingTransferFileOffer?> = _incomingOffer.asStateFlow()
    private val _progress = MutableStateFlow<QingTransferProgress?>(null)
    val progress: StateFlow<QingTransferProgress?> = _progress.asStateFlow()
    private val _lastCompleted = MutableStateFlow<String?>(null)
    val lastCompleted: StateFlow<String?> = _lastCompleted.asStateFlow()
    private val _error = MutableStateFlow<QingTransferErrorCode?>(null)
    val error: StateFlow<QingTransferErrorCode?> = _error.asStateFlow()

    private var socket: Socket? = null
    private var ioJob: Job? = null
    private var transferJob: Job? = null
    private var incomingDecision: CompletableDeferred<Uri?>? = null
    private var outgoingDecision: CompletableDeferred<Boolean>? = null
    private var outgoingRawComplete: CompletableDeferred<Unit>? = null
    private var resultDecision: CompletableDeferred<Boolean>? = null
    private var incomingName: String? = null
    var targetDeviceId: String? = null
    var targetAddress: String? = null

    init { discovery.onIncomingSocket = { client -> ioJob = scope.launch { handleIncoming(client) } } }

    fun connect(peer: QingTransferPeer) = connect(peer, null)

    private fun connect(peer: QingTransferPeer, initialSend: QingTransferPendingSend?) {
        if (!QingTransferTarget.matches(peer, targetDeviceId, targetAddress)) return
        if (_state.value != QingTransferConnectionState.IDLE) return
        stage("connect-start endpoints=${peer.addresses.size} port=${peer.port}")
        _error.value = null
        _lastCompleted.value = null
        _state.value = QingTransferConnectionState.CONNECTING
        ioJob = scope.launch {
            try {
                var connected: Socket? = null
                for (address in peer.addresses) {
                    try {
                        val candidate = Socket()
                        withTimeout(5_000) { candidate.connect(InetSocketAddress(address, peer.port), 5_000) }
                        stage("connect-socket-ok")
                        connected = candidate
                        break
                    } catch (_: Exception) { }
                }
                val client = connected ?: throw IllegalStateException("unable")
                socket = client
                withTimeout(5_000) {
                    QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.Hello(
                        "android", friendlyName, DeviceDiscoverySessionStore.get(context).ownId))
                        when (QingTransferProtocol.read(client.getInputStream())) {
                        QingTransferMessage.Accept -> {
                            stage("connect-accepted")
                            _state.value = QingTransferConnectionState.CONNECTED
                            discovery.setConnectedPeer(peer.serviceName)
                            initialSend?.let {
                                stage("initial-send-dispatch")
                                sendFile(it.uri, it.name, it.size)
                            }
                        }
                        QingTransferMessage.Reject -> throw PeerRejectedException()
                        else -> throw ProtocolException()
                    }
                }
                receiveUntilClosed(client)
            } catch (_: CancellationException) { throw CancellationException() }
            catch (error: PeerRejectedException) { stage("connect-rejected"); fail(QingTransferErrorCode.PEER_REJECTED, peer.serviceName) }
            catch (error: ProtocolException) { stage("connect-invalid-protocol"); fail(QingTransferErrorCode.INVALID_PROTOCOL, peer.serviceName) }
            catch (error: Exception) { stage("connect-failed type=${error.javaClass.simpleName}"); fail(QingTransferErrorCode.UNABLE_TO_CONNECT, peer.serviceName) }
        }
    }

    fun connectAndSend(peer: QingTransferPeer, uri: Uri, name: String, size: Long) {
        if (_state.value != QingTransferConnectionState.IDLE ||
            size !in 0..QingTransferProtocol.MAX_FILE_BYTES || !safeFileName(name)) {
            _error.value = QingTransferErrorCode.TRANSFER_FAILED
            return
        }
        connect(peer, QingTransferPendingSend(uri, name, size))
    }

    fun sendFile(uri: Uri, name: String, size: Long) {
        if (_state.value != QingTransferConnectionState.CONNECTED || size !in 0..QingTransferProtocol.MAX_FILE_BYTES || !safeFileName(name)) {
            _error.value = QingTransferErrorCode.TRANSFER_FAILED
            return
        }
        if (transferJob?.isActive == true) return
        _lastCompleted.value = null
        transferJob = scope.launch {
            try {
                stage("offer-send nameLength=${name.length} size=$size")
                val client = socket ?: throw TransferException()
                val decision = CompletableDeferred<Boolean>()
                outgoingDecision = decision
                val rawComplete = CompletableDeferred<Unit>()
                outgoingRawComplete = rawComplete
                QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.FileOffer(name, size))
                stage("offer-sent")
                val accepted = runCatching { withTimeout(120_000) { decision.await() } }.getOrNull()
                if (accepted == null) throw TransferException()
                if (!accepted) {
                    stage("offer-response accepted=false")
                    _error.value = QingTransferErrorCode.PEER_REJECTED
                    return@launch
                }
                stage("offer-response accepted=true")
                val digest = MessageDigest.getInstance("SHA-256")
                resolver.openInputStream(uri)?.use { input ->
                    stage("raw-send-begin declared=$size")
                    streamToSocket(input, client.getOutputStream(), size, name, digest)
                } ?: throw TransferException()
                if (_progress.value?.completed != size) throw TransferException()
                val result = CompletableDeferred<Boolean>()
                resultDecision = result
                QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.FileEnd(digest.digest().hexLower()))
                stage("raw-send-end declared=$size; file-end-sent")
                rawComplete.complete(Unit)
                if (!withTimeoutOrFalse(result, 30_000)) throw TransferException()
                if (!result.await()) throw TransferException()
                stage("result-received ok=true")
                _progress.value = null
                _lastCompleted.value = name
            } catch (_: CancellationException) {
                _error.value = QingTransferErrorCode.CANCELED
            } catch (error: Exception) {
                stage("send-exception type=${error.javaClass.simpleName}")
                _error.value = QingTransferErrorCode.TRANSFER_FAILED
            } finally {
                outgoingDecision = null; outgoingRawComplete?.cancel(); outgoingRawComplete = null; resultDecision = null
                if (_state.value == QingTransferConnectionState.CONNECTED) _progress.value = null
            }
        }
    }

    fun cancelTransfer() { transferJob?.cancel(); transferJob = null; _progress.value = null; disconnect() }

    fun acceptIncoming(uri: Uri) {
        val offer = _incomingOffer.value ?: return
        if (!safeFileName(offer.name)) return
        incomingDecision?.complete(uri)
    }

    fun acceptIncomingAutomatically(): Boolean {
        val offer = _incomingOffer.value ?: return false
        val destination = defaultDestination(offer, requireAutoAccept = true) ?: return false
        incomingDecision?.complete(destination)
        return true
    }

    fun acceptIncomingToDefault(): Boolean {
        val offer = _incomingOffer.value ?: return false
        val destination = defaultDestination(offer, requireAutoAccept = false) ?: return false
        incomingDecision?.complete(destination)
        return true
    }

    fun rejectIncomingFile() { incomingDecision?.complete(null) }

    fun acceptIncoming() {
        if (_state.value != QingTransferConnectionState.WAITING_APPROVAL) return
        val client = socket ?: return
        scope.launch {
            try {
                QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.Accept)
                if (_state.value == QingTransferConnectionState.WAITING_APPROVAL && socket === client) {
                    _incomingPeer.value = null; _state.value = QingTransferConnectionState.CONNECTED
                } else runCatching { client.close() }
            } catch (_: Exception) { fail(QingTransferErrorCode.UNABLE_TO_CONNECT) }
        }
    }

    fun rejectIncoming() {
        val client = socket ?: return
        scope.launch { runCatching { QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.Reject) }; closeToIdle(client) }
    }

    fun disconnect() { closeToIdle() }
    fun clearError() { _error.value = null }
    fun clearCompletion() { _lastCompleted.value = null; _error.value = null }
    fun reportTransferFailure() { _error.value = QingTransferErrorCode.TRANSFER_FAILED }

    fun receivePreferences(): QingTransferReceivePreferences = receivePreferences?.read() ?: QingTransferReceivePreferences()

    fun updateReceivePreferences(value: QingTransferReceivePreferences) { receivePreferences?.write(value) }

    fun defaultDirectoryStatus(): Boolean {
        val uri = receivePreferences?.read()?.defaultTreeUri ?: return false
        return runCatching { resolver.persistedUriPermissions.any { it.uri == Uri.parse(uri) && it.isReadPermission && it.isWritePermission } }.getOrDefault(false)
    }

    private fun defaultDestination(offer: QingTransferFileOffer, requireAutoAccept: Boolean): Uri? {
        val settings = receivePreferences?.read() ?: return null
        if (!settings.useDefaultDirectory || settings.defaultTreeUri.isNullOrBlank() || !defaultDirectoryStatus() ||
            requireAutoAccept && !settings.autoAccept) return null
        val tree = DocumentFile.fromTreeUri(context, Uri.parse(settings.defaultTreeUri!!)) ?: return null
        if (!tree.canWrite()) return null
        val existing = tree.listFiles().mapNotNull { it.name }.toSet()
        return tree.createFile("application/octet-stream", QingTransferReceivePolicy.nextFileName(offer.name, existing))?.uri
    }

    fun canAutomaticallyAccept(): Boolean {
        val settings = receivePreferences?.read() ?: return false
        if (!QingTransferReceivePolicy.automaticAcceptAllowed(settings, defaultDirectoryStatus())) return false
        return runCatching { DocumentFile.fromTreeUri(context, Uri.parse(settings.defaultTreeUri!!))?.canWrite() == true }.getOrDefault(false)
    }

    fun dispose() {
        discovery.onIncomingSocket = null
        transferJob?.cancel()
        scope.coroutineContext[Job]?.cancel()
        closeToIdle()
    }

    private suspend fun handleIncoming(client: Socket) {
        try {
            // Read the bounded first frame before touching connection state. A
            // discovery probe must be acknowledged silently, even when a real
            // session is already connected or awaiting approval.
            client.soTimeout = 5_000
            val first = withTimeout(5_000) { QingTransferProtocol.read(client.getInputStream()) }
            if (first is QingTransferMessage.Probe) {
                QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.ProbeAck(first.nonce))
                client.close()
                return
            }
            if (_state.value != QingTransferConnectionState.IDLE) {
                runCatching { QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.Reject) }
                client.close(); return
            }
            val hello = first as? QingTransferMessage.Hello
            val address = client.inetAddress.hostAddress?.substringBefore('%')
            val devices = DeviceDiscoverySessionStore.get(context)
            val pairing = devices.pairing.snapshot.value
            val trusted = pairing.paired.filter { paired ->
                paired.id in pairing.online && paired.platform == hello?.platform &&
                    (hello?.deviceId?.equals(paired.discoveryId, ignoreCase = true) == true ||
                        hello?.deviceId == null && devices.snapshot.value.nearby.any { nearby ->
                            nearby.discoveryId == paired.discoveryId &&
                                nearby.address.substringBefore('%') == address
                        })
            }.singleOrNull()
            if (trusted == null || hello == null) {
                runCatching { QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.Reject) }
                client.close(); return
            }
            targetDeviceId = trusted.discoveryId
            targetAddress = address
            _lastCompleted.value = null
            socket = client
            client.soTimeout = 0
            incomingName = trusted.name
            _incomingPeer.value = QingTransferPeer("incoming", trusted.name, trusted.platform, "1", listOf("file"), listOfNotNull(address), 0, trusted.discoveryId)
            QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.Accept)
            _state.value = QingTransferConnectionState.CONNECTED
            receiveUntilClosed(client)
        } catch (_: Exception) { closeToIdle(client) }
    }

    private suspend fun receiveUntilClosed(client: Socket) {
        try {
            while (true) {
                when (val message = QingTransferProtocol.read(client.getInputStream())) {
                    QingTransferMessage.FileAccept -> {
                        stage("accept-received")
                        outgoingDecision?.complete(true)
                        outgoingRawComplete?.await()
                    }
                    QingTransferMessage.FileReject -> { stage("reject-received"); outgoingDecision?.complete(false) }
                    is QingTransferMessage.FileResult -> { stage("result-received ok=${message.ok}"); resultDecision?.complete(message.ok) }
                    is QingTransferMessage.FileOffer -> { stage("offer-received nameLength=${message.name.length} size=${message.size}"); handleIncomingFile(client, message) }
                    else -> Unit
                }
            }
        } catch (_: Exception) {
            if (_state.value == QingTransferConnectionState.CONNECTED && _error.value == null &&
                _lastCompleted.value == null) {
                _error.value = QingTransferErrorCode.DISCONNECTED
            }
            closeToIdle(client)
        }
    }

    private suspend fun handleIncomingFile(client: Socket, offer: QingTransferMessage.FileOffer) {
        if (incomingDecision != null || transferJob?.isActive == true) {
            runCatching { QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.FileReject) }
            return
        }
        val decision = CompletableDeferred<Uri?>()
        _lastCompleted.value = null
        incomingDecision = decision
        _incomingOffer.value = QingTransferFileOffer(offer.name, offer.size)
        defaultDestination(QingTransferFileOffer(offer.name, offer.size), requireAutoAccept = true)?.let { decision.complete(it) }
        val destination = try { withTimeoutOrNull(120_000) { decision.await() } }
            finally { incomingDecision = null; _incomingOffer.value = null }
        if (destination == null) {
            stage("incoming-decision accepted=false")
            runCatching { QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.FileReject) }
            return
        }
        try {
            stage("incoming-decision accepted=true")
            QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.FileAccept)
            stage("accept-sent; raw-receive-begin declared=${offer.size}")
            val digest = MessageDigest.getInstance("SHA-256")
            resolver.openOutputStream(destination, "w")?.use { output ->
                streamFromSocket(client.getInputStream(), output, offer.size, offer.name, digest)
            } ?: throw TransferException()
            val end = QingTransferProtocol.read(client.getInputStream())
            val ok = end is QingTransferMessage.FileEnd && end.sha256 == digest.digest().hexLower()
            stage("raw-receive-end declared=${offer.size}; file-end-received hashMatch=$ok")
            runCatching { QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.FileResult(ok)) }
            if (!ok) runCatching { DocumentsContract.deleteDocument(resolver, destination) }
            if (!ok) throw TransferException()
            _progress.value = null
            _lastCompleted.value = offer.name
        } catch (error: Exception) {
            stage("receive-exception type=${error.javaClass.simpleName}")
            runCatching { DocumentsContract.deleteDocument(resolver, destination) }
            _error.value = QingTransferErrorCode.TRANSFER_FAILED
            _progress.value = null
            runCatching { QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.FileResult(false)) }
        }
    }

    private suspend fun streamToSocket(input: InputStream, output: OutputStream, total: Long, name: String, digest: MessageDigest) {
        val buffer = ByteArray(128 * 1024); var done = 0L
        while (done < total) {
            val count = input.read(buffer, 0, minOf(buffer.size.toLong(), total - done).toInt())
            if (count < 0) throw EOFException("short file")
            if (count == 0) continue
            digest.update(buffer, 0, count); output.write(buffer, 0, count); done += count
            _progress.value = QingTransferProgress(name, done, total, false)
        }
        output.flush()
    }

    private suspend fun streamFromSocket(input: InputStream, output: OutputStream, total: Long, name: String, digest: MessageDigest) {
        val buffer = ByteArray(128 * 1024); var done = 0L
        while (done < total) {
            val count = input.read(buffer, 0, minOf(buffer.size.toLong(), total - done).toInt())
            if (count < 0) throw EOFException("short file")
            if (count == 0) continue
            digest.update(buffer, 0, count); output.write(buffer, 0, count); done += count
            _progress.value = QingTransferProgress(name, done, total, true)
        }
        output.flush()
    }

    private fun closeToIdle(expectedSocket: Socket? = null) {
        if (expectedSocket != null && socket !== expectedSocket) { runCatching { expectedSocket.close() }; return }
        ioJob = null
        transferJob?.cancel()
        runCatching { socket?.close() }
        socket = null; incomingName = null; incomingDecision?.cancel(); incomingDecision = null
        outgoingDecision?.cancel(); outgoingDecision = null; resultDecision?.cancel(); resultDecision = null
        outgoingRawComplete?.cancel(); outgoingRawComplete = null
        _incomingOffer.value = null; _incomingPeer.value = null; _progress.value = null
        discovery.setConnectedPeer(null)
        _state.value = QingTransferConnectionState.IDLE
    }

    private fun fail(code: QingTransferErrorCode, serviceName: String? = null) {
        _error.value = code
        serviceName?.let(discovery::forgetPeer)
        closeToIdle()
    }

    private suspend fun withTimeoutOrFalse(deferred: CompletableDeferred<Boolean>, timeout: Long): Boolean =
        runCatching { withTimeout(timeout) { deferred.await() } }.getOrDefault(false)

    private class PeerRejectedException : Exception()
    private class ProtocolException : Exception()
    private class TransferException : Exception()
}

private fun ByteArray.hexLower(): String = joinToString("") { "%02x".format(it) }
private fun safeFileName(value: String): Boolean = value.isNotEmpty() && value.length <= 255 && value != "." && value != ".." && value.none { it.isISOControl() || it == '/' || it == '\\' || it == '"' }

internal fun QingTransferErrorCode.messageRes(): Int = when (this) {
    QingTransferErrorCode.UNABLE_TO_CONNECT -> R.string.qing_transfer_error_unable_to_connect
    QingTransferErrorCode.PEER_REJECTED -> R.string.qing_transfer_error_peer_rejected
    QingTransferErrorCode.INVALID_PROTOCOL -> R.string.qing_transfer_error_invalid_protocol
    QingTransferErrorCode.TRANSFER_FAILED -> R.string.qing_transfer_error_transfer_failed
    QingTransferErrorCode.CANCELED -> R.string.qing_transfer_error_canceled
    QingTransferErrorCode.DISCONNECTED -> R.string.qing_transfer_error_disconnected
}
