package com.qingtoolbox.android

import android.content.ContentResolver
import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract
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
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.security.MessageDigest

internal enum class QingTransferConnectionState { IDLE, CONNECTING, WAITING_APPROVAL, CONNECTED }

internal enum class QingTransferErrorCode {
    UNABLE_TO_CONNECT,
    PEER_REJECTED,
    INVALID_PROTOCOL,
    TRANSFER_FAILED,
    CANCELED,
    DISCONNECTED,
    PICKER_TIMEOUT,
}

internal data class QingTransferFileOffer(val name: String, val size: Long)

internal data class QingTransferProgress(
    val name: String,
    val completed: Long,
    val total: Long,
    val receiving: Boolean,
)

internal class QingTransferConnection(
    private val context: Context,
    private val discovery: QingTransferDiscovery,
    private val friendlyName: String,
) {
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

    init { discovery.onIncomingSocket = { client -> ioJob = scope.launch { handleIncoming(client) } } }

    fun connect(peer: QingTransferPeer) {
        if (_state.value != QingTransferConnectionState.IDLE) return
        _error.value = null
        _state.value = QingTransferConnectionState.CONNECTING
        ioJob = scope.launch {
            try {
                var connected: Socket? = null
                for (address in peer.addresses) {
                    try {
                        val candidate = Socket()
                        withTimeout(5_000) { candidate.connect(InetSocketAddress(address, peer.port), 5_000) }
                        connected = candidate
                        break
                    } catch (_: Exception) { }
                }
                val client = connected ?: throw IllegalStateException("unable")
                socket = client
                withTimeout(5_000) {
                    QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.Hello("android", friendlyName))
                    when (QingTransferProtocol.read(client.getInputStream())) {
                        QingTransferMessage.Accept -> _state.value = QingTransferConnectionState.CONNECTED
                        QingTransferMessage.Reject -> throw PeerRejectedException()
                        else -> throw ProtocolException()
                    }
                }
                receiveUntilClosed(client)
            } catch (_: CancellationException) { throw CancellationException() }
            catch (error: PeerRejectedException) { fail(QingTransferErrorCode.PEER_REJECTED, peer.serviceName) }
            catch (error: ProtocolException) { fail(QingTransferErrorCode.INVALID_PROTOCOL, peer.serviceName) }
            catch (_: Exception) { fail(QingTransferErrorCode.UNABLE_TO_CONNECT, peer.serviceName) }
        }
    }

    fun sendFile(uri: Uri, name: String, size: Long) {
        if (_state.value != QingTransferConnectionState.CONNECTED || size < 0 || !safeFileName(name)) {
            _error.value = QingTransferErrorCode.TRANSFER_FAILED
            return
        }
        if (transferJob?.isActive == true) return
        transferJob = scope.launch {
            try {
                val client = socket ?: throw TransferException()
                val decision = CompletableDeferred<Boolean>()
                outgoingDecision = decision
                val rawComplete = CompletableDeferred<Unit>()
                outgoingRawComplete = rawComplete
                QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.FileOffer(name, size))
                val accepted = runCatching { withTimeout(30_000) { decision.await() } }.getOrNull()
                if (accepted == null) throw TransferException()
                if (!accepted) {
                    _error.value = QingTransferErrorCode.PEER_REJECTED
                    return@launch
                }
                val digest = MessageDigest.getInstance("SHA-256")
                resolver.openInputStream(uri)?.use { input ->
                    streamToSocket(input, client.getOutputStream(), size, name, digest)
                } ?: throw TransferException()
                if (_progress.value?.completed != size) throw TransferException()
                val result = CompletableDeferred<Boolean>()
                resultDecision = result
                QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.FileEnd(digest.digest().hexLower()))
                rawComplete.complete(Unit)
                if (!withTimeoutOrFalse(result, 30_000)) throw TransferException()
                if (!result.await()) throw TransferException()
                _progress.value = null
            } catch (_: CancellationException) {
                _error.value = QingTransferErrorCode.CANCELED
            } catch (_: Exception) {
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
    fun pickerTimedOut() {
        _error.value = QingTransferErrorCode.PICKER_TIMEOUT
        closeToIdle()
    }
    fun clearError() { _error.value = null }
    fun reportTransferFailure() { _error.value = QingTransferErrorCode.TRANSFER_FAILED }

    fun dispose() {
        discovery.onIncomingSocket = null
        transferJob?.cancel()
        scope.coroutineContext[Job]?.cancel()
        closeToIdle()
    }

    private suspend fun handleIncoming(client: Socket) {
        if (_state.value != QingTransferConnectionState.IDLE) {
            runCatching { QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.Reject) }
            client.close(); return
        }
        socket = client
        try {
            val hello = withTimeout(5_000) { QingTransferProtocol.read(client.getInputStream()) }
            if (hello !is QingTransferMessage.Hello) throw ProtocolException()
            incomingName = hello.name
            _incomingPeer.value = QingTransferPeer("incoming", hello.name, hello.platform, "1", listOf("file"), emptyList(), 0)
            _state.value = QingTransferConnectionState.WAITING_APPROVAL
            receiveUntilClosed(client)
        } catch (_: Exception) { closeToIdle(client) }
    }

    private suspend fun receiveUntilClosed(client: Socket) {
        try {
            while (true) {
                when (val message = QingTransferProtocol.read(client.getInputStream())) {
                    QingTransferMessage.FileAccept -> {
                        outgoingDecision?.complete(true)
                        outgoingRawComplete?.await()
                    }
                    QingTransferMessage.FileReject -> outgoingDecision?.complete(false)
                    is QingTransferMessage.FileResult -> resultDecision?.complete(message.ok)
                    is QingTransferMessage.FileOffer -> handleIncomingFile(client, message)
                    else -> Unit
                }
            }
        } catch (_: Exception) {
            if (_state.value == QingTransferConnectionState.CONNECTED && _error.value == null) {
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
        incomingDecision = decision
        _incomingOffer.value = QingTransferFileOffer(offer.name, offer.size)
        val destination = try { decision.await() } finally { incomingDecision = null; _incomingOffer.value = null }
        if (destination == null) {
            runCatching { QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.FileReject) }
            return
        }
        try {
            QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.FileAccept)
            val digest = MessageDigest.getInstance("SHA-256")
            resolver.openOutputStream(destination, "w")?.use { output ->
                streamFromSocket(client.getInputStream(), output, offer.size, offer.name, digest)
            } ?: throw TransferException()
            val end = QingTransferProtocol.read(client.getInputStream())
            val ok = end is QingTransferMessage.FileEnd && end.sha256 == digest.digest().hexLower()
            runCatching { QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.FileResult(ok)) }
            if (!ok) runCatching { DocumentsContract.deleteDocument(resolver, destination) }
            if (!ok) throw TransferException()
            _progress.value = null
        } catch (_: Exception) {
            runCatching { DocumentsContract.deleteDocument(resolver, destination) }
            _error.value = QingTransferErrorCode.TRANSFER_FAILED
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
    QingTransferErrorCode.PICKER_TIMEOUT -> R.string.qing_transfer_error_picker_timeout
}
