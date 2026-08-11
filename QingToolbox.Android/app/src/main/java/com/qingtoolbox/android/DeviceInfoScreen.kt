package com.qingtoolbox.android

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Build
import androidx.compose.material.icons.outlined.Code
import androidx.compose.material.icons.outlined.ContentCopy
import androidx.compose.material.icons.outlined.DevicesOther
import androidx.compose.material.icons.outlined.DisplaySettings
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch

@Composable
fun DeviceInfoScreen(modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val snapshot = remember(context) { DeviceInfoProvider.read(context) }
    val scope = rememberCoroutineScope()
    val snackbarHostState = remember { SnackbarHostState() }
    val unavailable = stringResource(R.string.unavailable)
    val summaryLabels = DeviceInfoSummaryLabels(
        device = stringResource(R.string.device_info_section_device),
        manufacturer = stringResource(R.string.device_info_manufacturer),
        brand = stringResource(R.string.device_info_brand),
        model = stringResource(R.string.device_info_model),
        deviceCodename = stringResource(R.string.device_info_device),
        product = stringResource(R.string.device_info_product),
        android = stringResource(R.string.device_info_section_android),
        version = stringResource(R.string.device_info_version),
        buildId = stringResource(R.string.device_info_build_id),
        securityPatch = stringResource(R.string.device_info_security_patch),
        hardware = stringResource(R.string.device_info_section_hardware),
        supportedAbis = stringResource(R.string.device_info_supported_abis),
        availableProcessors = stringResource(R.string.device_info_available_processors),
        display = stringResource(R.string.device_info_section_display),
        size = stringResource(R.string.device_info_screen_size),
        density = stringResource(R.string.device_info_density),
        app = stringResource(R.string.device_info_section_app),
        qingToolbox = stringResource(R.string.device_info_qingtoolbox),
        packageName = stringResource(R.string.device_info_package),
        unavailable = unavailable,
    )

    fun copyValue(label: String, value: String) {
        val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
        clipboard?.setPrimaryClip(ClipData.newPlainText(label, value))
        scope.launch { snackbarHostState.showSnackbar(context.getString(R.string.copied_value, label)) }
    }

    Box(modifier = modifier.fillMaxSize()) {
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(horizontal = 20.dp, vertical = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            item {
                DeviceInfoHero(
                    onCopySummary = {
                        copyValue(
                            context.getString(R.string.device_info_summary),
                            DeviceInfoFormat.summary(snapshot, summaryLabels),
                        )
                    },
                )
            }
            item {
                DeviceInfoGroup(
                    title = stringResource(R.string.device_info_section_device),
                    icon = Icons.Outlined.DevicesOther,
                ) {
                    DeviceInfoRow(stringResource(R.string.device_info_manufacturer), snapshot.manufacturer)
                    DeviceInfoRow(stringResource(R.string.device_info_brand), snapshot.brand)
                    DeviceInfoRow(stringResource(R.string.device_info_model), snapshot.model) {
                        copyValue(context.getString(R.string.device_info_model), snapshot.model)
                    }
                    DeviceInfoRow(stringResource(R.string.device_info_device), snapshot.device)
                    DeviceInfoRow(stringResource(R.string.device_info_product), snapshot.product)
                }
            }
            item {
                DeviceInfoGroup(
                    title = stringResource(R.string.device_info_section_android),
                    icon = Icons.Outlined.Info,
                ) {
                    DeviceInfoRow(
                        label = stringResource(R.string.device_info_android_version_api),
                        value = DeviceInfoFormat.androidVersion(snapshot.androidVersion, snapshot.apiLevel, unavailable),
                    ) {
                        copyValue(
                            context.getString(R.string.device_info_android_version),
                            DeviceInfoFormat.androidVersion(snapshot.androidVersion, snapshot.apiLevel, unavailable),
                        )
                    }
                    DeviceInfoRow(stringResource(R.string.device_info_build_id), snapshot.buildId) {
                        copyValue(context.getString(R.string.device_info_build_id), snapshot.buildId)
                    }
                    snapshot.securityPatch?.let { patch ->
                        DeviceInfoRow(stringResource(R.string.device_info_security_patch), patch)
                    }
                }
            }
            item {
                DeviceInfoGroup(
                    title = stringResource(R.string.device_info_section_hardware),
                    icon = Icons.Outlined.Build,
                ) {
                    val abiText = DeviceInfoFormat.abiList(snapshot.supportedAbis, unavailable)
                    DeviceInfoRow(stringResource(R.string.device_info_supported_abis), abiText) {
                        copyValue(context.getString(R.string.device_info_supported_abis), abiText)
                    }
                    DeviceInfoRow(
                        stringResource(R.string.device_info_available_processors),
                        snapshot.availableProcessors.toString(),
                    )
                }
            }
            item {
                DeviceInfoGroup(
                    title = stringResource(R.string.device_info_section_display),
                    icon = Icons.Outlined.DisplaySettings,
                ) {
                    DeviceInfoRow(
                        stringResource(R.string.device_info_screen_size),
                        DeviceInfoFormat.displaySize(snapshot.displayWidthPixels, snapshot.displayHeightPixels),
                    )
                    DeviceInfoRow(stringResource(R.string.device_info_density), DeviceInfoFormat.density(snapshot.displayDensity))
                    DeviceInfoRow(stringResource(R.string.device_info_density_dpi), DeviceInfoFormat.densityDpi(snapshot.displayDensityDpi))
                }
            }
            item {
                DeviceInfoGroup(
                    title = stringResource(R.string.device_info_section_app),
                    icon = Icons.Outlined.Code,
                ) {
                    val versionText = DeviceInfoFormat.appVersion(snapshot.appVersionName, snapshot.appVersionCode, unavailable)
                    DeviceInfoRow(stringResource(R.string.device_info_qingtoolbox), versionText) {
                        copyValue(context.getString(R.string.device_info_app_version), versionText)
                    }
                    DeviceInfoRow(stringResource(R.string.device_info_package), snapshot.appPackageName)
                }
            }
            item {
                QingStatusText(
                    text = stringResource(R.string.device_info_status),
                    modifier = Modifier.padding(top = 4.dp, bottom = 20.dp),
                )
            }
        }
        SnackbarHost(
            hostState = snackbarHostState,
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .padding(16.dp),
        )
    }
}

