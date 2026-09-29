package com.qingtoolbox.android

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.content.ContextCompat
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch

/** Visible, user-stoppable LAN presence while trusted devices or revocations exist. */
class DeviceLinkService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        val channel = NotificationChannel(CHANNEL, getString(R.string.device_hub_service_channel),
            NotificationManager.IMPORTANCE_LOW)
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(this, 1, Intent(this, DeviceLinkService::class.java).apply {
            action = ACTION_STOP
        }, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val notification = Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.mipmap.ic_launcher)
            .setContentTitle(getString(R.string.device_hub_service_title))
            .setContentText(getString(R.string.device_hub_service_text))
            .setContentIntent(open)
            .addAction(Notification.Action.Builder(null, getString(R.string.device_hub_service_stop), stop).build())
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
        val session = DeviceDiscoverySessionStore.get(this)
        session.acquire("service")
        val transfer = QingTransferProcessSessionStore.get(this)
        transfer.discovery.acquire("device-link")
        scope.launch {
            transfer.connection.incomingOffer.collect { offer ->
                val updated = Notification.Builder(this@DeviceLinkService, CHANNEL)
                    .setSmallIcon(R.mipmap.ic_launcher)
                    .setContentTitle(getString(R.string.device_hub_service_title))
                    .setContentText(if (offer == null) getString(R.string.device_hub_service_text)
                        else getString(R.string.device_hub_incoming_file))
                    .setContentIntent(open)
                    .addAction(Notification.Action.Builder(null, getString(R.string.device_hub_service_stop), stop).build())
                    .setOngoing(true)
                    .build()
                getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, updated)
            }
        }
        scope.launch {
            transfer.connection.lastCompleted.collect { completed ->
                if (completed == null) return@collect
                val updated = Notification.Builder(this@DeviceLinkService, CHANNEL)
                    .setSmallIcon(R.mipmap.ic_launcher)
                    .setContentTitle(getString(R.string.device_hub_service_title))
                    .setContentText(getString(R.string.device_hub_file_received))
                    .setContentIntent(open)
                    .addAction(Notification.Action.Builder(null, getString(R.string.device_hub_service_stop), stop).build())
                    .setOngoing(true)
                    .build()
                getSystemService(NotificationManager::class.java).notify(NOTIFICATION_ID, updated)
            }
        }
        scope.launch {
            session.pairing.snapshot.collect { state ->
                if (state.paired.isEmpty() && state.revocations.isEmpty()) stopSelf()
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) { stopSelf(); return START_NOT_STICKY }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        scope.cancel()
        QingTransferProcessSessionStore.get(this).discovery.release("device-link")
        DeviceDiscoverySessionStore.get(this).release("service")
        super.onDestroy()
    }

    companion object {
        private const val CHANNEL = "qingtoolbox-devices"
        private const val ACTION_STOP = "com.qingtoolbox.android.STOP_DEVICE_LINK"
        private const val NOTIFICATION_ID = 2401

        fun update(context: Context, needed: Boolean) {
            val intent = Intent(context, DeviceLinkService::class.java)
            if (needed) ContextCompat.startForegroundService(context, intent)
            else context.stopService(intent)
        }
    }
}
