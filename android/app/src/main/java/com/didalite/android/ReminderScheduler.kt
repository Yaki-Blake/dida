package com.didalite.android

import android.app.AlarmManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.os.Build

/**
 * 用 AlarmManager 做精确闹钟（计划书 4.3）。
 * 提醒永远由本机排程，断网、PC 关机都不影响。
 *
 * 有「精确闹钟」权限时用 setAlarmClock：系统把它当用户可见闹钟，息屏与 Doze 也会准点唤醒。
 * 实测小米 HyperOS 会推迟普通的 setExactAndAllowWhileIdle，直到亮屏才补发，所以不能只靠它。
 * 没有该权限时退化为 setAndAllowWhileIdle，仍能响，只是可能晚几分钟。
 */
object ReminderScheduler {

    const val ACTION_FIRE = "com.didalite.android.action.REMINDER_FIRE"
    const val EXTRA_TASK_ID = "task_id"

    fun schedule(context: Context, task: Task) {
        val at = ReminderPlanner.effectiveRemindAt(task)
        if (task.done || task.deleted || at == null || at <= System.currentTimeMillis()) {
            cancel(context, task.id)
            return
        }
        val manager = context.getSystemService(AlarmManager::class.java) ?: return
        val pending = pendingIntent(context, task.id)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S && !manager.canScheduleExactAlarms()) {
            manager.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, pending)
        } else {
            // 闹钟类型：息屏、Doze 都会准点唤醒设备，厂商的省电策略对它最宽容
            manager.setAlarmClock(AlarmManager.AlarmClockInfo(at, null), pending)
        }
    }

    fun cancel(context: Context, taskId: String) {
        val manager = context.getSystemService(AlarmManager::class.java) ?: return
        manager.cancel(pendingIntent(context, taskId))
    }

    /** 重排全部：开机、时钟变更、应用启动、同步之后都走这里。 */
    fun rescheduleAll(context: Context) {
        val db = Db(context)
        val now = System.currentTimeMillis()
        db.all().forEach { task ->
            val at = ReminderPlanner.effectiveRemindAt(task)
            if (task.done || task.deleted || at == null || at <= now) {
                cancel(context, task.id)
            } else {
                schedule(context, task)
            }
        }
    }

    private fun pendingIntent(context: Context, taskId: String): PendingIntent {
        val intent = Intent(context, ReminderReceiver::class.java).apply {
            action = ACTION_FIRE
            putExtra(EXTRA_TASK_ID, taskId)
        }
        return PendingIntent.getBroadcast(
            context,
            taskId.hashCode(),
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
    }
}
