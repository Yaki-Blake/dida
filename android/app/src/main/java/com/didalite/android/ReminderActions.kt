package com.didalite.android

import android.content.Context

/** 提醒的三个动作，通知按钮与全屏提醒页共用。 */
object ReminderActions {

    fun complete(context: Context, task: Task) {
        val db = Db(context)
        if (RepeatRules.isRepeating(task.repeatRule)) {
            // 响铃时已经推进到下一次，这里只确认本次，不能取消下一次的闹钟
        } else {
            db.saveLocal(task.copy(done = true, updatedAt = System.currentTimeMillis()))
            ReminderScheduler.cancel(context, task.id)
        }
        ReminderNotifier.cancel(context, task.id)
    }

    fun snooze(context: Context, task: Task, delayMs: Long) {
        val db = Db(context)
        val now = System.currentTimeMillis()
        val next = task.copy(remindAt = now + delayMs, updatedAt = now)
        db.saveLocal(next)
        ReminderScheduler.schedule(context, next)
        ReminderNotifier.cancel(context, task.id)
    }
}
