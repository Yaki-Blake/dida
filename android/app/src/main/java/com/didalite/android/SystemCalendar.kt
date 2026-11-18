package com.didalite.android

import android.content.ContentResolver
import android.content.ContentUris
import android.content.ContentValues
import android.content.Context
import android.content.pm.PackageManager
import android.provider.CalendarContract
import android.provider.CalendarContract.Calendars
import android.provider.CalendarContract.Events
import android.provider.CalendarContract.Reminders
import java.util.Calendar
import java.util.TimeZone

/** 读取系统日历：近 30 天到未来一年的事件，附带每个事件最早的一条提醒。 */
object SystemCalendar {

    /** 写进系统日历时打的来源标记，读回来时据此跳过，避免自己写的事件又被导入成新任务。 */
    const val OWNER_PACKAGE = "com.didalite.android"

    data class Event(
        val eventId: Long,
        val title: String,
        val note: String?,
        val location: String?,
        val startAt: Long,
        val endAt: Long?,
        val allDay: Boolean,
        val remindAt: Long?,
    )

    /**
     * 事件清单里的一行：比 [Event] 多了「已被删掉」和「是不是我们自己写的」两个标记。
     */
    data class EventRow(
        val eventId: Long,
        val title: String,
        val startAt: Long,
        val deleted: Boolean,
        val own: Boolean,
    )

    fun read(context: Context): List<Event> {
        val now = System.currentTimeMillis()
        val begin = now - 30L * 24 * 60 * 60 * 1000
        val end = now + 365L * 24 * 60 * 60 * 1000

        val projection = arrayOf(
            CalendarContract.Events._ID,
            CalendarContract.Events.TITLE,
            CalendarContract.Events.DESCRIPTION,
            CalendarContract.Events.EVENT_LOCATION,
            CalendarContract.Events.DTSTART,
            CalendarContract.Events.DTEND,
            CalendarContract.Events.ALL_DAY,
        )
        val selection =
            "${CalendarContract.Events.DTSTART} BETWEEN ? AND ? AND ${CalendarContract.Events.DELETED} = 0" +
                " AND (${CalendarContract.Events.CUSTOM_APP_PACKAGE} IS NULL" +
                " OR ${CalendarContract.Events.CUSTOM_APP_PACKAGE} <> ?)"

        val events = mutableListOf<Event>()
        context.contentResolver.query(
            CalendarContract.Events.CONTENT_URI,
            projection,
            selection,
            arrayOf(begin.toString(), end.toString(), OWNER_PACKAGE),
            "${CalendarContract.Events.DTSTART} ASC"
        )?.use { c ->
            while (c.moveToNext()) {
                val id = c.getLong(0)
                val start = c.getLong(4)
                events.add(
                    Event(
                        eventId = id,
                        title = c.getString(1) ?: "（无标题）",
                        note = c.getString(2),
                        location = c.getString(3),
                        startAt = start,
                        endAt = if (c.isNull(5)) null else c.getLong(5),
                        allDay = c.getInt(6) != 0,
                        remindAt = firstReminder(context, id, start),
                    )
                )
            }
        }
        return events
    }

    /**
     * 全部事件的清单，用来核对「这条任务对应的事件还在不在」。
     * 不按时间过滤，也不排除我们自己写的事件——两件事都要看。
     * 读不到（没权限、日历应用挂了）就返回 null，调用方据此放弃本轮判断，一条也不删。
     */
    fun inventory(context: Context): List<EventRow>? = try {
        val projection = arrayOf(
            CalendarContract.Events._ID,
            CalendarContract.Events.TITLE,
            CalendarContract.Events.DTSTART,
            CalendarContract.Events.DELETED,
            CalendarContract.Events.CUSTOM_APP_PACKAGE,
        )
        context.contentResolver.query(
            CalendarContract.Events.CONTENT_URI,
            projection,
            null,
            null,
            null,
        )?.use { c ->
            buildList {
                while (c.moveToNext()) {
                    add(
                        EventRow(
                            eventId = c.getLong(0),
                            title = c.getString(1) ?: "",
                            startAt = if (c.isNull(2)) 0L else c.getLong(2),
                            deleted = !c.isNull(3) && c.getInt(3) != 0,
                            own = !c.isNull(4) && c.getString(4) == OWNER_PACKAGE,
                        )
                    )
                }
            }
        }
    } catch (e: Exception) {
        null
    }

    /** 提醒的分钟数是「事件开始前多少分钟」，换算成绝对 UTC 毫秒。 */
    private fun firstReminder(context: Context, eventId: Long, startAt: Long): Long? {
        context.contentResolver.query(
            CalendarContract.Reminders.CONTENT_URI,
            arrayOf(CalendarContract.Reminders.MINUTES),
            "${CalendarContract.Reminders.EVENT_ID} = ?",
            arrayOf(eventId.toString()),
            "${CalendarContract.Reminders.MINUTES} ASC"
        )?.use { c -> if (c.moveToFirst()) return startAt - c.getLong(0) * 60_000L }
        return null
    }

