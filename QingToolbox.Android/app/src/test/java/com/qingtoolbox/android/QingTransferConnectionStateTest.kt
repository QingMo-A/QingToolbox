package com.qingtoolbox.android

import org.junit.Assert.assertFalse
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
}
