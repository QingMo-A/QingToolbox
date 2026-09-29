package com.qingtoolbox.android

import com.southernstorm.noise.protocol.CipherStatePair
import com.southernstorm.noise.protocol.HandshakeState
import java.io.DataInputStream
import java.io.DataOutputStream
import java.math.BigInteger
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.security.MessageDigest

/** Exact Android counterpart of the Windows QDP1 / Noise XX wire contract. */
internal object DevicePairingProtocol {
    private const val pattern = "Noise_XX_25519_ChaChaPoly_BLAKE2s"
    private const val maxFrame = 1024
    private val prologue = "QingToolbox device pairing v1".toByteArray(Charsets.UTF_8)
    private val codeDomain = "QingToolbox device pairing code v1".toByteArray(Charsets.UTF_8)
    private val idPattern = Regex("[0-9a-f]{32}")

    data class Hello(val discoveryId: String, val name: String, val platform: String)

    class Result(
        val remote: Hello,
        val remoteKey: ByteArray,
        val code: String,
        private val ciphers: CipherStatePair,
    ) : AutoCloseable {
        fun send(socket: Socket, approved: Boolean) {
            sendMessage(socket, byteArrayOf(if (approved) 'A'.code.toByte() else 'R'.code.toByte()))
        }

        fun sendMessage(socket: Socket, plain: ByteArray) {
            require(plain.size in 1..maxFrame - 16)
            val encrypted = ByteArray(plain.size + 16)
            val count = ciphers.sender.encryptWithAd(null, plain, 0, encrypted, 0, plain.size)
            writeFrame(socket, encrypted.copyOf(count))
        }

        fun receive(socket: Socket): Boolean {
            val plain = receiveMessage(socket)
            if (plain.size != 1 || (plain[0] != 'A'.code.toByte() && plain[0] != 'R'.code.toByte())) {
                throw IllegalArgumentException("Invalid pairing decision")
            }
            return plain[0] == 'A'.code.toByte()
        }

        fun receiveMessage(socket: Socket): ByteArray {
            val encrypted = readFrame(socket)
            val plain = ByteArray(encrypted.size)
            val count = ciphers.receiver.decryptWithAd(null, encrypted, 0, plain, 0, encrypted.size)
            return plain.copyOf(count)
        }

        override fun close() = ciphers.destroy()
    }

    fun handshake(
        socket: Socket,
        privateKey: ByteArray,
        own: Hello,
        initiator: Boolean,
        handshakePrologue: ByteArray = prologue,
    ): Result {
        require(privateKey.size == 32 && validHello(own))
        val state = HandshakeState(pattern, if (initiator) HandshakeState.INITIATOR else HandshakeState.RESPONDER)
        try {
            state.localKeyPair.setPrivateKey(privateKey, 0)
            state.setPrologue(handshakePrologue, 0, handshakePrologue.size)
            state.start()
            val localPayload = MobileJsonObject()
                .string("discoveryId", own.discoveryId)
                .string("name", own.name)
                .string("platform", own.platform)
                .build().toByteArray(Charsets.UTF_8)
            val remote = if (initiator) {
                writeNoise(state, socket, ByteArray(0))
                val answer = readNoise(state, socket)
                val hello = parseHello(answer)
                writeNoise(state, socket, localPayload)
                hello
            } else {
                require(readNoise(state, socket).isEmpty()) { "Unexpected initiator payload" }
                writeNoise(state, socket, localPayload)
                parseHello(readNoise(state, socket))
            }
            require(state.action == HandshakeState.SPLIT) { "Incomplete Noise handshake" }
            val remoteKeyState = state.remotePublicKey
            require(remoteKeyState != null && remoteKeyState.hasPublicKey()) { "Missing remote key" }
            val key = ByteArray(32)
            remoteKeyState.getPublicKey(key, 0)
            val code = verificationCode(state.handshakeHash)
            return Result(remote, key, code, state.split())
        } finally {
            state.destroy()
        }
    }

    fun verificationCode(handshakeHash: ByteArray): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(codeDomain + handshakeHash)
        val number = BigInteger(1, digest.copyOfRange(0, 8)).mod(BigInteger.valueOf(100_000_000)).toInt()
        return number.toString().padStart(8, '0')
    }

    private fun writeNoise(state: HandshakeState, socket: Socket, payload: ByteArray) {
        val output = ByteArray(maxFrame)
        val count = state.writeMessage(output, 0, payload, 0, payload.size)
        writeFrame(socket, output.copyOf(count))
    }

    private fun readNoise(state: HandshakeState, socket: Socket): ByteArray {
        val message = readFrame(socket)
        val payload = ByteArray(maxFrame)
        val count = state.readMessage(message, 0, message.size, payload, 0)
        return payload.copyOf(count)
    }

    private fun writeFrame(socket: Socket, message: ByteArray) {
        require(message.size in 1..maxFrame)
        DataOutputStream(socket.getOutputStream()).apply {
            writeShort(message.size)
            write(message)
            flush()
        }
    }

    private fun readFrame(socket: Socket): ByteArray {
        val input = DataInputStream(socket.getInputStream())
        val length = input.readUnsignedShort()
        require(length in 1..maxFrame) { "Invalid pairing frame length" }
        return ByteArray(length).also(input::readFully)
    }

    private fun parseHello(bytes: ByteArray): Hello {
        val text = Charsets.UTF_8.newDecoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
            .decode(ByteBuffer.wrap(bytes)).toString()
        val parsed = MobileJson.parse(text)
        val hello = Hello(
            discoveryId = parsed.stringField("discoveryId") ?: "",
            name = parsed.stringField("name") ?: "",
            platform = parsed.stringField("platform") ?: "",
        )
        require(validHello(hello)) { "Invalid pairing identity" }
        return hello
    }

    private fun validHello(hello: Hello): Boolean =
        idPattern.matches(hello.discoveryId) &&
            hello.name.isNotBlank() &&
            hello.name.codePointCount(0, hello.name.length) <= 64 &&
            hello.name.none { it.isISOControl() } &&
            hello.platform in setOf("android", "windows")
}
