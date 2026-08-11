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
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.launch

@Composable
fun DeviceInfoScreen(modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val snapshot = remember(context) { DeviceInfoProvider.read(context) }
    val scope = rememberCoroutineScope()
    val snackbarHostState = remember { SnackbarHostState() }

    fun copyValue(label: String, value: String) {
        val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
        clipboard?.setPrimaryClip(ClipData.newPlainText(label, value))
        scope.launch { snackbarHostState.showSnackbar("Copied $label") }
    }

    Box(modifier = modifier.fillMaxSize()) {
        LazyColumn(
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(horizontal = 20.dp, vertical = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            item {
                DeviceInfoHero(
                    onCopySummary = { copyValue("summary", DeviceInfoFormat.summary(snapshot)) },
                )
            }
            item {
                DeviceInfoGroup(
                    title = "Device",
                    icon = Icons.Outlined.DevicesOther,
                ) {
                    DeviceInfoRow("Manufacturer", snapshot.manufacturer)
                    DeviceInfoRow("Brand", snapshot.brand)
                    DeviceInfoRow("Model", snapshot.model) { copyValue("Model", snapshot.model) }
                    DeviceInfoRow("Device", snapshot.device)
                    DeviceInfoRow("Product", snapshot.product)
                }
            }
            item {
                DeviceInfoGroup(
                    title = "Android",
                    icon = Icons.Outlined.Info,
                ) {
                    DeviceInfoRow(
                        label = "Android version / API",
                        value = DeviceInfoFormat.androidVersion(snapshot.androidVersion, snapshot.apiLevel),
                    ) {
                        copyValue("Android version", DeviceInfoFormat.androidVersion(snapshot.androidVersion, snapshot.apiLevel))
                    }
                    DeviceInfoRow("Build ID", snapshot.buildId) { copyValue("Build ID", snapshot.buildId) }
                    snapshot.securityPatch?.let { patch ->
                        DeviceInfoRow("Security patch", patch)
                    }
                }
            }
            item {
                DeviceInfoGroup(
                    title = "Hardware",
                    icon = Icons.Outlined.Build,
                ) {
                    val abiText = DeviceInfoFormat.abiList(snapshot.supportedAbis)
                    DeviceInfoRow("Supported ABIs", abiText) { copyValue("Supported ABIs", abiText) }
                    DeviceInfoRow("Available processors", snapshot.availableProcessors.toString())
                }
            }
            item {
                DeviceInfoGroup(
                    title = "Display",
                    icon = Icons.Outlined.DisplaySettings,
                ) {
                    DeviceInfoRow(
                        "Screen size",
                        DeviceInfoFormat.displaySize(snapshot.displayWidthPixels, snapshot.displayHeightPixels),
                    )
                    DeviceInfoRow("Density", DeviceInfoFormat.density(snapshot.displayDensity))
                    DeviceInfoRow("Density DPI", DeviceInfoFormat.densityDpi(snapshot.displayDensityDpi))
                }
            }
            item {
                DeviceInfoGroup(
                    title = "App",
                    icon = Icons.Outlined.Code,
                ) {
                    val versionText = DeviceInfoFormat.appVersion(snapshot.appVersionName, snapshot.appVersionCode)
                    DeviceInfoRow("QingToolbox", versionText) { copyValue("App version", versionText) }
                    DeviceInfoRow("Package", snapshot.appPackageName)
                }
            }
            item {
                QingStatusText(
                    text = "Reads public device and app properties only. Nothing is stored or uploaded.",
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
                        text = "Device Info",
                        style = MaterialTheme.typography.titleLarge,
                        fontWeight = FontWeight.SemiBold,
                    )
                    Text(
                        text = "A quick, permission-free view of this device.",
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
                Text("Copy summary")
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
                    contentDescription = "Copy $label",
                    tint = MaterialTheme.colorScheme.primary,
                )
            }
        }
    }
}
