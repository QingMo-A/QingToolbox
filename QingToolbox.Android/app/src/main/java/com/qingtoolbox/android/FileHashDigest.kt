package com.qingtoolbox.android

import java.io.InputStream
import java.security.MessageDigest
import java.util.concurrent.CancellationException

enum class HashAlgorithm(
    val label: String,
    val digestName: String,
) {
    MD5("MD5", "MD5"),
    SHA_1("SHA-1", "SHA-1"),
    SHA_256("SHA-256", "SHA-256"),
    SHA_512("SHA-512", "SHA-512"),
}

data class HashDigestResult(
    val algorithm: HashAlgorithm,
    val value: String,
)

/**
 * Calculates several digests while consuming an input stream exactly once.
 * The caller owns and closes [input].
 */
object StreamingDigest {
    private const val DEFAULT_BUFFER_SIZE = 64 * 1024
    private const val HEX = "0123456789abcdef"

    fun calculate(
        input: InputStream,
        algorithms: Collection<HashAlgorithm>,
        bufferSize: Int = DEFAULT_BUFFER_SIZE,
        onProgress: (Long) -> Unit = {},
        shouldCancel: () -> Boolean = { false },
    ): List<HashDigestResult> {
        require(algorithms.isNotEmpty()) { "Select at least one hash algorithm" }
        require(bufferSize > 0) { "Buffer size must be positive" }

        val selected = algorithms.toList().distinct()
        val digests = selected.associateWith { MessageDigest.getInstance(it.digestName) }
        val buffer = ByteArray(bufferSize)
        var totalRead = 0L

        while (true) {
            if (shouldCancel()) throw CancellationException("Digest calculation cancelled")
            val count = input.read(buffer)
            if (count < 0) break
            if (count == 0) continue

            digests.values.forEach { digest -> digest.update(buffer, 0, count) }
            totalRead += count
            onProgress(totalRead)
        }

        if (shouldCancel()) throw CancellationException("Digest calculation cancelled")
        return selected.map { algorithm ->
            HashDigestResult(algorithm, digests.getValue(algorithm).digest().toHex())
        }
    }

    private fun ByteArray.toHex(): String {
        val bytes = this
        return buildString(bytes.size * 2) {
            bytes.forEach { byte ->
                val value = byte.toInt() and 0xff
                append(HEX[value ushr 4])
                append(HEX[value and 0x0f])
            }
        }
    }
}
