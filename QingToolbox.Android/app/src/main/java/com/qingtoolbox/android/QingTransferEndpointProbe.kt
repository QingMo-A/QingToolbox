package com.qingtoolbox.android

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import java.net.InetSocketAddress
import java.net.Socket

/**
 * Bounded, silent compatibility check for one DNS-SD endpoint. It sends only a
 * nonce-bound probe frame and never enters the user connection flow.
 */
internal object QingTransferEndpointProbe {
    fun endpointKey(peer: QingTransferPeer): String =
        buildString {
            append(peer.serviceName.trim().trimEnd('.').lowercase())
            append('|').append(peer.port).append('|')
            peer.addresses.distinct().sorted().forEachIndexed { index, address ->
                if (index > 0) append(',')
                append(address)
            }
        }

    suspend fun confirm(peer: QingTransferPeer, timeoutMillis: Long = 1_000L): Boolean = withContext(Dispatchers.IO) {
        if (peer.port !in 1..65_535 || peer.addresses.isEmpty()) return@withContext false
        peer.addresses.distinct().any { address ->
            runCatching {
                    withTimeout(timeoutMillis) {
                        Socket().use { socket ->
                            socket.connect(InetSocketAddress(address, peer.port), timeoutMillis.toInt())
                            socket.soTimeout = timeoutMillis.toInt()
                            val nonce = QingTransferProtocol.createProbeNonce()
                        QingTransferProtocol.write(socket.getOutputStream(), QingTransferMessage.Probe(nonce))
                        val response = QingTransferProtocol.read(socket.getInputStream())
                        response is QingTransferMessage.ProbeAck && response.nonce == nonce
                    }
                }
            }.getOrElse { error ->
                if (error is CancellationException) throw error
                false
            }
        }
    }

}
