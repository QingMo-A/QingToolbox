package com.qingtoolbox.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class DeviceInfoTest {
    private val unavailable = "Unavailable"
    private val labels = DeviceInfoSummaryLabels(
        device = "Device",
        manufacturer = "Manufacturer",
        brand = "Brand",
        model = "Model",
        deviceCodename = "Device",
        product = "Product",
        android = "Android",
        version = "Version",
        buildId = "Build ID",
        securityPatch = "Security patch",
        hardware = "Hardware",
        supportedAbis = "Supported ABIs",
        availableProcessors = "Available processors",
        display = "Display",
        size = "Screen size",
        density = "Density",
        app = "App",
        qingToolbox = "QingToolbox",
        packageName = "Package",
        unavailable = unavailable,
    )

    @Test
    fun formatsSupportedAbisForDisplayAndCopy() {
        assertEquals("arm64-v8a, armeabi-v7a", DeviceInfoFormat.abiList(listOf("arm64-v8a", "  ", "armeabi-v7a"), unavailable))
        assertEquals(unavailable, DeviceInfoFormat.abiList(emptyList(), unavailable))
    }

    @Test
    fun formatsAndroidReleaseAndApiTogether() {
        assertEquals("Android 14 (API 34)", DeviceInfoFormat.androidVersion("14", 34, unavailable))
        assertEquals("Android Unavailable (API 35)", DeviceInfoFormat.androidVersion("", 35, unavailable))
    }

    @Test
    fun formatsSecurityPatchFallbackWhenMissing() {
        assertEquals("2024-10-05", DeviceInfoFormat.securityPatch("2024-10-05", unavailable))
        assertEquals(unavailable, DeviceInfoFormat.securityPatch(null, unavailable))
        assertEquals(unavailable, DeviceInfoFormat.securityPatch("  ", unavailable))
    }

    @Test
    fun summaryContainsSafeDeviceAndAppFields() {
        val snapshot = DeviceInfoSnapshot(
            manufacturer = "Qing",
            brand = "Qing",
            model = "Qing One",
            device = "qing_one",
            product = "qing_product",
            androidVersion = "14",
            apiLevel = 34,
            buildId = "UP1A.231005.007",
            securityPatch = null,
            supportedAbis = listOf("arm64-v8a"),
            availableProcessors = 8,
            displayWidthPixels = 1080,
            displayHeightPixels = 2400,
            displayDensity = 3f,
            displayDensityDpi = 480,
            appVersionName = "0.1.0-alpha",
            appVersionCode = 1,
            appPackageName = "com.qingtoolbox.android",
        )

        val summary = DeviceInfoFormat.summary(snapshot, labels)
        assertTrue(summary.contains("Model: Qing One"))
        assertTrue(summary.contains("Android 14 (API 34)"))
        assertTrue(summary.contains("Build ID: UP1A.231005.007"))
        assertTrue(summary.contains("Supported ABIs: arm64-v8a"))
        assertTrue(summary.contains("QingToolbox: 0.1.0-alpha (code 1)"))
        assertTrue(!summary.contains("Security patch:"))
    }
}
