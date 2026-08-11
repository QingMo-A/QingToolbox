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
        val unavailable = applicationContext.getString(R.string.unavailable)
        val displayMetrics = applicationContext.resources.displayMetrics
        val packageInfo = applicationContext.packageManager.getPackageInfo(applicationContext.packageName, 0)
        return DeviceInfoSnapshot(
            manufacturer = Build.MANUFACTURER.valueOrUnavailable(unavailable),
            brand = Build.BRAND.valueOrUnavailable(unavailable),
            model = Build.MODEL.valueOrUnavailable(unavailable),
            device = Build.DEVICE.valueOrUnavailable(unavailable),
            product = Build.PRODUCT.valueOrUnavailable(unavailable),
            androidVersion = Build.VERSION.RELEASE.valueOrUnavailable(unavailable),
            apiLevel = Build.VERSION.SDK_INT,
            buildId = Build.ID.valueOrUnavailable(unavailable),
            securityPatch = Build.VERSION.SECURITY_PATCH.takeIf { it.isNotBlank() },
            supportedAbis = Build.SUPPORTED_ABIS.toList().ifEmpty { listOf(unavailable) },
            availableProcessors = Runtime.getRuntime().availableProcessors(),
            displayWidthPixels = displayMetrics.widthPixels,
            displayHeightPixels = displayMetrics.heightPixels,
            displayDensity = displayMetrics.density,
            displayDensityDpi = displayMetrics.densityDpi,
            appVersionName = packageInfo.versionName.valueOrUnavailable(unavailable),
            appVersionCode = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                packageInfo.longVersionCode
            } else {
                @Suppress("DEPRECATION")
                packageInfo.versionCode.toLong()
            },
            appPackageName = applicationContext.packageName,
        )
    }

    private fun String?.valueOrUnavailable(unavailable: String): String =
        this?.takeIf { it.isNotBlank() } ?: unavailable
}

data class DeviceInfoSummaryLabels(
    val device: String,
    val manufacturer: String,
    val brand: String,
    val model: String,
    val deviceCodename: String,
    val product: String,
    val android: String,
    val version: String,
    val buildId: String,
    val securityPatch: String,
    val hardware: String,
    val supportedAbis: String,
    val availableProcessors: String,
    val display: String,
    val size: String,
    val density: String,
    val app: String,
    val qingToolbox: String,
    val packageName: String,
    val unavailable: String,
)

object DeviceInfoFormat {
    fun abiList(abis: List<String>, unavailable: String): String =
        abis.map { it.trim() }.filter { it.isNotEmpty() }.ifEmpty { listOf(unavailable) }.joinToString(", ")

    fun androidVersion(release: String?, apiLevel: Int, unavailable: String): String =
        "Android ${release.orUnavailable(unavailable)} (API $apiLevel)"

    fun securityPatch(patch: String?, unavailable: String): String = patch.orUnavailable(unavailable)

    fun displaySize(widthPixels: Int, heightPixels: Int): String =
        "$widthPixels × $heightPixels px"

    fun density(density: Float): String = "%.2fx".format(java.util.Locale.US, density)

    fun densityDpi(densityDpi: Int): String = "$densityDpi dpi"

    fun appVersion(versionName: String?, versionCode: Long, unavailable: String): String =
        "${versionName.orUnavailable(unavailable)} (code $versionCode)"

    fun summary(snapshot: DeviceInfoSnapshot, labels: DeviceInfoSummaryLabels): String = buildString {
        appendLine(labels.device)
        appendLine("${labels.manufacturer}: ${snapshot.manufacturer}")
        appendLine("${labels.brand}: ${snapshot.brand}")
        appendLine("${labels.model}: ${snapshot.model}")
        appendLine("${labels.deviceCodename}: ${snapshot.device}")
        appendLine("${labels.product}: ${snapshot.product}")
        appendLine()
        appendLine(labels.android)
        appendLine("${labels.version}: ${androidVersion(snapshot.androidVersion, snapshot.apiLevel, labels.unavailable)}")
        appendLine("${labels.buildId}: ${snapshot.buildId}")
        snapshot.securityPatch?.takeIf { it.isNotBlank() }?.let { appendLine("${labels.securityPatch}: $it") }
        appendLine()
        appendLine(labels.hardware)
        appendLine("${labels.supportedAbis}: ${abiList(snapshot.supportedAbis, labels.unavailable)}")
        appendLine("${labels.availableProcessors}: ${snapshot.availableProcessors}")
        appendLine()
        appendLine(labels.display)
        appendLine("${labels.size}: ${displaySize(snapshot.displayWidthPixels, snapshot.displayHeightPixels)}")
        appendLine("${labels.density}: ${density(snapshot.displayDensity)} (${densityDpi(snapshot.displayDensityDpi)})")
        appendLine()
        appendLine(labels.app)
        appendLine("${labels.qingToolbox}: ${appVersion(snapshot.appVersionName, snapshot.appVersionCode, labels.unavailable)}")
        append("${labels.packageName}: ${snapshot.appPackageName}")
    }

    private fun String?.orUnavailable(unavailable: String): String = this?.takeIf { it.isNotBlank() } ?: unavailable
}
