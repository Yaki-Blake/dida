package com.didalite.android

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager

/**
 * 全量同步：任务 / 闹钟 / 便签 一次推拉完。
 * 便签页和闹钟页右上角的「同步」都走这里，不区分功能。
 */
object SyncEngine {

    fun run(context: Context, db: Db): String {
        val base = Api.normalize(db.getMeta("server_url") ?: Api.DefaultServerUrl)
        if (base.isNullOrEmpty()) throw IllegalStateException("先到「设置」里填服务器地址")
        val token = db.getMeta("token") ?: throw IllegalStateException(
            if (!db.getMeta("user").isNullOrEmpty()) "登录已失效，去「设置」补个密码"
            else "先到「设置」里登入"
        )

        // 系统日历里新写的日程先变成本地任务（标脏），这一轮全量快照才带得上去
        val imported = importCalendarQuietly(context, db)
        val cal = CalendarSync.scan(context, db)
        val dirty = db.allIncludingDeleted()
        val alarmOps = db.alarmOps()
        val dirtyNotes = db.allNotesIncludingDeleted()
        val sentAt = dirty.associate { it.id to it.updatedAt }
        val sentRev = dirty.associate { it.id to it.rev }
        val sentNoteAt = dirtyNotes.associate { it.id to it.updatedAt }
        val sentNoteRev = dirtyNotes.associate { it.id to it.rev }
        val beforeAlarms = db.alarms()
        val beforeNotes = db.notes().associate { it.id to it.updatedAt }

        val payload = Api.push(
            base, token, db.cursor(), dirty, alarmOps,
            notes = dirtyNotes, noteSince = db.noteCursor(),
        )
        payload.accepted.forEach { (id, rev) -> db.markSynced(id, rev, sentAt[id]) }
        payload.noteAccepted.forEach { (id, rev) -> db.markNoteSynced(id, rev, sentNoteAt[id]) }
        payload.rejected.forEach { db.applyServer(it) }
        payload.tasks.forEach { db.applyServer(it) }
        db.setCursor(payload.maxRev)
        db.replaceAlarms(payload.alarms)
        payload.notes.forEach { db.applyServerNote(it) }
        if (payload.noteMaxRev > 0) db.setNoteCursor(payload.noteMaxRev)
        db.clearAlarmOps()

        ReminderScheduler.rescheduleAll(context)
        writeCalendarQuietly(context, db)

        val uploaded = payload.accepted.count { (id, rev) -> sentRev[id] != rev }
        val noteUploaded = payload.noteAccepted.count { (id, rev) -> sentNoteRev[id] != rev }
        val afterNotes = db.notes().associate { it.id to it.updatedAt }
        val noteChanges = (beforeNotes.keys + afterNotes.keys).count { beforeNotes[it] != afterNotes[it] }
        val alarmChanged = beforeAlarms.sortedBy { it.id } != payload.alarms.sortedBy { it.id }

        val sb = StringBuilder()
        if (uploaded == 0 && payload.tasks.isEmpty() && alarmOps.isEmpty() && !alarmChanged &&
            payload.rejected.isEmpty() && dirtyNotes.isEmpty() && noteChanges == 0
        ) {
            sb.append("已是最新，没有变化")
        } else {
            sb.append("上传任务 $uploaded 条，下载任务 ${payload.tasks.size} 条")
            if (imported > 0) sb.append("，日历导入 $imported 条")
            if (alarmOps.isNotEmpty()) sb.append("，闹钟改动 ${alarmOps.size} 个")
            if (alarmChanged) sb.append("，闹钟 ${payload.alarms.size} 个")
            if (dirtyNotes.isNotEmpty() || noteChanges > 0) sb.append("，便签 上传 $noteUploaded / 下载 $noteChanges 条")
        }
        db.setMeta("last_sync_at", System.currentTimeMillis().toString())
        return sb.toString()
    }

    private fun importCalendarQuietly(context: Context, db: Db): Int {
        if (context.checkSelfPermission(Manifest.permission.READ_CALENDAR)
            != PackageManager.PERMISSION_GRANTED
        ) return 0
        return try {
            SystemCalendar.importInto(db, SystemCalendar.read(context))
        } catch (e: Exception) {
            0
        }
    }

    private fun writeCalendarQuietly(context: Context, db: Db) {
        if (!CalendarWriter.hasPermission(context)) return
        runCatching { CalendarWriter.sync(context, db) }
    }
}
