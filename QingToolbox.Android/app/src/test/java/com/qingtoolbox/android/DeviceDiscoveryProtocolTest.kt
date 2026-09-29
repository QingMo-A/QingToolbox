package com.qingtoolbox.android

import java.io.DataInputStream
import java.net.InetAddress
import java.net.ServerSocket
import kotlin.concurrent.thread
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class DeviceDiscoveryProtocolTest {
    private val local = InetAddress.getByName("127.0.0.1")
    private val id = "0123456789abcdef0123456789abcdef"

    @Test fun rejectsUntrustedOrInvalidAdvertisements() {
        val fields = mapOf("v" to "1", "id" to id, "pf" to "windows", "name" to "Desktop")
        assertNotNull(DeviceDiscoveryProtocol.parse("desk", fields, listOf(local), 1234, "f".repeat(32)))
        assertNull(DeviceDiscoveryProtocol.parse("desk", fields, listOf(local), 1234, id))
        assertNull(DeviceDiscoveryProtocol.parse("desk", fields + ("v" to "2"), listOf(local), 1234, "f".repeat(32)))
        assertNull(DeviceDiscoveryProtocol.parse("desk", fields + ("id" to "bad"), listOf(local), 1234, "f".repeat(32)))
        assertNull(DeviceDiscoveryProtocol.parse("desk", fields + ("pf" to "unknown"), listOf(local), 1234, "f".repeat(32)))
        assertNull(DeviceDiscoveryProtocol.parse("desk", fields, emptyList(), 1234, "f".repeat(32)))
        assertNull(DeviceDiscoveryProtocol.parse("desk", fields, listOf(local), 0, "f".repeat(32)))
    }

    @Test fun probeRequiresMatchingNonceAndDiscoveryId() {
        ServerSocket(0, 1, local).use { listener ->
            val server = thread {
                listener.accept().use { socket ->
                    val request = ByteArray(20)
                    DataInputStream(socket.getInputStream()).readFully(request)
                    assertEquals("QDB1", String(request.copyOfRange(0, 4), Charsets.US_ASCII))
                    val idBytes = id.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
                    socket.getOutputStream().write("QDA1".toByteArray() + request.copyOfRange(4, 20) + idBytes)
                }
            }
            val candidate = DeviceDiscoveryProtocol.parse(
                "desk", mapOf("v" to "1", "id" to id, "pf" to "windows", "name" to "Desktop"),
                listOf(local), listener.localPort, "f".repeat(32),
            )!!
            assertEquals(local, DeviceDiscoveryProtocol.probe(candidate))
            server.join(2_000)
        }
        ServerSocket(0, 1, local).use { listener ->
            val server = thread {
                listener.accept().use { socket ->
                    val request = ByteArray(20)
                    DataInputStream(socket.getInputStream()).readFully(request)
                    socket.getOutputStream().write("QDA1".toByteArray() + ByteArray(16) + ByteArray(16))
                }
            }
            val candidate = DeviceDiscoveryProtocol.parse(
                "desk", mapOf("v" to "1", "id" to id, "pf" to "windows", "name" to "Desktop"),
                listOf(local), listener.localPort, "f".repeat(32),
            )!!
            assertNull(DeviceDiscoveryProtocol.probe(candidate))
            server.join(2_000)
        }
    }
}
