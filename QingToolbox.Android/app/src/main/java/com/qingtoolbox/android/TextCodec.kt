package com.qingtoolbox.android

import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets
import java.util.Base64

/**
 * The conversions the text codec performs.
 *
 * The names are the module-facing ones: a web module names an operation on the bridge and
 * carries its own labels, so the shell never needs a display name for these.
 */
enum class TextCodecOperation {
    BASE64_ENCODE,
    BASE64_DECODE,
    URL_ENCODE,
    URL_DECODE,
}

enum class TextCodecError {
    INVALID_BASE64,
    INVALID_URL_PERCENT,
    INVALID_URL_UTF8,
}

class TextCodecException(val error: TextCodecError) : IllegalArgumentException()

object TextCodec {
    fun convert(operation: TextCodecOperation, input: String): String = when (operation) {
        TextCodecOperation.BASE64_ENCODE -> encodeBase64(input)
        TextCodecOperation.BASE64_DECODE -> decodeBase64(input)
        TextCodecOperation.URL_ENCODE -> encodeUrl(input)
        TextCodecOperation.URL_DECODE -> decodeUrl(input)
    }

    fun encodeBase64(input: String): String =
        Base64.getEncoder().encodeToString(input.toByteArray(StandardCharsets.UTF_8))

    fun decodeBase64(input: String): String {
        val bytes = try {
            Base64.getDecoder().decode(input)
        } catch (_: IllegalArgumentException) {
            throw TextCodecException(TextCodecError.INVALID_BASE64)
        }
        return decodeUtf8(bytes, TextCodecError.INVALID_BASE64)
    }

    fun encodeUrl(input: String): String {
        val bytes = input.toByteArray(StandardCharsets.UTF_8)
        return buildString(bytes.size) {
            bytes.forEach { byte ->
                val value = byte.toInt() and 0xff
                if (isUrlUnreserved(value)) {
                    append(value.toChar())
                } else {
                    append('%')
                    append(HEX[value ushr 4])
                    append(HEX[value and 0x0f])
                }
            }
        }
    }

    fun decodeUrl(input: String): String {
        val decoded = StringBuilder(input.length)
        var index = 0
        while (index < input.length) {
            if (input[index] != '%') {
                decoded.append(input[index])
                index++
                continue
            }

            val bytes = ByteArrayOutputStream()
            while (index < input.length && input[index] == '%') {
                if (index + 2 >= input.length) {
                    throw TextCodecException(TextCodecError.INVALID_URL_PERCENT)
                }
                val high = hexValue(input[index + 1])
                val low = hexValue(input[index + 2])
                if (high < 0 || low < 0) {
                    throw TextCodecException(TextCodecError.INVALID_URL_PERCENT)
                }
                bytes.write((high shl 4) or low)
                index += 3
            }
            decoded.append(decodeUtf8(bytes.toByteArray(), TextCodecError.INVALID_URL_UTF8))
        }
        return decoded.toString()
    }

    private fun decodeUtf8(bytes: ByteArray, error: TextCodecError): String = try {
        StandardCharsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
            .decode(ByteBuffer.wrap(bytes))
            .toString()
    } catch (_: java.nio.charset.CharacterCodingException) {
        throw TextCodecException(error)
    }

    private fun isUrlUnreserved(value: Int): Boolean =
        value in 'A'.code..'Z'.code ||
            value in 'a'.code..'z'.code ||
            value in '0'.code..'9'.code ||
            value == '-'.code || value == '.'.code || value == '_'.code || value == '~'.code

    private fun hexValue(char: Char): Int = when (char) {
        in '0'..'9' -> char - '0'
        in 'a'..'f' -> char - 'a' + 10
        in 'A'..'F' -> char - 'A' + 10
        else -> -1
    }

    private const val HEX = "0123456789ABCDEF"
}