    // ---------- 任务 id 与事件号的对应 ----------

    /**
     * 任务 id 一律 26 位、C 开头、后面全数字，对齐服务端 CHAR(26)。
     *
     * 新编码把事件号**前面**补零：C000…003。事件号 3 和 30 不会撞成同一个 id。
     * 旧版本是**后面**补零（C300…0），3 和 30 会撞车，老任务还留着那种 id，
     * 所以认领老任务时要用 [legacyEncode]，新建任务一律用 [encode]。
     */
    fun encode(eventId: Long): String = "C" + eventId.toString().padStart(25, '0')

    /** 旧编码：事件号后面补零。只用来认领老任务，不再生成新的。 */
    fun legacyEncode(eventId: Long): String = "C" + eventId.toString().padEnd(25, '0')

    /** 是不是从系统日历导入出来的任务：C 开头 + 后 25 位全数字。 */
    fun isCalendarOrigin(id: String): Boolean =
        id.length == 26 && id[0] == 'C' && id.drop(1).all { it in '0'..'9' }

    /** 是不是旧编码的任务：C 后面第一位不是 0（补零在前面的新编码第一位一定是 0）。 */
    fun isLegacyId(id: String): Boolean = isCalendarOrigin(id) && id[1] != '0'

    /** 新编码的任务 id 反推事件号：去掉 C 和前导 0 就是事件号。旧编码反推不唯一，不给算。 */
    fun decodeEventId(id: String): Long? {
        if (!isCalendarOrigin(id) || isLegacyId(id)) return null
        return id.drop(1).trimStart('0').toLongOrNull()
    }

    /** 事件转本地任务：id 用 [encode]，对齐服务端 CHAR(26)。 */
    fun toTask(e: Event): Task = Task(
        id = encode(e.eventId),
        title = e.title,
        note = e.note,
        location = e.location,
        startAt = e.startAt,
        dueAt = e.endAt,
        allDay = e.allDay,
        remindAt = e.remindAt,
        updatedAt = System.currentTimeMillis(),
        dirty = true,
    )

    /**
     * 导入：新事件插入；已存在且日历侧有变化才覆盖；墓碑不复活，避免删了又被读回来。
     * 每导入一条就登记「任务 ↔ 事件」的对应关系，之后日历里删掉事件才认得出该删哪条任务。
     */
    fun importInto(db: Db, events: List<Event>): Int {
        var changed = 0
        val byEvent = db.calendarLinks().entries.associate { (taskId, eventId) -> eventId to taskId }
        // 旧编码会让事件号 3 和 30 撞成同一个 id，撞车的一批只能靠标题和时间认领
        val legacyGroups = events.groupBy { legacyEncode(it.eventId) }
        for (e in events) {
            val task = toTask(e)
            val old = resolveTask(db, e, byEvent, legacyGroups)
            if (old == null) {
                db.saveLocal(task)
                db.linkCalendar(task.id, e.eventId)
                changed++
                continue
            }
            if (old.deleted) continue
            val same = old.title == task.title &&
                old.startAt == task.startAt &&
                old.dueAt == task.dueAt &&
                old.remindAt == task.remindAt &&
                old.location == task.location &&
                old.allDay == task.allDay
            if (same) continue
            db.saveLocal(task.copy(id = old.id, rev = old.rev, updatedAt = System.currentTimeMillis()))
            db.linkCalendar(old.id, e.eventId)
            changed++
        }
        return changed
    }

    /**
     * 找这条事件在本地对应哪条任务：先看链接表，再看有没有旧编码的老任务，最后看新编码。
     * 认不出来就返回 null（新建一条），宁可多一条也不把别人的日程改掉。
     */
    private fun resolveTask(
        db: Db,
        e: Event,
        byEvent: Map<Long, String>,
        legacyGroups: Map<String, List<Event>>,
    ): Task? {
        byEvent[e.eventId]?.let { id -> db.find(id)?.let { return it } }
        val legacyId = legacyEncode(e.eventId)
        if (!isLegacyId(legacyId)) return null
        val old = db.find(legacyId) ?: return db.find(encode(e.eventId))
        val group = legacyGroups[legacyId].orEmpty()
        if (group.size > 1) {
            // 撞车了：只有标题和时间都对得上，才算这条老任务就是这条事件
            val hit = group.filter { it.title == old.title && it.startAt == old.startAt }
            if (hit.size != 1 || hit[0].eventId != e.eventId) return null
        }
        return old
    }
}

/**
 * 单向写系统日历：电脑端同步下来的任务写进手机日历，改了就更新、删了就删事件。
 * 不做反向回写——日历里改的内容不会回到任务，避免两头改出冲突。
 */