@Composable
private fun DeviceInfoHero(onCopySummary: () -> Unit) {
    QingCard(modifier = Modifier.fillMaxWidth()) {
        Column(
            modifier = Modifier.padding(18.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                QingIconSurface(modifier = Modifier.size(52.dp)) {
                    Icon(
                        imageVector = Icons.Outlined.DevicesOther,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.primary,
                    )
                }
                Column {
                    Text(
                        text = stringResource(R.string.device_info_title),
                        style = MaterialTheme.typography.titleLarge,
                        fontWeight = FontWeight.SemiBold,
                    )
                    Text(
                        text = stringResource(R.string.device_info_body),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            QingSecondaryButton(
                onClick = onCopySummary,
                modifier = Modifier.fillMaxWidth(),
            ) {
                Icon(Icons.Outlined.ContentCopy, contentDescription = null)
                Spacer(Modifier.size(8.dp))
                Text(stringResource(R.string.device_info_copy_summary))
            }
        }
    }
}

@Composable
private fun DeviceInfoGroup(
    title: String,
    icon: ImageVector,
    content: @Composable ColumnScope.() -> Unit,
) {
    QingCard(modifier = Modifier.fillMaxWidth()) {
        Column {
            Row(
                modifier = Modifier.padding(horizontal = 18.dp, vertical = 14.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                QingIconSurface(modifier = Modifier.size(36.dp)) {
                    Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                }
                Text(
                    text = title,
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.SemiBold,
                )
            }
            content()
        }
    }
}

@Composable
private fun ColumnScope.DeviceInfoRow(
    label: String,
    value: String,
    onCopy: (() -> Unit)? = null,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(start = 18.dp, end = 8.dp, top = 8.dp, bottom = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = label,
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            QingStatusText(text = value)
        }
        if (onCopy != null) {
            IconButton(onClick = onCopy) {
                Icon(
                    imageVector = Icons.Outlined.ContentCopy,
                    contentDescription = stringResource(R.string.copy_value_content_description, label),
                    tint = MaterialTheme.colorScheme.primary,
                )
            }
        }
    }
}
