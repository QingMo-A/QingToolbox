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
        val json = String(payload, Charsets.UTF_8)
        if (json == "{\"type\":\"accept\",\"v\":1}") return QingTransferMessage.Accept
        if (json == "{\"type\":\"reject\",\"v\":1}") return QingTransferMessage.Reject
        val match = Regex("^\\{\\\"type\\\":\\\"hello\\\",\\\"v\\\":1,\\\"pf\\\":\\\"(windows|android)\\\",\\\"name\\\":\\\"([^\\\"\\\\\\u0000-\\u001F]*)\\\"\\}$").matchEntire(json)
            ?: return null
        val platform = match.groupValues[1]
        val name = match.groupValues[2]
        return if (safe(platform) && safe(name)) QingTransferMessage.Hello(platform, name) else null
    }

    private fun safe(value: String): Boolean = value.isNotEmpty() && value.length <= MAX_FIELD_LENGTH && value.none { it.isISOControl() || it == '"' || it == '\\' }

    private fun readExactly(input: InputStream, buffer: ByteArray) {
        var offset = 0
        while (offset < buffer.size) {
            val count = input.read(buffer, offset, buffer.size - offset)
            if (count < 0) throw EOFException("The peer closed the protocol stream.")
            offset += count
        }
    }
}
