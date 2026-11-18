package com.didalite.android

import org.json.JSONObject

/**
 * 便签：标题 + 正文，不带日期也不带提醒。
 * 手机与电脑都能改，所以冲突按 updated_at 裁决，rev 由服务端发号。
 */
data class Note(
    val id: String,
    val title: String = "",
    val body: String = "",
    val pinned: Boolean = false,
    val deleted: Boolean = false,
    val updatedAt: Long = System.currentTimeMillis(),
    val rev: Long = 0,
    val dirty: Boolean = false,
) {
    /** 列表第一行：没标题就拿正文首个非空行顶上。 */
    val displayTitle: String
        get() {
            val t = title.trim()
            if (t.isNotEmpty()) return t
            val line = body.lineSequence().firstOrNull { it.isNotBlank() }?.trim()
            return if (line.isNullOrEmpty()) "无标题便签" else line
        }

    /** 列表第二行预览：正文压成一行，过长截断。没写标题时跳过首行，避免和标题重复。 */
    val preview: String
        get() {
            val lines = body.replace("\r", "")
                .split('\n')
                .map { it.trim() }
                .filter { it.isNotEmpty() }
            val start = if (title.isBlank() && lines.isNotEmpty()) 1 else 0
            val text = lines.drop(start).joinToString(" ")
            if (text.isEmpty()) return ""
            return if (text.length <= 80) text else text.take(80) + "…"
        }

    fun toJson(): JSONObject = JSONObject().apply {
        put("id", id)
        put("title", title)
        put("body", body)
        put("pinned", pinned)
        put("deleted", deleted)
        put("updated_at", updatedAt)
        put("rev", rev)
    }

    companion object {
        fun fromJson(o: JSONObject) = Note(
            id = o.optString("id"),
            title = o.optString("title"),
            body = o.optString("body"),
            pinned = o.optBoolean("pinned"),
            deleted = o.optBoolean("deleted"),
            updatedAt = o.optLong("updated_at"),
            rev = o.optLong("rev"),
        )

        fun create(title: String = "", body: String = "") = Note(
            id = Ulid.new(),
            title = title,
            body = body,
            updatedAt = System.currentTimeMillis(),
            dirty = true,
        )
    }
}

/** 时间戳 → 列表里的「今天 14:20 / 11月5日 / 2025年…」，与电脑端同一套口径。 */
fun noteStamp(updatedAt: Long): String {
    if (updatedAt <= 0) return ""
    val then = java.util.Calendar.getInstance().apply { timeInMillis = updatedAt }
    val now = java.util.Calendar.getInstance()
    val hm = String.format("%02d:%02d", then.get(java.util.Calendar.HOUR_OF_DAY), then.get(java.util.Calendar.MINUTE))
    val sameDay = then.get(java.util.Calendar.YEAR) == now.get(java.util.Calendar.YEAR) &&
        then.get(java.util.Calendar.DAY_OF_YEAR) == now.get(java.util.Calendar.DAY_OF_YEAR)
    return when {
        sameDay -> "今天 $hm"
        then.get(java.util.Calendar.YEAR) == now.get(java.util.Calendar.YEAR) ->
            "${then.get(java.util.Calendar.MONTH) + 1}月${then.get(java.util.Calendar.DAY_OF_MONTH)}日"
        else -> "${then.get(java.util.Calendar.YEAR)}年${then.get(java.util.Calendar.MONTH) + 1}月"
    }
}
