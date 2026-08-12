package com.qingtoolbox.android

import org.junit.Assert.assertFalse
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class QingTransferConnectionStateTest {
    private val peer = QingTransferPeer("incoming", "Desk", "windows", "1", listOf("file"), emptyList(), 0)

    @Test
    fun incomingDialogIsOnlyVisibleWhileWaitingApproval() {
        assertTrue(shouldShowIncomingDialog(QingTransferConnectionState.WAITING_APPROVAL, peer))
        assertFalse(shouldShowIncomingDialog(QingTransferConnectionState.CONNECTED, peer))
        assertFalse(shouldShowIncomingDialog(QingTransferConnectionState.IDLE, null))
    }

    @Test
    fun transferErrorsHaveLocalizedResourceMappings() {
        val resources = QingTransferErrorCode.entries.map { it.messageRes() }
        assertEquals(QingTransferErrorCode.entries.size, resources.distinct().size)
        resources.forEach { assertNotEquals(0, it) }
    }

    @Test
    fun pickerLeaseOnlyPreservesSessionWhileActive() {
        assertTrue(shouldKeepTransferSession(QingTransferConnectionState.CONNECTED))
        assertFalse(shouldKeepTransferSession(QingTransferConnectionState.IDLE))
    }

    @Test
    fun onlyConnectedSessionsAreRetainedWhenActivityStops() {
        assertTrue(shouldKeepTransferSession(QingTransferConnectionState.CONNECTED))
        assertFalse(shouldKeepTransferSession(QingTransferConnectionState.CONNECTING))
        assertFalse(shouldKeepTransferSession(QingTransferConnectionState.WAITING_APPROVAL))
    }
}
