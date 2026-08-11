package com.qingtoolbox.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class QingTransferMetadataTest {
    private val fields = mapOf<String, String?>(
        "v" to "1",
        "pf" to "windows",
        "name" to "Desk",
        "cap" to "file",
    )

    @Test
    fun parsesAndNormalizesServiceName() {
        val result = QingTransferMetadata.parse(
            "Desk._qingtransfer._tcp.local.",
            fields,
            addresses = listOf("192.168.1.5", "192.168.1.5"),
            port = 43125,
        )
        assertTrue(result is QingTransferMetadata.ParseResult.Valid)
        assertEquals(
            "Desk._qingtransfer._tcp.local",
            (result as QingTransferMetadata.ParseResult.Valid).peer.serviceName,
        )
        assertEquals(listOf("192.168.1.5"), result.peer.addresses)
    }

    @Test
    fun rejectsUnsupportedVersionMalformedFieldsAndWrongService() {
        assertTrue(QingTransferMetadata.parse("Desk._qingtransfer._tcp.local", fields + ("v" to "2"), port = 1) is QingTransferMetadata.ParseResult.Invalid)
        assertTrue(QingTransferMetadata.parse("Desk._qingtransfer._tcp.local", fields + ("cap" to "chat"), port = 1) is QingTransferMetadata.ParseResult.Invalid)
        assertTrue(QingTransferMetadata.parse("Desk._other._tcp.local", fields, port = 1) is QingTransferMetadata.ParseResult.Invalid)
        assertTrue(QingTransferMetadata.parse("Desk._qingtransfer._tcp.local", fields + ("name" to "bad\u0001name"), port = 1) is QingTransferMetadata.ParseResult.Invalid)
        assertTrue(QingTransferMetadata.parse("Desk._qingtransfer._tcp.local", fields, port = 65536) is QingTransferMetadata.ParseResult.Invalid)
    }

    @Test
    fun createsContractMetadataAndFullServiceNames() {
        assertEquals(mapOf("v" to "1", "pf" to "android", "name" to "Phone", "cap" to "file"), QingTransferMetadata.create("android", "Phone"))
        assertEquals("Phone._qingtransfer._tcp.local", QingTransferMetadata.fullServiceName("Phone"))
        assertEquals("Phone._qingtransfer._tcp.local", QingTransferMetadata.fullServiceName("Phone._qingtransfer._tcp."))
    }

    @Test
    fun peerTableDeduplicatesAndRemovesByServiceName() {
        val table = QingTransferPeerTable()
        val peer = QingTransferPeer(
            serviceName = "Desk._qingtransfer._tcp.local",
            displayName = "Desk",
            platform = "windows",
            protocolVersion = "1",
            capabilities = listOf("file"),
            addresses = listOf("192.168.1.5"),
            port = 43125,
        )
        assertTrue(table.upsert(peer))
        assertTrue(!table.upsert(peer))
        assertEquals(1, table.snapshot().size)
        assertTrue(table.remove("DESK._QINGTRANSFER._TCP.LOCAL."))
        assertTrue(table.snapshot().isEmpty())
    }
}
