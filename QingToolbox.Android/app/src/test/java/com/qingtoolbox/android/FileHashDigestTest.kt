package com.qingtoolbox.android

import java.io.ByteArrayInputStream
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class FileHashDigestTest {
    @Test
    fun calculatesAllAlgorithmsFromOneStream() {
        val input = "The quick brown fox jumps over the lazy dog"
            .toByteArray(Charsets.UTF_8)
        val reads = CountingInputStream(input)

        val results = StreamingDigest.calculate(
            input = reads,
            algorithms = HashAlgorithm.entries.toList(),
            bufferSize = 7,
        ).associate { it.algorithm to it.value }
        assertEquals("9e107d9d372bb6826bd81d3542a419d6", results[HashAlgorithm.MD5])
        assertEquals("2fd4e1c67a2d28fced849ee1bb76e7391b93eb12", results[HashAlgorithm.SHA_1])
        assertEquals(
            "d7a8fbb307d7809469ca9abcb0082e4f8d5651e46d3cdb762d02d0bf37c9e592",
            results[HashAlgorithm.SHA_256],
        )
        assertEquals(
            "07e547d9586f6a73f73fbac0435ed76951218fb7d0c8d788a309d785436bbb642e93a252a954f23912547d1e8a3b5ed6e1bfd7097821233fa0538f3db854fee6",
            results[HashAlgorithm.SHA_512],
        )
        assertTrue(reads.readCalls > 1)
    }

    @Test
    fun selectedAlgorithmsAreUpdatedTogether() {
        val input = ByteArrayInputStream("QingToolbox".toByteArray())
        val results = StreamingDigest.calculate(
            input = input,
            algorithms = listOf(HashAlgorithm.SHA_256, HashAlgorithm.SHA_256),
        )

        assertEquals(1, results.size)
        assertEquals(HashAlgorithm.SHA_256, results.single().algorithm)
    }

    private class CountingInputStream(data: ByteArray) : ByteArrayInputStream(data) {
        var readCalls: Int = 0
            private set

        override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
            readCalls++
            return super.read(buffer, offset, length)
        }
    }
}
