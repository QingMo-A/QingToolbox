package com.qingtoolbox.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class QingTransferReceivePreferencesTest {
    @Test fun missingOrInvalidConditionsNeverAutoAccept() {
        assertFalse(QingTransferReceivePolicy.automaticAcceptAllowed(QingTransferReceivePreferences(), true))
        assertFalse(QingTransferReceivePolicy.automaticAcceptAllowed(QingTransferReceivePreferences("tree", false, true), true))
        assertFalse(QingTransferReceivePolicy.automaticAcceptAllowed(QingTransferReceivePreferences("tree", true, true), false))
        assertTrue(QingTransferReceivePolicy.automaticAcceptAllowed(QingTransferReceivePreferences("tree", true, true), true))
    }

    @Test fun destinationNeverOverwritesExistingName() {
        assertEquals("photo (2).txt", QingTransferReceivePolicy.nextFileName("photo.txt", setOf("photo.txt", "photo (1).txt")))
        assertEquals("README (1)", QingTransferReceivePolicy.nextFileName("README", setOf("README")))
    }
}
