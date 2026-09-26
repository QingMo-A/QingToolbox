package com.qingtoolbox.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class MobileJsonTest {
    @Test
    fun readsObjectsArraysAndScalars() {
        val root = MobileJson.parse(
            """
            {
              "name": "qing.text-codec",
              "count": 3,
              "ratio": 1.5,
              "enabled": true,
              "missing": null,
              "items": ["a", "b"]
            }
            """.trimIndent(),
        )

        assertEquals("qing.text-codec", root.stringField("name"))
        assertEquals(3, root.intField("count"))
        assertEquals(true, root.field("enabled")?.asBoolOrNull())
        assertTrue(root.field("missing") is MobileJsonValue.Null)
        assertEquals(listOf("a", "b"), root.stringArrayField("items"))
        // A fractional number is not an integer, so it must not be read as one.
        assertNull(root.field("ratio")?.asIntOrNull())
    }

    @Test
    fun decodesEscapesInsideStrings() {
        val root = MobileJson.parse("""{"text":"line\nquote\"slash\\unicode\u4e2d"}""")
        assertEquals("line\nquote\"slash\\unicode\u4e2d", root.stringField("text"))
    }

    @Test
    fun rejectsMalformedDocuments() {
        listOf(
            "{",
            "{\"a\"}",
            "{\"a\": }",
            "[1,]",
            "\"unterminated",
            "}",
            "{\"a\":1} trailing",
            "{\"a\":01x}",
        ).forEach { malformed ->
            val failure = runCatching { MobileJson.parse(malformed) }.exceptionOrNull()
            assertTrue("expected '$malformed' to be rejected", failure is MobileJsonException)
        }
    }

    @Test
    fun quotesStringsSoTheyCanBeEmbeddedInScript() {
        assertEquals("\"plain\"", MobileJson.quote("plain"))
        assertEquals("\"a\\\"b\"", MobileJson.quote("a\"b"))
        assertEquals("\"tab\\there\"", MobileJson.quote("tab\there"))
        assertEquals("\"\\u0001\"", MobileJson.quote("\u0001"))
    }

    @Test
    fun buildsFlatObjectsWithoutTrailingCommas() {
        val json = MobileJsonObject()
            .string("id", "qing.qr-code")
            .int("apiVersion", 1)
            .bool("ok", false)
            .strings("capabilities", listOf("graphics.qr"))
            .objectValue("nested", MobileJsonObject().string("k", "v").build())
            .build()

        assertEquals(
            """{"id":"qing.qr-code","apiVersion":1,"ok":false,"capabilities":["graphics.qr"],"nested":{"k":"v"}}""",
            json,
        )
    }

    @Test
    fun roundTripsThroughTheWriter() {
        val text = "a\"b\\c\nd"
        val list = listOf("x", "y")
        val json = MobileJsonObject()
            .string("text", text)
            .strings("list", list)
            .build()

        val parsed = MobileJson.parse(json)
        assertEquals(text, parsed.stringField("text"))
        assertEquals(list, parsed.stringArrayField("list"))
    }
}
