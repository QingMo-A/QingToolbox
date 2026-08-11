package com.qingtoolbox.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class TextCodecTest {
    @Test
    fun base64UsesUtf8AndRoundTripsUnicode() {
        val input = "Hello, 世界"

        assertEquals("SGVsbG8sIOS4lueVjA==", TextCodec.encodeBase64(input))
        assertEquals(input, TextCodec.decodeBase64(TextCodec.encodeBase64(input)))
        assertEquals("", TextCodec.decodeBase64(TextCodec.encodeBase64("")))
    }

    @Test
    fun base64RejectsInvalidInputWithClearMessage() {
        val error = assertThrows(TextCodecException::class.java) {
            TextCodec.decodeBase64("%%%not-base64%%")
        }

        assertEquals("Invalid Base64 input.", error.message)
    }

    @Test
    fun urlEncodingRoundTripsSpacesUnicodeAndSpecialCharacters() {
        val input = "hello world + 中文?&=~"
        val encoded = TextCodec.encodeUrl(input)

        assertEquals("hello%20world%20%2B%20%E4%B8%AD%E6%96%87%3F%26%3D~", encoded)
        assertEquals(input, TextCodec.decodeUrl(encoded))
    }

    @Test
    fun urlDecodeRejectsInvalidPercentEncoding() {
        listOf("%", "%2", "%GG", "%E4%A0").forEach { invalid ->
            val error = assertThrows(TextCodecException::class.java) {
                TextCodec.decodeUrl(invalid)
            }
            assertEquals(
                if (invalid == "%E4%A0") "Invalid URL UTF-8 encoding."
                else "Invalid URL percent-encoding.",
                error.message,
            )
        }
    }
}
