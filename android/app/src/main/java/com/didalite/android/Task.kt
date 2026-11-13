package com.didalite.android

import org.json.JSONObject

/**
 * 与 PC 端 TaskItem 字段一一对应。
 * JSON 一律 snake_case，空值不输出（服务端用 WhenWritingNull，缺省即 null）。
 * 所有时间均为 UTC 毫秒。
 */
data class Task(
    val id: String,
    val title: String,
    val note: String? = null,
    val location: String? = null,
    val startAt: Long? = null,
    val dueAt: Long? = null,
    val allDay: Boolean = false,
    val remindAt: Long? = null,
    val repeatRule: String = "none",
    val done: Boolean = false,
    val deleted: Boolean = false,
    val updatedAt: Long = 0,
    val rev: Long = 0,
    val dirty: Boolean = false,
) {
    /** 排序与展示用的锚点时间：起始优先，其次截止。 */
    val anchorAt: Long get() = startAt ?: dueAt ?: Long.MAX_VALUE

    fun toJson(): JSONObject = JSONObject().apply {
        put("id", id)
        put("title", title)
        note?.let { put("note", it) }
        location?.let { put("location", it) }
        startAt?.let { put("start_at", it) }
        dueAt?.let { put("due_at", it) }
        put("all_day", allDay)
        remindAt?.let { put("remind_at", it) }
        put("repeat_rule", repeatRule)
        put("done", done)
        put("deleted", deleted)
        put("updated_at", updatedAt)
        put("rev", rev)
    }

    companion object {
        fun fromJson(o: JSONObject): Task = Task(
            id = o.getString("id"),
            title = o.optString("title", ""),
            note = o.stringOrNull("note"),
            location = o.stringOrNull("location"),
            startAt = o.longOrNull("start_at"),
            dueAt = o.longOrNull("due_at"),
            allDay = o.optBoolean("all_day", false),
            remindAt = o.longOrNull("remind_at"),
            repeatRule = o.optString("repeat_rule", "none"),
            done = o.optBoolean("done", false),
            deleted = o.optBoolean("deleted", false),
            updatedAt = o.optLong("updated_at", 0),
            rev = o.optLong("rev", 0),
            dirty = false,
        )
    }
}

private fun JSONObject.stringOrNull(key: String): String? =
    if (has(key) && !isNull(key)) getString(key) else null

private fun JSONObject.longOrNull(key: String): Long? =
    if (has(key) && !isNull(key)) getLong(key) else null
