package com.didalite.android

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** 通知上的三个动作：完成 / 推迟 10 分钟 / 推迟 1 天。 */
class ReminderActionReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        val id = intent.getStringExtra(ReminderNotifier.EXTRA_TASK_ID) ?: return
        val task = Db(context).find(id) ?: return
        when (intent.action) {
            ReminderNotifier.ACTION_COMPLETE -> ReminderActions.complete(context, task)
            ReminderNotifier.ACTION_SNOOZE_10 -> ReminderActions.snooze(context, task, 10 * 60 * 1000L)
            ReminderNotifier.ACTION_SNOOZE_DAY -> ReminderActions.snooze(context, task, 24 * 60 * 60 * 1000L)
        }
    }
}
