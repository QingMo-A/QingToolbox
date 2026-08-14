package com.qingtoolbox.android

import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.ServerSocket
import kotlin.concurrent.thread

class QingTransferEndpointProbeTest {
    @Test
    fun confirmsExactAckAndRejectsWrongNonceOrClosedEndpoint() {
        val server = ServerSocket(0)
        val port = server.localPort
        val acceptThread = thread(start = true) {
            server.use { listener ->
                listener.accept().use { socket ->
                    val probe = QingTransferProtocol.read(socket.getInputStream()) as QingTransferMessage.Probe
                    QingTransferProtocol.write(socket.getOutputStream(), QingTransferMessage.ProbeAck(probe.nonce))
                }
            }
        }
        val peer = peer(port)
        assertTrue(runBlocking { QingTransferEndpointProbe.confirm(peer) })
        acceptThread.join()

        val wrongServer = ServerSocket(0)
        val wrongThread = thread(start = true) {
            wrongServer.use { listener ->
                listener.accept().use { socket ->
                    val probe = QingTransferProtocol.read(socket.getInputStream()) as QingTransferMessage.Probe
                    QingTransferProtocol.write(socket.getOutputStream(), QingTransferMessage.ProbeAck(probe.nonce.reversed()))
                }
            }
        }
        assertFalse(runBlocking { QingTransferEndpointProbe.confirm(peer(wrongServer.localPort)) })
        wrongThread.join()

        val closed = ServerSocket(0)
        val closedPort = closed.localPort
        closed.close()
        assertFalse(runBlocking { QingTransferEndpointProbe.confirm(peer(closedPort), timeoutMillis = 200) })
    }

    private fun peer(port: Int) = QingTransferPeer(
        serviceName = "Probe._qingtransfer._tcp.local",
        displayName = "Probe",
        platform = "windows",
        protocolVersion = "1",
        capabilities = listOf("file"),
        addresses = listOf("127.0.0.1"),
        port = port,
    )
}