object CalendarWriter {

    data class CalendarInfo(val id: Long, val name: String)

    data class Result(
        val inserted: Int,
        val updated: Int,
        val removed: Int,
        val error: String? = null,
    ) {
        fun describe(): String = error ?: "写入日历：新增 $inserted 条，更新 $updated 条，删除 $removed 条"
    }

    private const val DAY_MS = 24L * 60 * 60 * 1000
    private const val DEFAULT_DURATION_MS = 30L * 60 * 1000
    private const val MAX_REMIND_MINUTES = 40320L

    private data class Snapshot(
        val eventId: Long,
        val title: String?,
        val note: String?,
        val location: String?,
        val start: Long,
        val end: Long,
        val allDay: Boolean,
        val reminderMinutes: Long?,
    ) {
        fun matches(task: Task, startAt: Long): Boolean {
            val (start, end) = span(task, startAt)
            return title.orEmpty() == task.title &&
                note.orEmpty() == task.note.orEmpty() &&
                location.orEmpty() == task.location.orEmpty() &&
                start == this.start && end == this.end && allDay == task.allDay &&
                reminderMinutes == reminderMinutesFor(startAt, task.remindAt)
        }
    }

    fun hasPermission(context: Context): Boolean =
        context.checkSelfPermission(android.Manifest.permission.WRITE_CALENDAR) ==
            PackageManager.PERMISSION_GRANTED

    /** 可写日历：可见且至少「可修改」。没有权限时直接返回空，避免查询抛 SecurityException。 */
    fun writableCalendars(context: Context): List<CalendarInfo> {
        if (!hasPermission(context)) return emptyList()
        return context.contentResolver.query(
            Calendars.CONTENT_URI,
            arrayOf(Calendars._ID, Calendars.CALENDAR_DISPLAY_NAME),
            "${Calendars.VISIBLE} = 1 AND ${Calendars.CALENDAR_ACCESS_LEVEL} >= ?",
            arrayOf(Calendars.CAL_ACCESS_CONTRIBUTOR.toString()),
            "${Calendars.IS_PRIMARY} DESC, ${Calendars._ID} ASC",
        )?.use { c ->
            buildList { while (c.moveToNext()) add(CalendarInfo(c.getLong(0), c.getString(1) ?: "日历")) }
        } ?: emptyList()
    }

    /** 用设置里选过的日历，没选过就取第一个并记下来。 */
    fun chosenId(context: Context, db: Db): Long? {
        val list = writableCalendars(context)
        val saved = db.getMeta("calendar_id")?.toLongOrNull()
        if (saved != null && list.any { it.id == saved }) return saved
        val fallback = list.firstOrNull() ?: return null
        db.setMeta("calendar_id", fallback.id.toString())
        return fallback.id
    }

    fun sync(context: Context, db: Db): Result {
        if (!hasPermission(context)) return Result(0, 0, 0, "没有日历写入权限")
        val calendarId = chosenId(context, db) ?: return Result(0, 0, 0, "手机里没有可写的日历")
        val resolver = context.contentResolver
        val links = db.calendarLinks()
        var inserted = 0
        var updated = 0
        var removed = 0

        // 任务已删除（墓碑）或本地已不存在：连日历事件一起删掉
        for ((taskId, eventId) in links) {
            // 从日历导入的任务，事件是用户自己的，不归我们删——它自己没了就是用户删的
            if (isCalendarOrigin(taskId)) continue
            val task = db.find(taskId)
            if (task != null && !task.deleted) continue
            resolver.delete(ContentUris.withAppendedId(Events.CONTENT_URI, eventId), null, null)
            db.unlinkCalendar(taskId)
            removed++
        }

        val now = System.currentTimeMillis()
        for (task in db.all()) {
            // 从系统日历导入的任务本来就有事件，不重复写
            if (isCalendarOrigin(task.id)) continue
            val startAt = task.startAt ?: task.dueAt ?: task.remindAt ?: continue
            // 只写今天以后（含昨天）的，避免一上来把陈年日程铺满日历
            if (startAt < now - DAY_MS) continue

            val snapshot = links[task.id]?.let { readEvent(resolver, it) }
            if (snapshot == null) {
                val newId = insertEvent(resolver, calendarId, task, startAt) ?: continue
                writeReminder(resolver, newId, startAt, task.remindAt)
                db.linkCalendar(task.id, newId)
                inserted++
            } else if (!snapshot.matches(task, startAt)) {
                updateEvent(resolver, snapshot.eventId, task, startAt)
                resolver.delete(
                    Reminders.CONTENT_URI,
                    "${Reminders.EVENT_ID} = ?",
                    arrayOf(snapshot.eventId.toString()),
                )
                writeReminder(resolver, snapshot.eventId, startAt, task.remindAt)
                updated++
            }
        }
        return Result(inserted, updated, removed)
    }

