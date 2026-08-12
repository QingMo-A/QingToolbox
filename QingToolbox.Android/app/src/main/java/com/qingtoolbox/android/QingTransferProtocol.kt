package com.qingtoolbox.android

import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

internal sealed interface QingTransferMessage {
    data class Hello(val platform: String, val name: String) : QingTransferMessage
    data object Accept : QingTransferMessage
    data object Reject : QingTransferMessage
    data class FileOffer(val name: String, val size: Long) : QingTransferMessage
    data object FileAccept : QingTransferMessage
    data object FileReject : QingTransferMessage
    data class FileEnd(val sha256: String) : QingTransferMessage
    data class FileResult(val ok: Boolean) : QingTransferMessage
}

internal object QingTransferProtocol {
    const val MAX_FRAME_BYTES = 4096
    private const val MAX_FIELD_LENGTH = 128

    fun encode(message: QingTransferMessage): ByteArray {
        val json = when (message) {
            is QingTransferMessage.Hello -> {
                require(message.platform in setOf("windows", "android") && safe(message.platform) && safe(message.name))
                "{\"type\":\"hello\",\"v\":1,\"pf\":\"${message.platform}\",\"name\":\"${message.name}\"}"
            }
            QingTransferMessage.Accept -> "{\"type\":\"accept\",\"v\":1}"
            QingTransferMessage.Reject -> "{\"type\":\"reject\",\"v\":1}"
            is QingTransferMessage.FileOffer -> {
                require(safeFileName(message.name) && message.size >= 0)
                "{\"type\":\"file_offer\",\"v\":1,\"name\":\"${escape(message.name)}\",\"size\":${message.size}}"
            }
            QingTransferMessage.FileAccept -> "{\"type\":\"file_accept\",\"v\":1}"
            QingTransferMessage.FileReject -> "{\"type\":\"file_reject\",\"v\":1}"
            is QingTransferMessage.FileEnd -> {
                require(message.sha256.matches(Regex("[0-9a-f]{64}")))
                "{\"type\":\"file_end\",\"v\":1,\"sha256\":\"${message.sha256}\"}"
            }
            is QingTransferMessage.FileResult -> "{\"type\":\"file_result\",\"v\":1,\"ok\":${message.ok}}"
        }.toByteArray(Charsets.UTF_8)
        require(json.isNotEmpty() && json.size <= MAX_FRAME_BYTES) { "Protocol frame is too large." }
        return ByteBuffer.allocate(4 + json.size).order(ByteOrder.BIG_ENDIAN).putInt(json.size).put(json).array()
    }

    fun decode(frame: ByteArray): QingTransferMessage? {
        if (frame.size < 5 || frame.size > MAX_FRAME_BYTES + 4) return null
        val size = ByteBuffer.wrap(frame, 0, 4).order(ByteOrder.BIG_ENDIAN).int
        if (size <= 0 || size > MAX_FRAME_BYTES || size != frame.size - 4) return null
        return parse(frame.copyOfRange(4, frame.size))
    }

    fun read(input: InputStream): QingTransferMessage? {
        val header = ByteArray(4)
        readExactly(input, header)
        val size = ByteBuffer.wrap(header).order(ByteOrder.BIG_ENDIAN).int
        if (size <= 0 || size > MAX_FRAME_BYTES) throw IllegalArgumentException("Protocol frame is too large.")
        val payload = ByteArray(size)
        readExactly(input, payload)
        return parse(payload)
    }

    fun write(output: OutputStream, message: QingTransferMessage) {
        output.write(encode(message)); output.flush()
    }

