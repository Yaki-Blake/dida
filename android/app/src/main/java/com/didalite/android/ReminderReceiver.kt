package com.didalite.android

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/**
 * 闹钟到点：弹通知（含全屏提醒），重复任务顺手推进到下一次。
 * 非重复任务的 PendingIntent 是一次性的，响过即失效，不会重复响。
 */
class ReminderReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        val id = intent.getStringExtra(ReminderScheduler.EXTRA_TASK_ID) ?: return
        val db = Db(context)
        val task = db.find(id) ?: return
        if (task.deleted) {
            ReminderScheduler.cancel(context, id)
            return
        }
        if (task.done) return

        ReminderNotifier.show(context, task)

        // 重复任务：响过就推进到下一次并排下一条，避免下一天漏响
        val advanced = ReminderPlanner.advance(task, System.currentTimeMillis())
        if (advanced != null) {
            db.saveLocal(advanced)
            ReminderScheduler.schedule(context, advanced)
        }
    }
}
