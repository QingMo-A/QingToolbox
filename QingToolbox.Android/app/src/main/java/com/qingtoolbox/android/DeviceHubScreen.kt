package com.qingtoolbox.android

import android.content.Intent
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.app.NotificationManagerCompat
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.DesktopWindows
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.Refresh
import androidx.compose.material.icons.outlined.Smartphone
import androidx.compose.material.icons.outlined.Notifications
import androidx.compose.material.icons.outlined.NotificationsOff
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DeviceHubScreen() {
    val context = LocalContext.current
    val transferSession = remember(context) { QingTransferProcessSessionStore.get(context) }
    var transferTarget by remember { mutableStateOf<PairedDevice?>(null) }
    var transferAddress by remember { mutableStateOf<String?>(null) }
    var initialTransferFile by remember { mutableStateOf<QingTransferSelectedFile?>(null) }
    var pendingPickerTarget by remember { mutableStateOf<Pair<PairedDevice, String?>?>(null) }
    var showReceiveSettings by remember { mutableStateOf(false) }
    val incomingOffer by transferSession.connection.incomingOffer.collectAsStateWithLifecycle()
    val sendLauncher = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        val pending = pendingPickerTarget
        pendingPickerTarget = null
        if (uri != null && pending != null) {
            val details = queryTransferFile(context, uri)
            if (details == null || details.second < 0L) {
                transferSession.connection.reportTransferFailure()
            } else {
                val (peer, address) = pending
                if (transferSession.selectedDeviceId != peer.discoveryId) {
                    if (transferSession.connection.progress.value != null) return@rememberLauncherForActivityResult
                    transferSession.connection.disconnect()
                }
                transferSession.selectedDeviceId = peer.discoveryId
                transferSession.connection.clearCompletion()
                transferSession.connection.targetDeviceId = peer.discoveryId
                transferSession.connection.targetAddress = address
                transferSession.discovery.hintTrustedAddress(peer.discoveryId, address)
                transferAddress = address
                initialTransferFile = QingTransferSelectedFile(uri, details.first, details.second)
                transferTarget = peer
            }
        }
    }
    LaunchedEffect(incomingOffer) {
        if (incomingOffer == null || transferTarget != null) return@LaunchedEffect
        val connection = transferSession.connection
        val paired = DeviceDiscoverySessionStore.get(context).pairing.snapshot.value.paired
            .firstOrNull { it.discoveryId == connection.targetDeviceId } ?: return@LaunchedEffect
        transferAddress = connection.targetAddress
        initialTransferFile = null
        transferTarget = paired
    }
    NearbyDevicesScreen(onReceiveSettings = { showReceiveSettings = true }, onTransferOpen = { peer, address ->
        if (transferSession.connection.progress.value != null) return@NearbyDevicesScreen
        transferSession.discovery.hintTrustedAddress(peer.discoveryId, address)
        pendingPickerTarget = peer to address
        runCatching { sendLauncher.launch(arrayOf("*/*")) }.onFailure {
            pendingPickerTarget = null
            transferSession.connection.reportTransferFailure()
        }
    })
    transferTarget?.let { target ->
        androidx.compose.runtime.key(target.discoveryId, initialTransferFile?.uri) {
            // File transfer is an action on an already connected device. Keep the
            // transport discovery and progress state internal instead of opening a
            // second device-picker sheet that says it is looking for the same peer.
            QingTransferDevicesScreen(
                targetDeviceId = target.discoveryId,
                targetAddress = transferAddress,
                initialFile = initialTransferFile,
                showContent = false,
                onClose = {
                    transferSession.connection.disconnect()
                    transferTarget = null
                    initialTransferFile = null
                },
            )
        }
    }
    if (showReceiveSettings) QingTransferReceiveSettingsDialog(onDismiss = { showReceiveSettings = false })
}

