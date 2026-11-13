package com.didalite.android

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** 到点通知：响铃 + 震动 + 全屏提醒，三个动作与 PC 及计划书 6.5 一致。 */
object ReminderNotifier {

    const val CHANNEL_ID = "dida_reminder"
    const val EXTRA_TASK_ID = "task_id"

    const val ACTION_COMPLETE = "com.didalite.android.action.COMPLETE"
    const val ACTION_SNOOZE_10 = "com.didalite.android.action.SNOOZE_10"
    const val ACTION_SNOOZE_DAY = "com.didalite.android.action.SNOOZE_DAY"

    fun show(context: Context, task: Task) {
        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        ensureChannel(manager)

        val open = PendingIntent.getActivity(
            context,
            task.id.hashCode(),
            Intent(context, ReminderActivity::class.java).putExtra(EXTRA_TASK_ID, task.id),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )

        val builder = Notification.Builder(context, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.ic_popup_reminder)
            .setContentTitle(task.title)
            .setContentText(describe(task))
            .setCategory(Notification.CATEGORY_ALARM)
            .setAutoCancel(true)
            .setContentIntent(open)
            .setFullScreenIntent(open, true)
            .addAction(
                Notification.Action.Builder(null, "完成", action(context, task.id, ACTION_COMPLETE, 1)).build()
            )
            .addAction(
                Notification.Action.Builder(null, "推迟 10 分钟", action(context, task.id, ACTION_SNOOZE_10, 2)).build()
            )
            .addAction(
                Notification.Action.Builder(null, "推迟 1 天", action(context, task.id, ACTION_SNOOZE_DAY, 3)).build()
            )

        manager.notify(task.id.hashCode(), builder.build())
    }

    fun cancel(context: Context, taskId: String) {
        context.getSystemService(NotificationManager::class.java)?.cancel(taskId.hashCode())
    }

    private fun action(context: Context, taskId: String, action: String, code: Int): PendingIntent =
        PendingIntent.getBroadcast(
            context,
            taskId.hashCode() * 10 + code,
            Intent(context, ReminderActionReceiver::class.java).apply {
                this.action = action
                putExtra(EXTRA_TASK_ID, taskId)
            },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )

    private fun ensureChannel(manager: NotificationManager) {
        if (manager.getNotificationChannel(CHANNEL_ID) != null) return
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "提醒", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "任务到点提醒"
                enableVibration(true)
                setSound(
                    android.provider.Settings.System.DEFAULT_NOTIFICATION_URI,
                    android.media.AudioAttributes.Builder()
                        .setUsage(android.media.AudioAttributes.USAGE_ALARM)
                        .setContentType(android.media.AudioAttributes.CONTENT_TYPE_SONIFICATION)
                        .build(),
                )
                lockscreenVisibility = Notification.VISIBILITY_PUBLIC
            }
        )
    }

    private fun describe(task: Task): String {
        val at = ReminderPlanner.effectiveRemindAt(task) ?: return "提醒"
        val text = SimpleDateFormat("HH:mm", Locale.CHINA).format(Date(at))
        return if (task.allDay) "全天 · $text" else "$text"
    }
}
