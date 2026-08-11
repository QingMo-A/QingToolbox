package com.qingtoolbox.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class QrCodeEncoderTest {
    @Test
    fun encodesAsciiAsSquareBlackAndWhiteMatrix() {
        val matrix = QrCodeEncoder.encode("QingToolbox")

        assertEquals(QrCodeEncoder.OUTPUT_SIZE, matrix.size)
        assertTrue(matrix.darkModuleCount() > 0)
        assertTrue(matrix.darkModuleCount() < matrix.size * matrix.size)
        assertFalse(matrix.isDark(0, 0))
    }

    @Test
    fun encodesUnicodeAndUrls() {
        val unicode = QrCodeEncoder.encode("你好，QingToolbox")
        val url = QrCodeEncoder.encode("https://qingtoolbox.example/?q=hello%20world")

        assertEquals(unicode.size, url.size)
        assertNotEquals(unicode.darkModuleCount(), url.darkModuleCount())
    }

    @Test
    fun rejectsEmptyText() {
        val error = assertThrows(IllegalArgumentException::class.java) {
            QrCodeEncoder.encode("")
        }

        assertEquals("Enter text to generate a QR code.", error.message)
    }
}
