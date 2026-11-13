package com.didalite.android

import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.database.sqlite.SQLiteDatabase
import android.database.sqlite.SQLiteOpenHelper
import org.json.JSONArray

/** 手机端本地库：结构对齐开发计划书 3.2，每端一份完整副本，断网也能看。 */
class Db(context: Context) : SQLiteOpenHelper(context.applicationContext, "dida.db", null, 3) {

    override fun onCreate(db: SQLiteDatabase) {
        db.execSQL(
            """
            CREATE TABLE IF NOT EXISTS task (
              id          TEXT PRIMARY KEY,
              title       TEXT NOT NULL,
              note        TEXT,
              location    TEXT,
              start_at    INTEGER,
              due_at      INTEGER,
              all_day     INTEGER NOT NULL DEFAULT 0,
              remind_at   INTEGER,
              repeat_rule TEXT NOT NULL DEFAULT 'none',
              done        INTEGER NOT NULL DEFAULT 0,
              deleted     INTEGER NOT NULL DEFAULT 0,
              updated_at  INTEGER NOT NULL,
              rev         INTEGER NOT NULL DEFAULT 0,
              dirty       INTEGER NOT NULL DEFAULT 0,
              reminded_at INTEGER
            )
            """.trimIndent()
        )
        db.execSQL("CREATE INDEX IF NOT EXISTS idx_task_rev ON task(rev)")
        db.execSQL("CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT NOT NULL)")
        createCalendarLink(db)
        createAlarm(db)
    }

    override fun onUpgrade(db: SQLiteDatabase, oldVersion: Int, newVersion: Int) {
        createCalendarLink(db)
        createAlarm(db)
    }

    /** 独立闹钟：只从电脑端「闹钟」页同步过来，手机端只读，不与任务混在一起。 */
    private fun createAlarm(db: SQLiteDatabase) {
        db.execSQL(
            """
            CREATE TABLE IF NOT EXISTS alarm (
              id          TEXT PRIMARY KEY,
              label       TEXT,
              hour        INTEGER NOT NULL,
              minute      INTEGER NOT NULL,
              repeat_rule TEXT NOT NULL DEFAULT 'daily',
              once_at     INTEGER,
              enabled     INTEGER NOT NULL DEFAULT 1
            )
            """.trimIndent()
        )
    }

    /** 任务与系统日历事件的对应关系：只在「单向写日历」这一侧使用。 */
    private fun createCalendarLink(db: SQLiteDatabase) {
        db.execSQL(
            "CREATE TABLE IF NOT EXISTS calendar_link (" +
                "task_id TEXT PRIMARY KEY, event_id INTEGER NOT NULL)"
        )
    }

    /** 列表展示用：未删除的，按锚点时间升序。 */
    fun all(): List<Task> = readableDatabase.query(
        "task", null, "deleted = 0", null, null, null,
        "COALESCE(start_at, due_at, 9223372036854775807) ASC"
    ).use { c -> buildList<Task> { while (c.moveToNext()) add(c.toTask()) } }

    /** 待上传的本地改动。 */
    fun dirty(): List<Task> = readableDatabase.query(
        "task", null, "dirty = 1", null, null, null, "updated_at ASC"
    ).use { c -> buildList<Task> { while (c.moveToNext()) add(c.toTask()) } }

    fun find(id: String): Task? = readableDatabase
        .query("task", null, "id = ?", arrayOf(id), null, null, null)
        .use { c -> if (c.moveToFirst()) c.toTask() else null }

    /** 本地写入：日历导入、本地编辑都走这里，标记待上传。 */
    fun saveLocal(task: Task) = writableDatabase.insertWithOnConflict(
        "task", null, task.toValues(dirty = true), SQLiteDatabase.CONFLICT_REPLACE
    )

    /** 服务端版本无条件覆盖本地（4.2 冲突规则第 3 条）。 */
    fun applyServer(task: Task) {
        writableDatabase.insertWithOnConflict(
            "task", null, task.toValues(dirty = false), SQLiteDatabase.CONFLICT_REPLACE
        )
    }

    /** accepted 之后清 dirty，但只有推送期间本地没再改过（updated_at 未变）才清。 */
    fun markSynced(id: String, rev: Long, sentUpdatedAt: Long?) {
        if (sentUpdatedAt == null) return
        writableDatabase.execSQL(
            "UPDATE task SET rev = ?, dirty = 0 WHERE id = ? AND updated_at = ?",
            arrayOf(rev, id, sentUpdatedAt)
        )
    }

    /** 闹钟列表：电脑端是唯一来源，按「时:分」排序。 */
    fun alarms(): List<Alarm> = readableDatabase
        .query("alarm", null, null, null, null, null, "hour, minute")
        .use { c ->
            buildList {
                while (c.moveToNext()) add(
                    Alarm(
                        id = c.getString(c.getColumnIndexOrThrow("id")),
                        label = c.getStringOrNull("label") ?: "",
                        hour = c.getInt(c.getColumnIndexOrThrow("hour")),
                        minute = c.getInt(c.getColumnIndexOrThrow("minute")),
                        repeatRule = c.getStringOrNull("repeat_rule") ?: "daily",
                        onceAt = c.getLongOrNull("once_at"),
                        enabled = c.getInt(c.getColumnIndexOrThrow("enabled")) != 0,
                    )
                )
            }
        }

