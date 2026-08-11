package com.qingtoolbox.android

import android.content.Context
import android.os.Build

data class DeviceInfoSnapshot(
    val manufacturer: String,
    val brand: String,
    val model: String,
    val device: String,
    val product: String,
    val androidVersion: String,
    val apiLevel: Int,
    val buildId: String,
    val securityPatch: String?,
    val supportedAbis: List<String>,
    val availableProcessors: Int,
    val displayWidthPixels: Int,
    val displayHeightPixels: Int,
    val displayDensity: Float,
    val displayDensityDpi: Int,
    val appVersionName: String,
    val appVersionCode: Long,
    val appPackageName: String,
)

object DeviceInfoProvider {
    fun read(context: Context): DeviceInfoSnapshot {
        val applicationContext = context.applicationContext
        val displayMetrics = applicationContext.resources.displayMetrics
        val packageInfo = applicationContext.packageManager.getPackageInfo(applicationContext.packageName, 0)
        return DeviceInfoSnapshot(
            manufacturer = Build.MANUFACTURER.valueOrUnavailable(),
            brand = Build.BRAND.valueOrUnavailable(),
            model = Build.MODEL.valueOrUnavailable(),
            device = Build.DEVICE.valueOrUnavailable(),
            product = Build.PRODUCT.valueOrUnavailable(),
            androidVersion = Build.VERSION.RELEASE.valueOrUnavailable(),
            apiLevel = Build.VERSION.SDK_INT,
            buildId = Build.ID.valueOrUnavailable(),
            securityPatch = Build.VERSION.SECURITY_PATCH.takeIf { it.isNotBlank() },
            supportedAbis = Build.SUPPORTED_ABIS.toList().ifEmpty { listOf("Unavailable") },
            availableProcessors = Runtime.getRuntime().availableProcessors(),
            displayWidthPixels = displayMetrics.widthPixels,
            displayHeightPixels = displayMetrics.heightPixels,
            displayDensity = displayMetrics.density,
            displayDensityDpi = displayMetrics.densityDpi,
            appVersionName = packageInfo.versionName.valueOrUnavailable(),
            appVersionCode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                packageInfo.longVersionCode
            } else {
                @Suppress("DEPRECATION")
                packageInfo.versionCode.toLong()
            },
            appPackageName = applicationContext.packageName,
        )
    }

    private fun String?.valueOrUnavailable(): String = this?.takeIf { it.isNotBlank() } ?: "Unavailable"
}

object DeviceInfoFormat {
    fun abiList(abis: List<String>): String =
        abis.map { it.trim() }.filter { it.isNotEmpty() }.ifEmpty { listOf("Unavailable") }.joinToString(", ")

    fun androidVersion(release: String?, apiLevel: Int): String =
        "Android ${release.orUnavailable()} (API $apiLevel)"

    fun securityPatch(patch: String?): String = patch.orUnavailable()

    fun displaySize(widthPixels: Int, heightPixels: Int): String =
        "$widthPixels × $heightPixels px"

    fun density(density: Float): String = "%.2fx".format(java.util.Locale.US, density)

    fun densityDpi(densityDpi: Int): String = "$densityDpi dpi"

    fun appVersion(versionName: String?, versionCode: Long): String =
        "${versionName.orUnavailable()} (code $versionCode)"

    fun summary(snapshot: DeviceInfoSnapshot): String = buildString {
        appendLine("Device")
        appendLine("Manufacturer: ${snapshot.manufacturer}")
        appendLine("Brand: ${snapshot.brand}")
        appendLine("Model: ${snapshot.model}")
        appendLine("Device: ${snapshot.device}")
        appendLine("Product: ${snapshot.product}")
        appendLine()
        appendLine("Android")
        appendLine("Version: ${androidVersion(snapshot.androidVersion, snapshot.apiLevel)}")
        appendLine("Build ID: ${snapshot.buildId}")
        snapshot.securityPatch?.takeIf { it.isNotBlank() }?.let { appendLine("Security patch: $it") }
        appendLine()
        appendLine("Hardware")
        appendLine("Supported ABIs: ${abiList(snapshot.supportedAbis)}")
        appendLine("Available processors: ${snapshot.availableProcessors}")
        appendLine()
        appendLine("Display")
        appendLine("Size: ${displaySize(snapshot.displayWidthPixels, snapshot.displayHeightPixels)}")
        appendLine("Density: ${density(snapshot.displayDensity)} (${densityDpi(snapshot.displayDensityDpi)})")
        appendLine()
        appendLine("App")
        appendLine("QingToolbox: ${appVersion(snapshot.appVersionName, snapshot.appVersionCode)}")
        append("Package: ${snapshot.appPackageName}")
    }

    private fun String?.orUnavailable(): String = this?.takeIf { it.isNotBlank() } ?: "Unavailable"
}
