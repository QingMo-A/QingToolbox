package com.qingtoolbox.android

import com.southernstorm.noise.protocol.Noise
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.atomic.AtomicReference
import kotlin.concurrent.thread
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class DevicePairingProtocolTest {
    @Test fun noiseHandshakeAgreesOnCodeAndEncryptedDecisions() {
        val initiatorKey = key()
        val responderKey = key()
        val androidHello = DevicePairingProtocol.Hello("a".repeat(32), "Phone", "android")
        val windowsHello = DevicePairingProtocol.Hello("b".repeat(32), "Desktop", "windows")
        ServerSocket(0, 1, InetAddress.getByName("127.0.0.1")).use { server ->
            val responder = thread {
                server.accept().use { socket ->
                    socket.soTimeout = 5_000
                    DevicePairingProtocol.handshake(socket, responderKey, windowsHello, false).use { pair ->
                        assertEquals(androidHello, pair.remote)
                        assertEquals(8, pair.code.length)
                        assertTrue(pair.receive(socket))
                        pair.send(socket, false)
                    }
                }
            }
            Socket("127.0.0.1", server.localPort).use { socket ->
                socket.soTimeout = 5_000
                DevicePairingProtocol.handshake(socket, initiatorKey, androidHello, true).use { pair ->
                    assertEquals(windowsHello, pair.remote)
                    pair.send(socket, true)
                    assertFalse(pair.receive(socket))
                }
            }
            responder.join(5_000)
            assertFalse(responder.isAlive)
        }
    }

    @Test fun managementHandshakeCarriesEncryptedBatteryWithoutPairingPrompt() {
        val prologue = "QingToolbox device management v1".toByteArray(Charsets.UTF_8)
        val initiatorKey = key()
        val responderKey = key()
        val phone = DevicePairingProtocol.Hello("a".repeat(32), "Phone", "android")
        val desktop = DevicePairingProtocol.Hello("b".repeat(32), "Desktop", "windows")
        val receiverError = AtomicReference<Throwable?>(null)
        ServerSocket(0, 1, InetAddress.getByName("127.0.0.1")).use { server ->
            val responder = thread {
                try {
                    server.accept().use { socket ->
                        socket.soTimeout = 5_000
                        DevicePairingProtocol.handshake(socket, responderKey, desktop, false, prologue).use { channel ->
                            assertEquals(phone, channel.remote)
                            val message = MobileJson.parse(String(channel.receiveMessage(socket), Charsets.UTF_8))
                            assertEquals("battery", message.stringField("action"))
                            assertEquals(42, message.intField("percent"))
                            channel.sendMessage(socket, byteArrayOf('D'.code.toByte()))
                        }
                    }
                } catch (error: Throwable) { receiverError.set(error) }
            }
            Socket("127.0.0.1", server.localPort).use { socket ->
                socket.soTimeout = 5_000
                DevicePairingProtocol.handshake(socket, initiatorKey, phone, true, prologue).use { channel ->
                    assertEquals(desktop, channel.remote)
                    channel.sendMessage(socket, "{\"version\":1,\"action\":\"battery\",\"percent\":42,\"charging\":true}".toByteArray(Charsets.UTF_8))
                    assertTrue(channel.receiveMessage(socket).contentEquals(byteArrayOf('D'.code.toByte())))
                }
            }
            responder.join(5_000)
            assertFalse(responder.isAlive)
            assertNull(receiverError.get())
        }
    }

    @Test fun managementPingReturnsAnEncryptedAcknowledgement() {
        val prologue = "QingToolbox device management v1".toByteArray(Charsets.UTF_8)
        val first = DevicePairingProtocol.Hello("a".repeat(32), "First", "windows")
        val second = DevicePairingProtocol.Hello("b".repeat(32), "Second", "android")
        val firstKey = key()
        val secondKey = key()
        val receiverError = AtomicReference<Throwable?>(null)
        ServerSocket(0, 1, InetAddress.getByName("127.0.0.1")).use { server ->
            val responder = thread {
                try {
                    server.accept().use { socket ->
                        socket.soTimeout = 5_000
                        DevicePairingProtocol.handshake(socket, secondKey, second, false, prologue).use { channel ->
                            assertEquals(first, channel.remote)
                            val request = MobileJson.parse(String(channel.receiveMessage(socket), Charsets.UTF_8))
                            assertEquals("ping", request.stringField("action"))
                            channel.sendMessage(socket, byteArrayOf('D'.code.toByte()))
                        }
                    }
                } catch (error: Throwable) { receiverError.set(error) }
            }
            Socket("127.0.0.1", server.localPort).use { socket ->
                socket.soTimeout = 5_000
                DevicePairingProtocol.handshake(socket, firstKey, first, true, prologue).use { channel ->
                    assertEquals(second, channel.remote)
                    channel.sendMessage(socket, "{\"version\":1,\"action\":\"ping\"}".toByteArray(Charsets.UTF_8))
                    assertTrue(channel.receiveMessage(socket).contentEquals(byteArrayOf('D'.code.toByte())))
                }
            }
            responder.join(5_000)
            assertFalse(responder.isAlive)
            assertNull(receiverError.get())
        }
    }

    private fun key(): ByteArray = Noise.createDH("25519").let { dh ->
        try {
            dh.generateKeyPair()
            ByteArray(32).also { dh.getPrivateKey(it, 0) }
        } finally {
            dh.destroy()
        }
    }
}
