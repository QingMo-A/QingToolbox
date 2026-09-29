package com.qingtoolbox.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class DeviceNotificationPayloadTest {
    @Test fun chineseAndEscapedTextStayInsideEncryptedFrame() {
        val payload = notificationManagementPayload("微信", "验证码", "中\"文\\消息".repeat(120))
        assertTrue(payload.size <= 1008)
        val json = MobileJson.parse(String(payload, Charsets.UTF_8))
        assertEquals(1, json.intField("version"))
        assertEquals("notification", json.stringField("action"))
        assertEquals("微信", json.stringField("appName"))
        assertEquals("验证码", json.stringField("title"))
        assertTrue((json.stringField("body") ?: "").isNotEmpty())
    }
}