    // ---------- 内部 ----------

    /** 从系统日历导入的任务不再往日历里写：它本来就有事件。 */
    private fun isCalendarOrigin(id: String): Boolean = SystemCalendar.isCalendarOrigin(id)

    /** 全天事件要落在整天上：按本地日期取当天 0 点，再换成 UTC 的 0 点。 */
    private fun span(task: Task, startAt: Long): Pair<Long, Long> {
        if (!task.allDay) {
            val end = task.dueAt?.takeIf { it > startAt } ?: (startAt + DEFAULT_DURATION_MS)
            return startAt to end
        }
        val local = Calendar.getInstance().apply { timeInMillis = startAt }
        val utc = Calendar.getInstance(TimeZone.getTimeZone("UTC")).apply {
            clear()
            set(
                local.get(Calendar.YEAR),
                local.get(Calendar.MONTH),
                local.get(Calendar.DAY_OF_MONTH),
                0, 0, 0,
            )
        }
        val start = utc.timeInMillis
        return start to start + DAY_MS
    }

    /** 日历提醒是「事件开始前多少分钟」。 */
    private fun reminderMinutesFor(startAt: Long, remindAt: Long?): Long? {
        if (remindAt == null) return null
        val diff = startAt - remindAt
        if (diff <= 0) return 0L
        return (diff / 60_000L).coerceAtMost(MAX_REMIND_MINUTES)
    }

    private fun readEvent(resolver: ContentResolver, eventId: Long): Snapshot? =
        resolver.query(
            ContentUris.withAppendedId(Events.CONTENT_URI, eventId),
            arrayOf(
                Events._ID,
                Events.TITLE,
                Events.DESCRIPTION,
                Events.EVENT_LOCATION,
                Events.DTSTART,
                Events.DTEND,
                Events.ALL_DAY,
            ),
            null, null, null,
        )?.use { c ->
            if (!c.moveToFirst()) return null
            Snapshot(
                eventId = c.getLong(0),
                title = if (c.isNull(1)) null else c.getString(1),
                note = if (c.isNull(2)) null else c.getString(2),
                location = if (c.isNull(3)) null else c.getString(3),
                start = c.getLong(4),
                end = if (c.isNull(5)) c.getLong(4) else c.getLong(5),
                allDay = c.getInt(6) != 0,
                reminderMinutes = readReminderMinutes(resolver, eventId),
            )
        }

    private fun readReminderMinutes(resolver: ContentResolver, eventId: Long): Long? =
        resolver.query(
            Reminders.CONTENT_URI,
            arrayOf(Reminders.MINUTES),
            "${Reminders.EVENT_ID} = ?",
            arrayOf(eventId.toString()),
            "${Reminders.MINUTES} ASC",
        )?.use { c -> if (c.moveToFirst()) c.getLong(0) else null }

    private fun insertEvent(
        resolver: ContentResolver,
        calendarId: Long,
        task: Task,
        startAt: Long,
    ): Long? {
        val values = eventValues(task, startAt).apply { put(Events.CALENDAR_ID, calendarId) }
        val uri = resolver.insert(Events.CONTENT_URI, values) ?: return null
        return ContentUris.parseId(uri)
    }

    private fun updateEvent(
        resolver: ContentResolver,
        eventId: Long,
        task: Task,
        startAt: Long,
    ) {
        resolver.update(
            ContentUris.withAppendedId(Events.CONTENT_URI, eventId),
            eventValues(task, startAt),
            null, null,
        )
    }

    private fun eventValues(task: Task, startAt: Long) = ContentValues().apply {
        val (start, end) = span(task, startAt)
        put(Events.TITLE, task.title)
        put(Events.DESCRIPTION, task.note)
        put(Events.EVENT_LOCATION, task.location)
        put(Events.DTSTART, start)
        put(Events.DTEND, end)
        put(Events.ALL_DAY, if (task.allDay) 1 else 0)
        put(Events.EVENT_TIMEZONE, if (task.allDay) "UTC" else TimeZone.getDefault().id)
        put(Events.CUSTOM_APP_PACKAGE, SystemCalendar.OWNER_PACKAGE)
        put(Events.HAS_ALARM, if (task.remindAt != null) 1 else 0)
    }

    private fun writeReminder(
        resolver: ContentResolver,
        eventId: Long,
        startAt: Long,
        remindAt: Long?,
    ) {
        val minutes = reminderMinutesFor(startAt, remindAt) ?: return
        val values = ContentValues().apply {
            put(Reminders.EVENT_ID, eventId)
            put(Reminders.MINUTES, minutes)
            put(Reminders.METHOD, Reminders.METHOD_ALERT)
        }
        runCatching { resolver.insert(Reminders.CONTENT_URI, values) }
    }
}