    /** 整批替换：闹钟以电脑端为准，删掉电脑上已删除的，更新改过的。 */
    fun replaceAlarms(items: List<Alarm>) {
        val db = writableDatabase
        db.beginTransaction()
        try {
            db.delete("alarm", null, null)
            items.forEach { alarm ->
                db.insertWithOnConflict(
                    "alarm", null,
                    ContentValues().apply {
                        put("id", alarm.id)
                        put("label", alarm.label)
                        put("hour", alarm.hour)
                        put("minute", alarm.minute)
                        put("repeat_rule", alarm.repeatRule)
                        put("once_at", alarm.onceAt)
                        put("enabled", if (alarm.enabled) 1 else 0)
                    },
                    SQLiteDatabase.CONFLICT_REPLACE
                )
            }
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    /** 手机端停用 / 启用一条闹钟：先改本地，改动攒着等下次同步带给电脑端。 */
    fun setAlarmEnabled(id: String, enabled: Boolean) {
        writableDatabase.execSQL(
            "UPDATE alarm SET enabled = ? WHERE id = ?",
            arrayOf(if (enabled) 1 else 0, id)
        )
        addAlarmOp(AlarmOp(id, enabled = enabled))
    }

    /** 手机端删除：本机列表立刻消失，下次同步让电脑端也删掉。 */
    fun deleteAlarm(id: String) = deleteAlarms(listOf(id))

    /** 批量删除：逐条改本地表，改动攒着等下次同步。 */
    fun deleteAlarms(ids: List<String>) {
        if (ids.isEmpty()) return
        val db = writableDatabase
        db.beginTransaction()
        try {
            ids.forEach { id ->
                db.delete("alarm", "id = ?", arrayOf(id))
                addAlarmOp(AlarmOp(id, deleted = true))
            }
            db.setTransactionSuccessful()
        } finally {
            db.endTransaction()
        }
    }

    /** 还没上报的闹钟改动。 */
    fun alarmOps(): List<AlarmOp> {
        val raw = getMeta("alarm_ops") ?: return emptyList()
        return runCatching {
            val array = JSONArray(raw)
            buildList { for (i in 0 until array.length()) add(AlarmOp.fromJson(array.getJSONObject(i))) }
        }.getOrDefault(emptyList())
    }

    fun clearAlarmOps() = setMeta("alarm_ops", null)

    /** 同一个 id 只留最后一次改动：先停用再删除，就只发删除。 */
    private fun addAlarmOp(op: AlarmOp) {
        val ops = alarmOps().filterNot { it.id == op.id }.toMutableList()
        ops.add(op)
        val array = JSONArray()
        ops.forEach { array.put(it.toJson()) }
        setMeta("alarm_ops", array.toString())
    }

    /** 已经写进系统日历的任务 → 事件 id。 */
    fun calendarLinks(): Map<String, Long> = readableDatabase
        .query("calendar_link", arrayOf("task_id", "event_id"), null, null, null, null, null)
        .use { c -> buildMap { while (c.moveToNext()) put(c.getString(0), c.getLong(1)) } }

    fun linkCalendar(taskId: String, eventId: Long) {
        writableDatabase.insertWithOnConflict(
            "calendar_link", null,
            ContentValues().apply { put("task_id", taskId); put("event_id", eventId) },
            SQLiteDatabase.CONFLICT_REPLACE
        )
    }

    fun unlinkCalendar(taskId: String) {
        writableDatabase.delete("calendar_link", "task_id = ?", arrayOf(taskId))
    }

    fun getMeta(key: String): String? = readableDatabase
        .query("meta", arrayOf("v"), "k = ?", arrayOf(key), null, null, null)
        .use { c -> if (c.moveToFirst()) c.getString(0) else null }

    fun setMeta(key: String, value: String?) {
        if (value == null) {
            writableDatabase.delete("meta", "k = ?", arrayOf(key))
        } else {
            writableDatabase.insertWithOnConflict(
                "meta", null,
                ContentValues().apply { put("k", key); put("v", value) },
                SQLiteDatabase.CONFLICT_REPLACE
            )
        }
    }

    /** 同步游标：上一轮拿到的最大 rev。 */
    fun cursor(): Long = getMeta("cursor")?.toLongOrNull() ?: 0L

    fun setCursor(rev: Long) = setMeta("cursor", rev.toString())

    private fun Cursor.toTask() = Task(
        id = getString(getColumnIndexOrThrow("id")),
        title = getString(getColumnIndexOrThrow("title")),
        note = getStringOrNull("note"),
        location = getStringOrNull("location"),
        startAt = getLongOrNull("start_at"),
        dueAt = getLongOrNull("due_at"),
        allDay = getInt(getColumnIndexOrThrow("all_day")) != 0,
        remindAt = getLongOrNull("remind_at"),
        repeatRule = getStringOrNull("repeat_rule") ?: "none",
        done = getInt(getColumnIndexOrThrow("done")) != 0,
        deleted = getInt(getColumnIndexOrThrow("deleted")) != 0,
        updatedAt = getLong(getColumnIndexOrThrow("updated_at")),
        rev = getLong(getColumnIndexOrThrow("rev")),
        dirty = getInt(getColumnIndexOrThrow("dirty")) != 0,
    )

    private fun Cursor.getStringOrNull(column: String): String? {
        val i = getColumnIndexOrThrow(column)
        return if (isNull(i)) null else getString(i)
    }

    private fun Cursor.getLongOrNull(column: String): Long? {
        val i = getColumnIndexOrThrow(column)
        return if (isNull(i)) null else getLong(i)
    }

    private fun Task.toValues(dirty: Boolean) = ContentValues().apply {
        put("id", id)
        put("title", title)
        put("note", note)
        put("location", location)
        put("start_at", startAt)
        put("due_at", dueAt)
        put("all_day", if (allDay) 1 else 0)
        put("remind_at", remindAt)
        put("repeat_rule", repeatRule)
        put("done", if (done) 1 else 0)
        put("deleted", if (deleted) 1 else 0)
        put("updated_at", updatedAt)
        put("rev", rev)
        put("dirty", if (dirty) 1 else 0)
    }
}
