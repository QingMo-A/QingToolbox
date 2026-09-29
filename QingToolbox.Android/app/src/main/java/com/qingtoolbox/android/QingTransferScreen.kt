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
import androidx.compose.material.icons.Icons
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import android.provider.OpenableColumns
import android.content.res.AssetFileDescriptor
import android.widget.Toast
internal fun shouldKeepTransferSession(state: QingTransferConnectionState): Boolean =
    state == QingTransferConnectionState.CONNECTED

internal fun shouldShowIncomingDialog(
    state: QingTransferConnectionState,
    peer: QingTransferPeer?,
): Boolean = state == QingTransferConnectionState.WAITING_APPROVAL && peer != null

internal data class QingTransferSelectedFile(
    val uri: android.net.Uri,
    val name: String,
    val size: Long,
)

@Composable
internal fun QingTransferDevicesScreen(
    modifier: Modifier = Modifier,
    targetDeviceId: String,
    targetAddress: String?,
    initialFile: QingTransferSelectedFile? = null,
    showContent: Boolean = true,
    onClose: () -> Unit = {},
) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val session = remember(context) { QingTransferProcessSessionStore.get(context) }
    val discovery = session.discovery
    val connection = session.connection
    val peers by session.peers.collectAsStateWithLifecycle()
    val targetPeers = peers.filter { QingTransferTarget.matches(it, targetDeviceId, targetAddress) }
    val state by session.discoveryState.collectAsStateWithLifecycle()
    val connectionState by connection.state.collectAsStateWithLifecycle()
    val incomingPeer by connection.incomingPeer.collectAsStateWithLifecycle()
    val incomingOffer by connection.incomingOffer.collectAsStateWithLifecycle()
    val transferProgress by connection.progress.collectAsStateWithLifecycle()
    val lastCompleted by connection.lastCompleted.collectAsStateWithLifecycle()
    val connectionError by connection.error.collectAsStateWithLifecycle()
    var pendingFile by remember(targetDeviceId, initialFile) { mutableStateOf(initialFile) }
    var saveOffer by remember { mutableStateOf<QingTransferFileOffer?>(null) }
    val sendLauncher = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) {
            val details = queryTransferFile(context, uri)
            if (details == null || details.second < 0L) connection.reportTransferFailure()
            else {
                connection.clearCompletion()
                pendingFile = QingTransferSelectedFile(uri, details.first, details.second)
            }
        }
    }
    val saveLauncher = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { uri ->
        val offer = saveOffer
        saveOffer = null
        if (offer != null && uri != null) connection.acceptIncoming(uri) else if (offer != null) connection.rejectIncomingFile()
    }

    DisposableEffect(lifecycleOwner, discovery, connection) {
        val observer = LifecycleEventObserver { _, event ->
            when (event) {
                Lifecycle.Event.ON_START -> discovery.acquire("transfer-screen")
                Lifecycle.Event.ON_STOP -> {
                    if (!shouldKeepTransferSession(connection.state.value)) {
                        connection.disconnect()
                    }
                    discovery.release("transfer-screen")
                }
                else -> Unit
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        if (lifecycleOwner.lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED)) discovery.acquire("transfer-screen")
        onDispose {
            lifecycleOwner.lifecycle.removeObserver(observer)
            if (!shouldKeepTransferSession(connection.state.value)) connection.disconnect()
            discovery.release("transfer-screen")
        }
    }

    LaunchedEffect(targetPeers, pendingFile, connectionState) {
        val file = pendingFile ?: return@LaunchedEffect
        when (connectionState) {
            QingTransferConnectionState.IDLE -> targetPeers.firstOrNull()?.let { peer ->
                pendingFile = null
                connection.connectAndSend(peer, file.uri, file.name, file.size)
            }
            QingTransferConnectionState.CONNECTED -> {
                pendingFile = null
                connection.sendFile(file.uri, file.name, file.size)
            }
            else -> Unit
        }
    }
    LaunchedEffect(lastCompleted, connectionState, transferProgress, incomingOffer) {
        if (lastCompleted != null && connectionState == QingTransferConnectionState.CONNECTED &&
            transferProgress == null && incomingOffer == null) {
            connection.disconnect()
            if (!showContent) {
                Toast.makeText(context, context.getString(R.string.qing_transfer_success), Toast.LENGTH_SHORT).show()
                onClose()
            }
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
        val autoAccepting = connection.canAutomaticallyAccept()
        if (autoAccepting) {
            androidx.compose.runtime.LaunchedEffect(offer) { connection.acceptIncomingAutomatically() }
        }
        if (!autoAccepting) AlertDialog(
            onDismissRequest = { connection.rejectIncomingFile() },
            title = { Text(stringResource(R.string.qing_transfer_incoming_file_title)) },
            text = { Text(stringResource(R.string.qing_transfer_incoming_file_body, incomingPeer?.displayName ?: "QingToolbox", offer.name, offer.size)) },
            confirmButton = { TextButton(onClick = {
                if (!connection.acceptIncomingToDefault()) {
                    saveOffer = offer
                    runCatching { saveLauncher.launch(offer.name) }.onFailure {
                        saveOffer = null
                        connection.rejectIncomingFile()
                    }
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

    if (showContent) LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        if (lastCompleted != null) {
            item {
                QingCard(modifier = Modifier.fillMaxWidth()) {
                    Column(modifier = Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        Text(
                            stringResource(R.string.qing_transfer_success),
                            style = MaterialTheme.typography.titleMedium,
                            fontWeight = FontWeight.SemiBold,
                            color = MaterialTheme.colorScheme.primary,
                        )
                        QingPrimaryButton(onClick = onClose, modifier = Modifier.fillMaxWidth()) {
                            Text(stringResource(R.string.qing_transfer_close))
                        }
                    }
                }
            }
        } else if (connectionState != QingTransferConnectionState.IDLE) {
            item {
                QingCard(modifier = Modifier.fillMaxWidth()) {
                    Column(modifier = Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
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
                                runCatching { sendLauncher.launch(arrayOf("*/*")) }.onFailure {
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
        if (lastCompleted == null && connectionState == QingTransferConnectionState.IDLE) {
            item {
                QingStatusText(
                    text = stringResource(if (state == QingTransferDiscoveryState.ERROR)
                        R.string.qing_transfer_error else R.string.qing_transfer_searching),
                    modifier = Modifier.padding(top = 2.dp, bottom = 20.dp),
                )
            }
        }
    }
}
@Composable
internal fun QingTransferReceiveSettingsDialog(onDismiss: () -> Unit) {
    val context = LocalContext.current
    val connection = remember(context) { QingTransferProcessSessionStore.get(context).connection }
    var receivePreferences by remember { mutableStateOf(connection.receivePreferences()) }
    val treeLauncher = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) {
            runCatching { context.contentResolver.takePersistableUriPermission(uri, android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION or android.content.Intent.FLAG_GRANT_WRITE_URI_PERMISSION) }
            receivePreferences = receivePreferences.copy(defaultTreeUri = uri.toString())
            connection.updateReceivePreferences(receivePreferences)
        }
    }
        AlertDialog(
            onDismissRequest = onDismiss,
            title = { Text(stringResource(R.string.qing_transfer_receive_settings)) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    QingSecondaryButton(onClick = { treeLauncher.launch(null) }, modifier = Modifier.fillMaxWidth()) {
                        Icon(Icons.Outlined.Folder, contentDescription = null)
                        Spacer(Modifier.size(8.dp))
                        Text(stringResource(R.string.qing_transfer_choose_directory))
                    }
                    Text(
                        if (receivePreferences.defaultTreeUri.isNullOrBlank()) stringResource(R.string.qing_transfer_no_default_directory)
                        else stringResource(R.string.qing_transfer_default_directory_configured),
                        style = MaterialTheme.typography.bodySmall,
                    )
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        QingSwitch(
                            checked = receivePreferences.useDefaultDirectory,
                            onCheckedChange = { checked ->
                                receivePreferences = receivePreferences.copy(useDefaultDirectory = checked)
                                connection.updateReceivePreferences(receivePreferences)
                            },
                        )
                        Text(stringResource(R.string.qing_transfer_use_default_directory))
                    }
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        QingSwitch(
                            checked = receivePreferences.autoAccept,
                            onCheckedChange = { checked ->
                                receivePreferences = receivePreferences.copy(autoAccept = checked)
                                connection.updateReceivePreferences(receivePreferences)
                            },
                        )
                        Text(stringResource(R.string.qing_transfer_auto_accept))
                    }
                    Text(stringResource(R.string.qing_transfer_auto_accept_hint), style = MaterialTheme.typography.bodySmall)
                }
            },
            confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.ok)) } },
        )
}

internal fun queryTransferFile(context: android.content.Context, uri: android.net.Uri): Pair<String, Long>? {
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
