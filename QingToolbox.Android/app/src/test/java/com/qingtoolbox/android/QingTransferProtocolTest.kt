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
}