    private fun parse(payload: ByteArray): QingTransferMessage? {
        val fields = JsonObjectParser(String(payload, Charsets.UTF_8)).parse() ?: return null
        val type = fields["type"]?.takeIf { it.isString }?.value ?: return null
        if (fields["v"]?.let { it.isString || it.value != "1" } != false) return null
        return when (type) {
            "accept" -> if (fields.size == 2) QingTransferMessage.Accept else null
            "reject" -> if (fields.size == 2) QingTransferMessage.Reject else null
            "file_accept" -> if (fields.size == 2) QingTransferMessage.FileAccept else null
            "file_reject" -> if (fields.size == 2) QingTransferMessage.FileReject else null
            "hello" -> {
                if (fields.size != 4) return null
                val platform = fields["pf"]?.takeIf { it.isString }?.value ?: return null
                val name = fields["name"]?.takeIf { it.isString }?.value ?: return null
                if (platform in setOf("windows", "android") && safe(platform) && safe(name)) QingTransferMessage.Hello(platform, name) else null
            }
            "file_offer" -> {
                if (fields.size != 4) return null
                val name = fields["name"]?.takeIf { it.isString }?.value ?: return null
                val size = fields["size"]?.takeIf { !it.isString }?.value?.toLongOrNull() ?: return null
                if (safeFileName(name) && size >= 0) QingTransferMessage.FileOffer(name, size) else null
            }
            "file_end" -> {
                if (fields.size != 3) return null
                val hash = fields["sha256"]?.takeIf { it.isString }?.value ?: return null
                if (hash.matches(Regex("[0-9a-f]{64}"))) QingTransferMessage.FileEnd(hash) else null
            }
            "file_result" -> {
                if (fields.size != 3) return null
                val ok = fields["ok"]?.takeIf { !it.isString }?.value ?: return null
                if (ok == "true" || ok == "false") QingTransferMessage.FileResult(ok == "true") else null
            }
            else -> null
        }
    }

    private fun safe(value: String): Boolean = value.isNotEmpty() && value.length <= MAX_FIELD_LENGTH && value.none { it.isISOControl() || it == '"' || it == '\\' }

    private fun safeFileName(value: String): Boolean =
        value.isNotEmpty() && value.length <= 255 && value != "." && value != ".." &&
            value.none { it.isISOControl() || it == '/' || it == '\\' || it == '"' }

    private fun escape(value: String): String = value.replace("\\", "\\\\").replace("\"", "\\\"")

    private data class JsonValue(val value: String, val isString: Boolean)

    /** Bounded object parser: protocol tests stay pure JVM without a permissive JSON dependency. */
    private class JsonObjectParser(private val text: String) {
        private var index = 0

        fun parse(): Map<String, JsonValue>? {
            skipWhitespace()
            if (!consume('{')) return null
            val result = linkedMapOf<String, JsonValue>()
            skipWhitespace()
            if (consume('}')) return result.takeIf { atEnd() }
            while (true) {
                skipWhitespace()
                val key = parseString() ?: return null
                if (result.containsKey(key)) return null
                skipWhitespace()
                if (!consume(':')) return null
                skipWhitespace()
                result[key] = parseValue() ?: return null
                skipWhitespace()
                when {
                    consume('}') -> return result.takeIf { atEnd() }
                    consume(',') -> Unit
                    else -> return null
                }
            }
        }

        private fun parseValue(): JsonValue? {
            if (index >= text.length) return null
            if (text[index] == '"') return parseString()?.let { JsonValue(it, true) }
            val start = index
            while (index < text.length && text[index] != ',' && text[index] != '}' && !text[index].isWhitespace()) index++
            if (start == index) return null
            return JsonValue(text.substring(start, index), false)
        }

        private fun parseString(): String? {
            if (!consume('"')) return null
            val result = StringBuilder()
            while (index < text.length) {
                val character = text[index++]
                when {
                    character == '"' -> return result.toString()
                    character == '\\' -> {
                        if (index >= text.length) return null
                        when (val escaped = text[index++]) {
                            '"', '\\', '/' -> result.append(escaped)
                            'b' -> result.append('\b')
                            'f' -> result.append('\u000C')
                            'n' -> result.append('\n')
                            'r' -> result.append('\r')
                            't' -> result.append('\t')
                            'u' -> {
                                if (index + 4 > text.length) return null
                                val hex = text.substring(index, index + 4)
                                if (hex.any { it.digitToIntOrNull(16) == null }) return null
                                result.append(hex.toInt(16).toChar()); index += 4
                            }
                            else -> return null
                        }
                    }
                    character.isISOControl() -> return null
                    else -> result.append(character)
                }
            }
            return null
        }

        private fun skipWhitespace() { while (index < text.length && text[index].isWhitespace()) index++ }
        private fun consume(expected: Char): Boolean = if (index < text.length && text[index] == expected) { index++; true } else false
        private fun atEnd(): Boolean = index == text.length
    }

    private fun readExactly(input: InputStream, buffer: ByteArray) {
        var offset = 0
        while (offset < buffer.size) {
            val count = input.read(buffer, offset, buffer.size - offset)
            if (count < 0) throw EOFException("The peer closed the protocol stream.")
            offset += count
        }
    }
}
