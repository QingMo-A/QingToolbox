package com.qingtoolbox.android

import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder

class QingTransferProtocolTest {
    @Test
    fun roundTripsHelloAndDecodesHandshake() {
        val frame = QingTransferProtocol.encode(QingTransferMessage.Hello("android", "Phone"))
        val decoded = QingTransferProtocol.decode(frame)
        assertTrue(decoded is QingTransferMessage.Hello)
    }

    @Test
    fun rejectsOversizedMalformedAndUnknownMessages() {
        val oversized = ByteBuffer.allocate(4).order(ByteOrder.BIG_ENDIAN).putInt(QingTransferProtocol.MAX_FRAME_BYTES + 1).array()
        assertNull(QingTransferProtocol.decode(oversized))
        val malformedPayload = "{\"type\":\"hello\",\"v\":2,\"pf\":\"android\",\"name\":\"x\"}".toByteArray()
        val malformed = ByteBuffer.allocate(4 + malformedPayload.size).order(ByteOrder.BIG_ENDIAN).putInt(malformedPayload.size).put(malformedPayload).array()
        assertNull(QingTransferProtocol.decode(malformed))
    }

    @Test
    fun acceptsRejectsAreBounded() {
        assertNotNull(QingTransferProtocol.decode(QingTransferProtocol.encode(QingTransferMessage.Accept)))
        assertNotNull(QingTransferProtocol.decode(QingTransferProtocol.encode(QingTransferMessage.Reject)))
        val frame = QingTransferProtocol.encode(QingTransferMessage.Hello("android", "Phone"))
        assertTrue(frame.size <= QingTransferProtocol.MAX_FRAME_BYTES + 4)
    }

    @Test
    fun roundTripsFileTransferMessagesAndRejectsUnsafeOffers() {
        val offer = QingTransferMessage.FileOffer("报告.txt", 42)
        assertTrue(QingTransferProtocol.decode(QingTransferProtocol.encode(offer)) == offer)
        assertTrue(QingTransferProtocol.decode(QingTransferProtocol.encode(QingTransferMessage.FileAccept)) == QingTransferMessage.FileAccept)
        assertTrue(QingTransferProtocol.decode(QingTransferProtocol.encode(QingTransferMessage.FileReject)) == QingTransferMessage.FileReject)
        val hash = "a".repeat(64)
        assertTrue(QingTransferProtocol.decode(QingTransferProtocol.encode(QingTransferMessage.FileEnd(hash))) == QingTransferMessage.FileEnd(hash))
        assertTrue(QingTransferProtocol.decode(QingTransferProtocol.encode(QingTransferMessage.FileResult(true))) == QingTransferMessage.FileResult(true))
        val unsafe = "{\"type\":\"file_offer\",\"v\":1,\"name\":\"../x\",\"size\":1}".toByteArray()
        val frame = ByteBuffer.allocate(4 + unsafe.size).order(ByteOrder.BIG_ENDIAN).putInt(unsafe.size).put(unsafe).array()
        assertNull(QingTransferProtocol.decode(frame))
    }

    @Test
    fun rejectsNegativeOrUppercaseFileEnd() {
        fun frame(payload: String): ByteArray {
            val bytes = payload.toByteArray()
            return ByteBuffer.allocate(4 + bytes.size).order(ByteOrder.BIG_ENDIAN).putInt(bytes.size).put(bytes).array()
        }
        assertNull(QingTransferProtocol.decode(frame("{\"type\":\"file_offer\",\"v\":1,\"name\":\"x\",\"size\":-1}")))
        assertNull(QingTransferProtocol.decode(frame("{\"type\":\"file_end\",\"v\":1,\"sha256\":\"${"A".repeat(64)}\"}")))
    }
}
