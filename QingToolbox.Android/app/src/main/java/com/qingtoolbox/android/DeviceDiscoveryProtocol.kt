package com.qingtoolbox.android

import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.security.SecureRandom

/** Discovery is deliberately untrusted. Only pairing can establish a device identity. */
internal object DeviceDiscoveryProtocol {
    const val serviceType = "_qingdevice._tcp."
    private const val timeoutMillis = 1_000
    private val idPattern = Regex("[0-9a-f]{32}")

    data class Candidate(
        val serviceName: String,
        val discoveryId: String,
        val name: String,
        val platform: String,
        val addresses: List<InetAddress>,
        val port: Int,
    )

    fun parse(
        serviceName: String,
        attributes: Map<String, String>,
        addresses: List<InetAddress>,
        port: Int,
        ownId: String,
    ): Candidate? {
        if (attributes["v"] != "1" || port !in 1..65535) return null
        val id = attributes["id"] ?: return null
        if (!idPattern.matches(id) || id == ownId) return null
        val platform = attributes["pf"] ?: return null
        if (platform != "windows" && platform != "android") return null
        val name = attributes["name"]?.trim()?.takeIf { it.isNotEmpty() && it.length <= 64 && it.none(Char::isISOControl) }
            ?: return null
        val usable = addresses.filterNot { it.isAnyLocalAddress || it.isMulticastAddress }.take(8)
        if (usable.isEmpty()) return null
        return Candidate(serviceName.lowercase(), id, name, platform, usable, port)
    }

    fun newId(): String = ByteArray(16).also(SecureRandom()::nextBytes).toHex()

    fun probe(candidate: Candidate): InetAddress? {
        val expected = candidate.discoveryId.hexBytes() ?: return null
        for (address in candidate.addresses) {
            val nonce = ByteArray(16).also(SecureRandom()::nextBytes)
            try {
                Socket().use { socket ->
                    socket.connect(InetSocketAddress(address, candidate.port), timeoutMillis)
                    socket.soTimeout = timeoutMillis
                    socket.getOutputStream().write("QDB1".toByteArray(Charsets.US_ASCII) + nonce)
                    val answer = ByteArray(36)
                    java.io.DataInputStream(socket.getInputStream()).readFully(answer)
                    if (answer.copyOfRange(0, 4).contentEquals("QDA1".toByteArray(Charsets.US_ASCII)) &&
                        answer.copyOfRange(4, 20).contentEquals(nonce) &&
                        answer.copyOfRange(20, 36).contentEquals(expected)
                    ) return address
                }
            } catch (_: java.io.IOException) {
                // Try the next address; an advertised endpoint is not trusted.
            }
        }
        return null
    }

    private fun String.hexBytes(): ByteArray? {
        if (!idPattern.matches(this)) return null
        return ByteArray(16) { index -> substring(index * 2, index * 2 + 2).toInt(16).toByte() }
    }

    private fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it.toInt() and 0xff) }
}
