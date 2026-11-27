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
        // 服务端每次回的都是**全量快照**（它不看 since），所以 payload.tasks 恒等于云端总数。
        // 「下载」只能按本地更新前后的 updated_at 差来算，否则每条都报「下载 146 条」，
        // 用户会以为一直在重复下载。
        val downloaded = payload.tasks.count { sentAt[it.id] != it.updatedAt } +
            payload.rejected.count { sentAt[it.id] != it.updatedAt }

        val sb = StringBuilder()
        // dirtyNotes 是「全量便签快照」，恒非空，不能拿它判断有没有便签变化——
        // 否则每次都多报一行「便签 上传 0 / 下载 0 条」。
        val noteTouched = noteUploaded > 0 || noteChanges > 0
        if (uploaded == 0 && downloaded == 0 && alarmOps.isEmpty() && !alarmChanged &&
            payload.rejected.isEmpty() && !noteTouched && imported == 0 && cal.removed == 0
        ) {
            sb.append("已是最新，没有变化")
        } else {
            sb.append("上传任务 $uploaded 条，下载任务 $downloaded 条")
            if (imported > 0) sb.append("，日历导入 $imported 条")
            if (cal.removed > 0) sb.append("，日历删除 ${cal.removed} 条")
            if (alarmOps.isNotEmpty()) sb.append("，闹钟改动 ${alarmOps.size} 个")
            if (alarmChanged) sb.append("，闹钟 ${payload.alarms.size} 个")
            if (noteTouched) sb.append("，便签 上传 $noteUploaded / 下载 $noteChanges 条")
        }
        // 日历权限没给时导入会被静默跳过，不说明白用户只会看到「没有变化」，误以为同步坏了
        if (!hasCalendarPermission(context)) {
            sb.append("\n没有「日历」权限，读不到手机日历里的日程。去「设置 → 系统日历」授权后再同步。")
        }
        db.setMeta("last_sync_at", System.currentTimeMillis().toString())
        return sb.toString()
    }

    /**
     * 打开 App 时在后台悄悄同步一轮：日历里新写的日程不用再去「专注」页翻同步按钮。
     *
     * 三条限制，避免帮倒忙：
     * ① 没登入直接返回（不弹错、不写 last_sync_at）；
     * ② 30 秒内已经自动同步过就跳过，防止来回切页把服务端刷爆；
     * ③ 任何异常都吞掉——自动同步失败不该打扰用户，手动点同步时才会看到报错。
     */
    fun autoRun(context: Context, db: Db) {
        if (db.getMeta("token").isNullOrEmpty()) return
        val now = System.currentTimeMillis()
        val last = db.getMeta(AUTO_SYNC_AT)?.toLongOrNull() ?: 0L
        if (now - last < AUTO_SYNC_GAP_MS) return
        db.setMeta(AUTO_SYNC_AT, now.toString())
        runCatching { run(context, db) }
    }

    private const val AUTO_SYNC_AT = "last_auto_sync_at"
    private const val AUTO_SYNC_GAP_MS = 30_000L

    private fun hasCalendarPermission(context: Context): Boolean =
        context.checkSelfPermission(Manifest.permission.READ_CALENDAR) ==
            PackageManager.PERMISSION_GRANTED

    private fun importCalendarQuietly(context: Context, db: Db): Int {
        if (!hasCalendarPermission(context)) return 0
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
