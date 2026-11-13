package com.didalite.android

import org.json.JSONObject

/**
 * 与 PC 端 AlarmItem 字段一一对应。
 * 只来自电脑端「闹钟」页（本地 alarm 表），与任务提醒、日历日程无关。
 */
data class Alarm(
    val id: String,
    val label: String = "",
    val hour: Int = 7,
    val minute: Int = 0,
    val repeatRule: String = "daily",
    val onceAt: Long? = null,
    val enabled: Boolean = true,
) {
    /** 时钟样式：固定两位，避免 7:5 这种读不出来的写法。 */
    val timeText: String get() = "%02d:%02d".format(hour, minute)

    companion object {
        fun fromJson(o: JSONObject): Alarm = Alarm(
            id = o.getString("id"),
            label = o.optString("label", ""),
            hour = o.optInt("hour", 7),
            minute = o.optInt("minute", 0),
            repeatRule = o.optString("repeat_rule", "daily"),
            onceAt = if (o.has("once_at") && !o.isNull("once_at")) o.getLong("once_at") else null,
            enabled = o.optBoolean("enabled", true),
        )
    }
}

/**
 * 手机端对闹钟的改动：只能开关和删除，攒在本机，下次同步上报给电脑端。
 * 电脑端的 alarm 表是唯一出处，所以这里只发「哪个 id 改成什么」，不碰时分和名称。
 */
data class AlarmOp(
    val id: String,
    val enabled: Boolean? = null,
    val deleted: Boolean? = null,
) {
    fun toJson(): JSONObject = JSONObject().put("id", id).apply {
        enabled?.let { put("enabled", it) }
        deleted?.let { put("deleted", it) }
    }

    companion object {
        fun fromJson(o: JSONObject): AlarmOp = AlarmOp(
            id = o.getString("id"),
            enabled = if (o.has("enabled")) o.getBoolean("enabled") else null,
            deleted = if (o.has("deleted")) o.getBoolean("deleted") else null,
        )
    }
}
