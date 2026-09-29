package com.qingtoolbox.android

import android.app.Notification
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification

/** Notification access is granted by Android's settings, never by pairing alone. */
class DeviceNotificationListener : NotificationListenerService() {
    private val recent = LinkedHashMap<String, Pair<Int, Long>>()

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        if (sbn.packageName == packageName ||
            sbn.notification.flags and Notification.FLAG_GROUP_SUMMARY != 0 ||
            sbn.notification.flags and Notification.FLAG_ONGOING_EVENT != 0) return
        val extras = sbn.notification.extras ?: return
        val title = clean(extras.getCharSequence(Notification.EXTRA_TITLE)?.toString(), 160)
        val body = clean((extras.getCharSequence(Notification.EXTRA_BIG_TEXT)
            ?: extras.getCharSequence(Notification.EXTRA_TEXT))?.toString(), 700, multiline = true)
        if (title.isEmpty() && body.isEmpty()) return
        val appName = runCatching {
            val info = packageManager.getApplicationInfo(sbn.packageName, 0)
            packageManager.getApplicationLabel(info).toString()
        }.getOrDefault(sbn.packageName).let { clean(it, 80) }
        val now = System.nanoTime()
        val fingerprint = listOf(appName, title, body).hashCode()
        synchronized(recent) {
            val previous = recent[sbn.key]
            if (previous != null && previous.first == fingerprint && now - previous.second < 60_000_000_000L) return
            recent[sbn.key] = fingerprint to now
            while (recent.size > 128) recent.remove(recent.keys.first())
        }
        DeviceDiscoverySessionStore.get(this).forwardNotification(appName, title, body)
    }

    private fun clean(value: String?, max: Int, multiline: Boolean = false): String = value.orEmpty()
        .filter { !it.isISOControl() || multiline && (it == '\n' || it == '\t') }
        .trim().take(max)
}
