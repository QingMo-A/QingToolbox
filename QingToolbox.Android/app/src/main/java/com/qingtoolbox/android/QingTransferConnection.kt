package com.qingtoolbox.android

import kotlinx.coroutines.CancellationException
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
import java.net.InetSocketAddress
import java.net.Socket

internal enum class QingTransferConnectionState { IDLE, CONNECTING, WAITING_APPROVAL, CONNECTED }

internal class QingTransferConnection(
    private val discovery: QingTransferDiscovery,
    private val friendlyName: String,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val _state = MutableStateFlow(QingTransferConnectionState.IDLE)
    val state: StateFlow<QingTransferConnectionState> = _state.asStateFlow()
    private val _incomingPeer = MutableStateFlow<QingTransferPeer?>(null)
    val incomingPeer: StateFlow<QingTransferPeer?> = _incomingPeer.asStateFlow()
    private val _error = MutableStateFlow<String?>(null)
    val error: StateFlow<String?> = _error.asStateFlow()
    private var socket: Socket? = null
    private var ioJob: Job? = null
    private var incomingName: String? = null
    private var incomingPlatform: String? = null

    init { discovery.onIncomingSocket = { client -> scope.launch { handleIncoming(client) } } }

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
                val client = connected ?: error("Unable to reach the device.")
                socket = client
                withTimeout(5_000) {
                    QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.Hello("android", friendlyName))
                    when (QingTransferProtocol.read(client.getInputStream())) {
                        QingTransferMessage.Accept -> _state.value = QingTransferConnectionState.CONNECTED
                        QingTransferMessage.Reject -> error("The peer rejected this request.")
                        else -> error("The peer sent an invalid response.")
                    }
                }
                receiveUntilClosed(client)
            } catch (cancelled: CancellationException) { throw cancelled }
            catch (error: Exception) { _error.value = error.message ?: "Unable to connect to the device."; closeToIdle(); }
        }
    }

    fun acceptIncoming() {
        if (_state.value != QingTransferConnectionState.WAITING_APPROVAL) return
        val client = socket ?: return
        scope.launch {
            runCatching { QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.Accept); _state.value = QingTransferConnectionState.CONNECTED }
                .onFailure { closeToIdle() }
        }
    }

    fun rejectIncoming() {
        val client = socket ?: return
        scope.launch { runCatching { QingTransferProtocol.write(client.getOutputStream(), QingTransferMessage.Reject) }; closeToIdle() }
    }

    fun disconnect() { closeToIdle() }
    fun clearError() { _error.value = null }

    fun dispose() {
        discovery.onIncomingSocket = null
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
            val hello = withContext(Dispatchers.IO) { withTimeout(5_000) { QingTransferProtocol.read(client.getInputStream()) } }
            if (hello !is QingTransferMessage.Hello) error("The peer sent an invalid request.")
            incomingName = hello.name; incomingPlatform = hello.platform
            _incomingPeer.value = QingTransferPeer("incoming", hello.name, hello.platform, "1", listOf("file"), emptyList(), 0)
            _state.value = QingTransferConnectionState.WAITING_APPROVAL
            // Approval is handled by the UI; keep this socket alive until it is accepted or rejected.
            receiveUntilClosed(client)
        } catch (_: Exception) { closeToIdle() }
    }

    private suspend fun receiveUntilClosed(client: Socket) {
        try {
            while (true) {
                val message = QingTransferProtocol.read(client.getInputStream()) ?: error("Malformed message")
            }
        } catch (_: Exception) { closeToIdle() }
    }

    private fun closeToIdle() {
        val job = ioJob
        ioJob = null
        job?.cancel()
        runCatching { socket?.close() }
        socket = null; incomingName = null; incomingPlatform = null
        _incomingPeer.value = null
        _state.value = QingTransferConnectionState.IDLE
    }
}
