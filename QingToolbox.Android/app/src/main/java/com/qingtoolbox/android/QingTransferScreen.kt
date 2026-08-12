package com.qingtoolbox.android

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.DevicesOther
import androidx.compose.material.icons.outlined.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import android.provider.OpenableColumns
import android.content.res.AssetFileDescriptor
import kotlinx.coroutines.delay

internal const val QING_TRANSFER_PICKER_LEASE_MS = 120_000L
internal fun shouldKeepTransferSessionForPicker(pickerActive: Boolean): Boolean = pickerActive

internal fun shouldShowIncomingDialog(
    state: QingTransferConnectionState,
    peer: QingTransferPeer?,
): Boolean = state == QingTransferConnectionState.WAITING_APPROVAL && peer != null

@Composable
fun QingTransferDevicesScreen(modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    var peers by remember { mutableStateOf(emptyList<QingTransferPeer>()) }
    var state by remember { mutableStateOf(QingTransferDiscoveryState.IDLE) }
    val discovery = remember(context) {
        QingTransferDiscovery(context, { peers = it }, { state = it })
    }
    val connection = remember(discovery) { QingTransferConnection(context, discovery, QingTransferMetadata.sanitizeName(android.os.Build.MODEL)) }
    val connectionState by connection.state.collectAsStateWithLifecycle()
    val incomingPeer by connection.incomingPeer.collectAsStateWithLifecycle()
    val incomingOffer by connection.incomingOffer.collectAsStateWithLifecycle()
    val transferProgress by connection.progress.collectAsStateWithLifecycle()
    val connectionError by connection.error.collectAsStateWithLifecycle()
    var saveOffer by remember { mutableStateOf<QingTransferFileOffer?>(null) }
    var pickerActive by remember { mutableStateOf(false) }
    val sendLauncher = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        pickerActive = false
        if (uri != null) {
            val details = queryTransferFile(context, uri)
            if (details == null || details.second < 0L) connection.reportTransferFailure()
            else connection.sendFile(uri, details.first, details.second)
        }
    }
    val saveLauncher = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { uri ->
        pickerActive = false
        val offer = saveOffer
        saveOffer = null
        if (offer != null && uri != null) connection.acceptIncoming(uri) else if (offer != null) connection.rejectIncomingFile()
    }

    LaunchedEffect(pickerActive) {
        if (pickerActive) {
            delay(QING_TRANSFER_PICKER_LEASE_MS)
            if (pickerActive) {
                pickerActive = false
                connection.pickerTimedOut()
                discovery.stop()
            }
        }
    }

    DisposableEffect(lifecycleOwner, discovery, connection) {
        val observer = LifecycleEventObserver { _, event ->
            when (event) {
                Lifecycle.Event.ON_START -> discovery.start()
                Lifecycle.Event.ON_STOP -> {
                    if (!shouldKeepTransferSessionForPicker(pickerActive)) {
                        connection.disconnect()
                        discovery.stop()
                    }
                }
                else -> Unit
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        if (lifecycleOwner.lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED)) discovery.start()
        onDispose {
            lifecycleOwner.lifecycle.removeObserver(observer)
            connection.dispose()
            discovery.stop()
        }
    }

    if (shouldShowIncomingDialog(connectionState, incomingPeer)) incomingPeer?.let { peer ->
        AlertDialog(
            onDismissRequest = { connection.rejectIncoming() },
            title = { Text(stringResource(R.string.qing_transfer_incoming_title)) },
            text = { Text(stringResource(R.string.qing_transfer_incoming_body, peer.displayName)) },
            confirmButton = { TextButton(onClick = { connection.acceptIncoming() }) { Text(stringResource(R.string.qing_transfer_accept)) } },
            dismissButton = { TextButton(onClick = { connection.rejectIncoming() }) { Text(stringResource(R.string.qing_transfer_reject)) } },
        )
    }
    incomingOffer?.let { offer ->
        AlertDialog(
            onDismissRequest = { connection.rejectIncomingFile() },
            title = { Text(stringResource(R.string.qing_transfer_incoming_file_title)) },
            text = { Text(stringResource(R.string.qing_transfer_incoming_file_body, incomingPeer?.displayName ?: "QingToolbox", offer.name, offer.size)) },
            confirmButton = { TextButton(onClick = {
                saveOffer = offer
                pickerActive = true
                runCatching { saveLauncher.launch(offer.name) }.onFailure {
                    pickerActive = false
                    saveOffer = null
                    connection.rejectIncomingFile()
                }
            }) { Text(stringResource(R.string.qing_transfer_accept)) } },
            dismissButton = { TextButton(onClick = { connection.rejectIncomingFile() }) { Text(stringResource(R.string.qing_transfer_reject)) } },
        )
    }
    connectionError?.let { error ->
        AlertDialog(
            onDismissRequest = { connection.clearError() },
            title = { Text(stringResource(R.string.qing_transfer_connection_failed)) },
            text = { Text(stringResource(error.messageRes())) },
            confirmButton = { TextButton(onClick = { connection.clearError() }) { Text(stringResource(R.string.ok)) } },
        )
    }

    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item {
            QingCard(modifier = Modifier.fillMaxWidth()) {
                Column(modifier = Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        QingIconSurface(modifier = Modifier.size(52.dp)) {
                            Icon(Icons.Outlined.DevicesOther, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                        }
                        Column(modifier = Modifier.weight(1f)) {
                            Text(stringResource(R.string.qing_transfer_title), style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
                            Text(stringResource(R.string.qing_transfer_body), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                    QingSecondaryButton(
                        onClick = {
                            // A restart is a full foreground-session reset: no stale
                            // connection may outlive the advertised listener.
                            connection.disconnect()
                            discovery.restart()
                        },
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Icon(Icons.Outlined.Refresh, contentDescription = null)
                        Spacer(Modifier.size(8.dp))
                        Text(stringResource(R.string.qing_transfer_refresh))
                    }
                    if (connectionState != QingTransferConnectionState.IDLE) {
                        QingStatusText(
                            text = when (connectionState) {
                                QingTransferConnectionState.CONNECTING -> stringResource(R.string.qing_transfer_connecting)
                                QingTransferConnectionState.WAITING_APPROVAL -> stringResource(R.string.qing_transfer_waiting_approval)
                                QingTransferConnectionState.CONNECTED -> stringResource(R.string.qing_transfer_connected)
                                QingTransferConnectionState.IDLE -> ""
                            },
                        )
                        if (connectionState == QingTransferConnectionState.CONNECTED) {
                            QingPrimaryButton(onClick = {
                                pickerActive = true
                                runCatching { sendLauncher.launch(arrayOf("*/*")) }.onFailure {
                                    pickerActive = false
                                    connection.reportTransferFailure()
                                }
                            }, modifier = Modifier.fillMaxWidth()) {
                                Text(stringResource(R.string.qing_transfer_send_file))
                            }
                            transferProgress?.let { progress ->
                                Text(stringResource(R.string.qing_transfer_transfer_progress, progress.name, progress.completed, progress.total))
                                LinearProgressIndicator(
                                    progress = { if (progress.total > 0) progress.completed.toFloat() / progress.total.toFloat() else 0f },
                                    modifier = Modifier.fillMaxWidth(),
                                )
                                QingSecondaryButton(onClick = connection::cancelTransfer, modifier = Modifier.fillMaxWidth()) {
                                    Text(stringResource(R.string.qing_transfer_cancel_transfer))
                                }
                            }
                            QingSecondaryButton(onClick = connection::disconnect, modifier = Modifier.fillMaxWidth()) {
                                Text(stringResource(R.string.qing_transfer_disconnect))
                            }
                        }
                    }
                }
            }
        }
        if (peers.isEmpty()) {
            item {
                QingEmptyState(
                    modifier = Modifier.fillMaxWidth(),
                    icon = { Icon(Icons.Outlined.DevicesOther, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
                    title = stringResource(R.string.qing_transfer_empty),
                    body = stringResource(R.string.qing_transfer_hint),
                )
            }
        } else {
            items(peers, key = { it.serviceName }) { peer -> QingTransferPeerCard(peer, connectionState, connection::connect) }
        }
        item {
            QingStatusText(
                text = when (state) {
                    QingTransferDiscoveryState.SEARCHING -> stringResource(R.string.qing_transfer_searching)
                    QingTransferDiscoveryState.ERROR -> stringResource(R.string.qing_transfer_error)
                    else -> stringResource(R.string.qing_transfer_hint)
                },
                modifier = Modifier.padding(top = 2.dp, bottom = 20.dp),
            )
        }
    }
}

private fun queryTransferFile(context: android.content.Context, uri: android.net.Uri): Pair<String, Long>? {
    val values = context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use { cursor ->
        if (cursor.moveToFirst()) {
            val name = cursor.getString(0)?.takeIf { it.isNotBlank() } ?: return@use null
            val size = if (cursor.isNull(1)) -1L else cursor.getLong(1)
            name to size
        } else null
    } ?: return null
    if (values.second >= 0L) return values
    val descriptorLength = runCatching {
        context.contentResolver.openAssetFileDescriptor(uri, "r")?.use(AssetFileDescriptor::getLength) ?: -1L
    }.getOrDefault(-1L)
    return values.first to descriptorLength
}

@Composable
private fun QingTransferPeerCard(
    peer: QingTransferPeer,
    connectionState: QingTransferConnectionState,
    onConnect: (QingTransferPeer) -> Unit,
) {
    QingCard(modifier = Modifier.fillMaxWidth()) {
        Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                QingIconSurface(modifier = Modifier.size(44.dp)) {
                    Icon(Icons.Outlined.DevicesOther, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                }
                Column(modifier = Modifier.weight(1f)) {
                    Text(peer.displayName, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                    Text(
                        if (peer.platform.equals("android", ignoreCase = true)) stringResource(R.string.qing_transfer_android) else stringResource(R.string.qing_transfer_windows),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    if (peer.addresses.isNotEmpty() && peer.port > 0) QingStatusText(text = peer.addresses.joinToString(", ") + ":${peer.port}")
                }
                Text(
                    if (peer.online) stringResource(R.string.qing_transfer_online) else stringResource(R.string.qing_transfer_offline),
                    style = MaterialTheme.typography.labelMedium,
                    color = if (peer.online) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            QingPrimaryButton(
                onClick = { onConnect(peer) },
                modifier = Modifier.fillMaxWidth(),
                enabled = connectionState == QingTransferConnectionState.IDLE && peer.online && peer.port > 0,
            ) { Text(stringResource(R.string.qing_transfer_connect)) }
        }
    }
}
