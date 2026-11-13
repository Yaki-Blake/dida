package com.didalite.android

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** 开机、应用更新、系统时间或时区变化后重排全部闹钟（计划书 T4.4）。 */
class BootReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        when (intent.action) {
            Intent.ACTION_BOOT_COMPLETED,
            Intent.ACTION_MY_PACKAGE_REPLACED,
            Intent.ACTION_TIME_CHANGED,
            Intent.ACTION_TIMEZONE_CHANGED,
            -> ReminderScheduler.rescheduleAll(context)
        }
    }
}
