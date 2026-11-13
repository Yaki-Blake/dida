package com.didalite.android

import android.content.Context
import android.provider.Settings

/** 提醒相关系统权限的查询与跳转，配合首启引导页（计划书 6.6）。 */
object Permissions {

    fun notificationsEnabled(context: Context): Boolean =
        context.getSystemService(android.app.NotificationManager::class.java)?.areNotificationsEnabled() == true

    fun canExactAlarm(context: Context): Boolean {
        if (android.os.Build.VERSION.SDK_INT < android.os.Build.VERSION_CODES.S) return true
        val manager = context.getSystemService(android.app.AlarmManager::class.java) ?: return false
        return manager.canScheduleExactAlarms()
    }

    fun ignoringBatteryOptimizations(context: Context): Boolean {
        val power = context.getSystemService(android.os.PowerManager::class.java) ?: return false
        return power.isIgnoringBatteryOptimizations(context.packageName)
    }

    fun allGranted(context: Context): Boolean =
        notificationsEnabled(context) && canExactAlarm(context) && ignoringBatteryOptimizations(context)

    /** 锁屏弹出全屏提醒页；Android 14 起系统默认不给普通应用，需要用户手动打开。 */
    fun canFullScreenIntent(context: Context): Boolean {
        if (android.os.Build.VERSION.SDK_INT < 34) return true
        return context.getSystemService(android.app.NotificationManager::class.java)?.canUseFullScreenIntent() == true
    }

    fun openNotificationSettings(context: Context) {
        val intent = android.content.Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).apply {
            putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
            addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        runCatching { context.startActivity(intent) }
    }

    fun requestExactAlarm(context: Context) {
        val intent = if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.S) {
            android.content.Intent(Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM).apply {
                data = android.net.Uri.parse("package:${context.packageName}")
                addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
            }
        } else {
            appDetails(context)
        }
        runCatching { context.startActivity(intent) }
    }

    fun requestIgnoreBattery(context: Context) {
        val intent = android.content.Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
            data = android.net.Uri.parse("package:${context.packageName}")
            addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        runCatching { context.startActivity(intent) }
    }

    fun requestFullScreenIntent(context: Context) {
        val intent = if (android.os.Build.VERSION.SDK_INT >= 34) {
            android.content.Intent(Settings.ACTION_MANAGE_APP_USE_FULL_SCREEN_INTENT).apply {
                data = android.net.Uri.parse("package:${context.packageName}")
                addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
            }
        } else {
            appDetails(context)
        }
        runCatching { context.startActivity(intent) }
    }

    private fun appDetails(context: Context) = android.content.Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
        data = android.net.Uri.parse("package:${context.packageName}")
        addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
    }
}
