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
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.res.stringResource
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver

@Composable
fun QingTransferDevicesScreen(modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    var peers by remember { mutableStateOf(emptyList<QingTransferPeer>()) }
    var state by remember { mutableStateOf(QingTransferDiscoveryState.IDLE) }
    val discovery = remember(context) {
        QingTransferDiscovery(
            context = context,
            onPeersChanged = { peers = it },
            onStateChanged = { state = it },
        )
    }
    DisposableEffect(lifecycleOwner, discovery) {
        val observer = LifecycleEventObserver { _, event ->
            when (event) {
                Lifecycle.Event.ON_START -> discovery.start()
                Lifecycle.Event.ON_STOP -> discovery.stop()
                else -> Unit
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        if (lifecycleOwner.lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED)) discovery.start()
        onDispose {
            lifecycleOwner.lifecycle.removeObserver(observer)
            discovery.stop()
        }
    }

    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item {
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
                            Icon(Icons.Outlined.DevicesOther, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
                        }
                        Column(modifier = Modifier.weight(1f)) {
                            Text(stringResource(R.string.qing_transfer_title), style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
                            Text(stringResource(R.string.qing_transfer_body), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                    QingSecondaryButton(
                        onClick = { discovery.restart() },
                        modifier = Modifier.fillMaxWidth(),
                        enabled = state != QingTransferDiscoveryState.ERROR,
                    ) {
                        Icon(Icons.Outlined.Refresh, contentDescription = null)
                        Spacer(Modifier.size(8.dp))
                        Text(stringResource(R.string.qing_transfer_refresh))
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
            items(peers, key = { it.serviceName }) { peer ->
                QingTransferPeerCard(peer)
            }
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

@Composable
private fun QingTransferPeerCard(peer: QingTransferPeer) {
    QingCard(modifier = Modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier.padding(16.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            QingIconSurface(modifier = Modifier.size(44.dp)) {
                Icon(Icons.Outlined.DevicesOther, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
            }
            Column(modifier = Modifier.weight(1f)) {
                Text(peer.displayName, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                Text(
                    text = if (peer.platform.equals("android", ignoreCase = true)) {
                        stringResource(R.string.qing_transfer_android)
                    } else {
                        stringResource(R.string.qing_transfer_windows)
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                if (peer.addresses.isNotEmpty() && peer.port > 0) {
                    QingStatusText(text = peer.addresses.joinToString(", ") + ":${peer.port}")
                }
            }
            Text(
                text = if (peer.online) stringResource(R.string.qing_transfer_online) else stringResource(R.string.qing_transfer_offline),
                style = MaterialTheme.typography.labelMedium,
                color = if (peer.online) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}