@Composable
private fun NearbyDevicesScreen(
    onReceiveSettings: () -> Unit,
    onTransferOpen: (PairedDevice, String?) -> Unit,
) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val session = remember(context) { DeviceDiscoverySessionStore.get(context) }
    val state by session.snapshot.collectAsStateWithLifecycle()
    val pairing by session.pairing.snapshot.collectAsStateWithLifecycle()
    var notificationAccess by remember { mutableStateOf(
        context.packageName in NotificationManagerCompat.getEnabledListenerPackages(context)
    ) }
    val needsLinkService = pairing.paired.isNotEmpty() || pairing.revocations.isNotEmpty()
    LaunchedEffect(needsLinkService) {
        if (lifecycleOwner.lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED)) {
            DeviceLinkService.update(context, needsLinkService)
        }
    }
    var revokeCandidate by remember { mutableStateOf<PairedDevice?>(null) }
    var acknowledgedNotices by remember { mutableStateOf(emptySet<String>()) }

    DisposableEffect(lifecycleOwner, session) {
        val observer = LifecycleEventObserver { _, event ->
            when (event) {
                Lifecycle.Event.ON_START -> session.acquire("screen")
                Lifecycle.Event.ON_RESUME -> notificationAccess =
                    context.packageName in NotificationManagerCompat.getEnabledListenerPackages(context)
                Lifecycle.Event.ON_STOP -> session.release("screen")
                else -> Unit
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        if (lifecycleOwner.lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED)) session.acquire("screen")
        onDispose {
            lifecycleOwner.lifecycle.removeObserver(observer)
            session.release("screen")
        }
    }

    pairing.pending.firstOrNull()?.let { request ->
        AlertDialog(
            onDismissRequest = { if (!request.localApproved) session.pairing.decide(request.sessionId, false) },
            title = { Text(stringResource(R.string.device_hub_pair_title, request.name)) },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text(stringResource(R.string.device_hub_pair_compare))
                    Text(request.code, style = MaterialTheme.typography.headlineMedium,
                        fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.primary)
                    if (request.localApproved) Text(stringResource(R.string.device_hub_pair_waiting))
                }
            },
            confirmButton = {
                if (!request.localApproved) TextButton(onClick = {
                    session.pairing.decide(request.sessionId, true)
                }) { Text(stringResource(R.string.device_hub_pair_accept)) }
            },
            dismissButton = {
                if (!request.localApproved) TextButton(onClick = {
                    session.pairing.decide(request.sessionId, false)
                }) { Text(stringResource(R.string.device_hub_pair_reject)) }
            },
        )
    }
    pairing.actions.firstOrNull()?.let { request ->
        AlertDialog(
            onDismissRequest = { if (!request.localApproved) session.pairing.decideAction(request.sessionId, false) },
            title = { Text(stringResource(if (request.action == DeviceAction.UPGRADE)
                R.string.device_hub_upgrade_request else if (request.action == DeviceAction.DISCONNECT)
                R.string.device_hub_disconnect_request else R.string.device_hub_demote_request, request.name)) },
            text = { Text(stringResource(R.string.device_hub_action_confirm)) },
            confirmButton = {
                if (!request.localApproved) TextButton(onClick = {
                    session.pairing.decideAction(request.sessionId, true)
                }) { Text(stringResource(R.string.device_hub_pair_accept)) }
            },
            dismissButton = {
                if (!request.localApproved) TextButton(onClick = {
                    session.pairing.decideAction(request.sessionId, false)
                }) { Text(stringResource(R.string.device_hub_pair_reject)) }
            },
        )
    }
    pairing.notices.firstOrNull { it.id !in acknowledgedNotices }?.let { notice ->
        AlertDialog(
            onDismissRequest = { acknowledgedNotices = acknowledgedNotices + notice.id },
            title = { Text(stringResource(R.string.device_hub_action_done)) },
            text = { Text(stringResource(when (notice.action) {
                DeviceAction.UPGRADE -> R.string.device_hub_upgrade_done
                DeviceAction.DISCONNECT -> R.string.device_hub_disconnect_done
                DeviceAction.DEMOTE -> R.string.device_hub_demote_done
                DeviceAction.DISCONNECT_NOTICE -> R.string.device_hub_disconnect_done
            }, notice.name)) },
            confirmButton = { TextButton(onClick = { acknowledgedNotices = acknowledgedNotices + notice.id }) {
                Text(stringResource(R.string.ok))
            } },
        )
    }
    revokeCandidate?.let { peer ->
        AlertDialog(
            onDismissRequest = { revokeCandidate = null },
            title = { Text(stringResource(R.string.device_hub_remove_title)) },
            text = { Text(stringResource(R.string.device_hub_remove_body, peer.name)) },
            confirmButton = { TextButton(onClick = {
                session.beginAction(peer.id, DeviceAction.DISCONNECT)
                revokeCandidate = null
            }) { Text(stringResource(R.string.device_hub_remove)) } },
            dismissButton = { TextButton(onClick = { revokeCandidate = null }) {
                Text(stringResource(R.string.cancel))
            } },
        )
    }

    val pairedIds = pairing.paired.mapTo(HashSet()) { it.discoveryId }
    val strangers = state.nearby.filterNot { it.discoveryId in pairedIds }

    LazyColumn(
        modifier = Modifier.fillMaxSize(),
        contentPadding = PaddingValues(horizontal = 20.dp, vertical = 12.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item {
            QingCard(modifier = Modifier.fillMaxWidth()) {
                Row(
                    modifier = Modifier.fillMaxWidth().padding(18.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(14.dp),
                ) {
                    // This card describes the phone in the reader's hand, so it gets the
                    // phone glyph rather than the generic device one.
                    Icon(Icons.Outlined.Smartphone, contentDescription = null,
                        tint = MaterialTheme.colorScheme.primary)
                    Column(modifier = Modifier.weight(1f)) {
                        Text(state.ownName, style = MaterialTheme.typography.titleLarge,
                            fontWeight = FontWeight.SemiBold)
                        Text(
                            if (state.available) stringResource(R.string.device_hub_discoverable)
                            else stringResource(R.string.device_hub_offline),
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                    IconButton(onClick = session::restart) {
                        Icon(Icons.Outlined.Refresh, contentDescription = stringResource(R.string.qing_transfer_refresh))
                    }
                    IconButton(onClick = onReceiveSettings) {
                        Icon(Icons.Outlined.Folder, contentDescription = stringResource(R.string.qing_transfer_receive_settings))
                    }
                }
            }
        }
        item {
            Text(stringResource(R.string.device_hub_intimate),
                style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        }
        if (pairing.paired.any { it.intimate && it.platform == "windows" } && !notificationAccess) item {
            QingCard(modifier = Modifier.fillMaxWidth()) {
                Row(modifier = Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(R.string.device_hub_notification_access_hint),
                        modifier = Modifier.weight(1f), style = MaterialTheme.typography.bodySmall)
                    TextButton(onClick = {
                        runCatching { context.startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)) }
                    }) { Text(stringResource(R.string.device_hub_notification_access_action)) }
                }
            }
        }
        val intimate = pairing.paired.filter { it.intimate }
        if (intimate.isEmpty()) item { Text(stringResource(R.string.device_hub_none_paired),
            color = MaterialTheme.colorScheme.onSurfaceVariant) }
        items(intimate, key = { "intimate:${it.id}" }) { peer ->
            PairedDeviceCard(peer, peer.id in pairing.online,
                pairing.batteries.firstOrNull { it.peerId == peer.id && System.currentTimeMillis() - it.receivedAtMs < 130_000L },
                onRelationship = { session.beginAction(peer.id, DeviceAction.DEMOTE) },
                onToggleForwarding = { session.pairing.setNotificationForwarding(peer.id, !peer.forwardNotifications) },
                onTransfer = { onTransferOpen(peer,
                    state.nearby.firstOrNull { it.discoveryId == peer.discoveryId }?.address) },
                onRemove = { revokeCandidate = peer })
        }
        item {
            Text(stringResource(R.string.device_hub_connected),
                style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        }
        val connected = pairing.paired.filterNot { it.intimate }
        if (connected.isEmpty()) item { Text(stringResource(R.string.device_hub_none_paired),
            color = MaterialTheme.colorScheme.onSurfaceVariant) }
        items(connected, key = { "connected:${it.id}" }) { peer ->
            PairedDeviceCard(peer, peer.id in pairing.online,
                pairing.batteries.firstOrNull { it.peerId == peer.id && System.currentTimeMillis() - it.receivedAtMs < 130_000L },
                onRelationship = { session.beginAction(peer.id, DeviceAction.UPGRADE) },
                onToggleForwarding = { },
                onTransfer = { onTransferOpen(peer,
                    state.nearby.firstOrNull { it.discoveryId == peer.discoveryId }?.address) },
                onRemove = { revokeCandidate = peer })
        }
        if (pairing.revocations.isNotEmpty()) item {
            Text(stringResource(R.string.device_hub_pending_disconnect, pairing.revocations.size),
                color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        item {
            Text(stringResource(R.string.device_hub_nearby),
                style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        }
        if (state.searching) item { CircularProgressIndicator() }
        state.error?.let { error ->
            item { Text(stringResource(when (error) {
                DeviceDiscoveryError.UNSUPPORTED -> R.string.device_hub_error_unsupported
                DeviceDiscoveryError.START_FAILED -> R.string.device_hub_error_start
                DeviceDiscoveryError.ADVERTISE_FAILED -> R.string.device_hub_error_advertise
                DeviceDiscoveryError.SEARCH_FAILED -> R.string.device_hub_error_search
            }), color = MaterialTheme.colorScheme.error) }
        }
        pairing.error?.let { error ->
            item { Row(verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(pairingErrorResource(error)),
                    modifier = Modifier.weight(1f), color = MaterialTheme.colorScheme.error)
                TextButton(onClick = session.pairing::clearError) { Text(stringResource(R.string.ok)) }
            } }
        }
        if (strangers.isEmpty() && !state.searching && state.error == null) {
            item { Text(stringResource(R.string.device_hub_none),
                color = MaterialTheme.colorScheme.onSurfaceVariant) }
        }
        items(strangers, key = { "nearby:${it.serviceName}" }) { peer ->
            QingCard(modifier = Modifier.fillMaxWidth()) {
                Row(
                    modifier = Modifier.fillMaxWidth().padding(18.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(14.dp),
                ) {
                    Icon(devicePlatformIcon(peer.platform), contentDescription = null,
                        tint = MaterialTheme.colorScheme.primary)
                    Column(modifier = Modifier.weight(1f)) {
                        Text(peer.name, style = MaterialTheme.typography.titleMedium,
                            fontWeight = FontWeight.SemiBold)
                        Text(if (peer.platform == "windows") "Windows" else "Android",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    TextButton(onClick = { session.beginPair(peer.discoveryId) }) {
                        Text(stringResource(R.string.device_hub_pair_action))
                    }
                }
            }
        }
    }
}

@Composable
private fun PairedDeviceCard(
    peer: PairedDevice,
    online: Boolean,
    battery: DeviceBattery?,
    onRelationship: () -> Unit,
    onToggleForwarding: () -> Unit,
    onTransfer: () -> Unit,
    onRemove: () -> Unit,
) {
    QingCard(modifier = Modifier.fillMaxWidth()) {
        Column(modifier = Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Icon(devicePlatformIcon(peer.platform), contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary)
                Column(modifier = Modifier.weight(1f)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(peer.name, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                        if (peer.intimate && peer.platform == "windows") IconButton(onClick = onToggleForwarding) {
                            Icon(if (peer.forwardNotifications) Icons.Outlined.Notifications
                                 else Icons.Outlined.NotificationsOff,
                                contentDescription = stringResource(if (peer.forwardNotifications)
                                    R.string.device_hub_forward_on else R.string.device_hub_forward_off),
                                tint = if (peer.forwardNotifications) MaterialTheme.colorScheme.primary
                                    else MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        IconButton(onClick = onTransfer, enabled = online) {
                            Icon(Icons.Outlined.Folder,
                                contentDescription = stringResource(R.string.device_hub_transfer),
                                tint = if (online) MaterialTheme.colorScheme.primary
                                    else MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                    Text(if (peer.platform == "windows") "Windows" else "Android",
                        style = MaterialTheme.typography.bodySmall)
                    if (battery != null) Text(stringResource(R.string.device_hub_battery,
                        battery.percent, if (battery.charging) stringResource(R.string.device_hub_charging) else ""),
                        style = MaterialTheme.typography.bodySmall)
                }
                Row(verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    QingStatusDot(online)
                    Text(stringResource(if (online) R.string.device_hub_peer_online else R.string.device_hub_peer_offline),
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                TextButton(onClick = onRelationship) {
                    Text(stringResource(if (peer.intimate) R.string.device_hub_make_connected
                        else R.string.device_hub_make_intimate))
                }
                TextButton(onClick = onRemove) { Text(stringResource(R.string.device_hub_remove)) }
            }
        }
    }
}

/**
 * The glyph for a peer, picked from the platform that peer reported.
 *
 * `platform` is validated as `android` or `windows` as it crosses the wire, but it stays a
 * claim about software rather than a fact about the hardware, so it is only ever allowed to
 * choose a picture — no behaviour may branch on it. Unknown values fall through to the phone
 * glyph rather than to nothing, so a peer is never drawn iconless.
 */
internal fun devicePlatformIcon(platform: String): ImageVector =
    if (platform == "windows") Icons.Outlined.DesktopWindows else Icons.Outlined.Smartphone

private fun pairingErrorResource(error: DevicePairingError): Int = when (error) {
    DevicePairingError.UNAVAILABLE -> R.string.device_hub_pair_unavailable
    DevicePairingError.CONNECTION_FAILED -> R.string.device_hub_pair_connection_failed
    DevicePairingError.HANDSHAKE_FAILED, DevicePairingError.ID_MISMATCH -> R.string.device_hub_pair_handshake_failed
    DevicePairingError.ALREADY_PAIRED -> R.string.device_hub_pair_already
    DevicePairingError.KEY_CHANGED -> R.string.device_hub_pair_key_changed
    DevicePairingError.TIMED_OUT -> R.string.device_hub_pair_timeout
    DevicePairingError.REMOTE_REJECTED -> R.string.device_hub_pair_remote_rejected
    DevicePairingError.SAVE_FAILED -> R.string.device_hub_pair_save_failed
    DevicePairingError.TOO_MANY -> R.string.device_hub_pair_too_many
    DevicePairingError.LOCAL_REJECTED -> R.string.device_hub_pair_remote_rejected
}
