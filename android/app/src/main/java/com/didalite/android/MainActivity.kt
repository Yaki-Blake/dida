package com.didalite.android

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

/**
 * 手机端同步面板：右上角「同步」，下面列出这一次具体新上传了哪些、新下载了哪些。
 * 界面全部用原生 View，零第三方依赖。
 */
class MainActivity : Activity() {

    private data class ChangeRow(val arrow: String, val title: String, val color: Int)

    private data class Outcome(val summary: String, val rows: List<ChangeRow>)

    private lateinit var db: Db
    private lateinit var statusView: TextView
    private lateinit var stampView: TextView
    private lateinit var permHint: TextView
    private lateinit var summaryView: TextView
    private lateinit var resultTime: TextView
    private lateinit var changeBox: LinearLayout
    private val actionButtons = mutableListOf<TextView>()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        db = Db(this)
        setContentView(buildUi())
        applyDidaTheme()
        // 启动时按本地库重排提醒，不依赖网络
        Thread { ReminderScheduler.rescheduleAll(applicationContext) }.start()
    }

    override fun onResume() {
        super.onResume()
        updateStatus()
    }

    // ---------- 界面 ----------

    private fun buildUi(): View {
        val root = ScrollView(this).apply {
            isFillViewport = true
            setBackgroundColor(Ui.PAPER)
        }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), statusBarHeight() + dp(10), dp(20), dp(28))
        }

        val titleRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        titleRow.addView(TextView(this).apply {
            text = "滴答"
            textSize = 22f
            typeface = Ui.display
            setTextColor(Ui.INK)
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        titleRow.addView(pill("专注", small = true).apply {
            setOnClickListener {
                startActivity(
                    Intent(this@MainActivity, FocusActivity::class.java).putExtra("tab", "note")
                )
            }
        }, gapParams(start = 6))
        titleRow.addView(pill("设置", small = true).apply {
            setOnClickListener { openSettings() }
        }, gapParams(start = 6))
        // 「同步」固定在标题行最右端：与便签页、闹钟页保持一致
        titleRow.addView(actionButton("同步", primary = true, small = true) { doSync() }, gapParams(start = 6))
        column.addView(titleRow)

        statusView = TextView(this).apply {
            textSize = 14f
            typeface = Ui.body
            setTextColor(Ui.INK_2)
            setPadding(0, dp(14), 0, 0)
            setOnClickListener { openSettings() }
        }
        column.addView(statusView)

        stampView = TextView(this).apply {
            textSize = 12f
            typeface = Ui.mono
            setTextColor(Ui.MUTED)
            setPadding(0, dp(3), 0, 0)
        }
        column.addView(stampView)

        permHint = TextView(this).apply {
            textSize = 12f
            typeface = Ui.medium
            setTextColor(Ui.WARN)
            setPadding(0, dp(8), 0, 0)
            visibility = View.GONE
            setOnClickListener {
                startActivity(Intent(this@MainActivity, SettingsActivity::class.java))
            }
        }
        column.addView(permHint)

        column.addView(pageRule(topMargin = 28))

        val resultHead = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, dp(14), 0, 0)
        }
        resultHead.addView(TextView(this).apply {
            text = "这次同步"
            textSize = 12f
            typeface = Ui.medium
            setTextColor(Ui.INK_2)
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        resultTime = TextView(this).apply {
            textSize = 12f
            typeface = Ui.mono
            setTextColor(Ui.MUTED)
        }
        resultHead.addView(resultTime)
        column.addView(resultHead)

        summaryView = TextView(this).apply {
            text = "点右上角「同步」，两边的改动会合到一起"
            textSize = 14f
            typeface = Ui.body
            setTextColor(Ui.MUTED)
            setPadding(0, dp(8), 0, 0)
        }
        column.addView(summaryView)

        changeBox = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(4), 0, 0)
        }
        column.addView(changeBox)

        column.addView(TextView(this).apply {
            text = "同步只搬数据，提醒由手机本地排程，断网也会响。"
            textSize = 12f
            typeface = Ui.body
            setTextColor(Ui.MUTED)
            setPadding(0, dp(28), 0, 0)
        })

        root.addView(column)
        return root
    }

    private fun actionButton(label: String, primary: Boolean = false, small: Boolean = false, action: () -> Unit) =
        pill(label, primary, small).apply {
            setOnClickListener { action() }
            actionButtons.add(this)
        }

    private fun updateStatus() {
        val token = db.getMeta("token")
        val name = db.getMeta("user") ?: ""
        // token 还在就说已登入；token 被服务端作废时账号仍留着，提示「输密码」而不是从头填账号
        statusView.text = when {
            token != null -> "已登入：$name"
            name.isNotEmpty() -> "未登入：$name · 点这里输密码登入"
            else -> "未登入 · 点右上角「设置」"
        }
        statusView.setTextColor(if (token != null) Ui.INK_2 else if (name.isNotEmpty()) Ui.WARN else Ui.MUTED)
        stampView.text = buildString {
            append("本地 ").append(db.all().size).append(" 条")
            lastSyncText()?.let { append(" · ").append(it).append(" 上次同步") }
        }
        permHint.visibility = if (Permissions.allGranted(this)) View.GONE else View.VISIBLE
        if (permHint.visibility == View.VISIBLE) {
            permHint.text = "提醒权限未开全 · 点这里去设置"
        }
    }

    /** 「今天 12:10」「昨天 21:06」「11 月 9 日 08:30」 */
    private fun lastSyncText(): String? {
        val at = db.getMeta("last_sync_at")?.toLongOrNull() ?: return null
        val then = java.util.Calendar.getInstance().apply { timeInMillis = at }
        val now = java.util.Calendar.getInstance()
        val hm = String.format("%02d:%02d", then.get(java.util.Calendar.HOUR_OF_DAY), then.get(java.util.Calendar.MINUTE))
        val sameYear = then.get(java.util.Calendar.YEAR) == now.get(java.util.Calendar.YEAR)
        val dayDiff = now.get(java.util.Calendar.DAY_OF_YEAR) - then.get(java.util.Calendar.DAY_OF_YEAR)
        return when {
            sameYear && dayDiff == 0 -> "今天 $hm"
            sameYear && dayDiff == 1 -> "昨天 $hm"
            else -> "${then.get(java.util.Calendar.MONTH) + 1} 月 ${then.get(java.util.Calendar.DAY_OF_MONTH)} 日 $hm"
        }
    }

    // ---------- 动作 ----------

    private fun doSync() {
        val (base, token) = session() ?: return
        runAsync("同步中") {
            // 先把系统日历里新写/改过的事件导入成本地任务（标脏），这一轮的全量快照才带得上去；
            // 没有读取权限或读失败就安静跳过，不拦同步
            val imported = importCalendarQuietly()
            // 再扫日历：手机里删掉的事件变成墓碑，才能跟着这一轮推给电脑端，电脑端那边也就消失了
            val cal = CalendarSync.scan(applicationContext, db)
            // 全量快照：整份上传本地日历，服务器按修改时间合并，不依赖 dirty 标记。
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

            val noteChanges = changedCount(beforeNotes, db.notes().associate { it.id to it.updatedAt })
            val uploaded = uploadedCount(payload.accepted, sentRev)
            val noteUploaded = uploadedCount(payload.noteAccepted, sentNoteRev)
            val uploadedIds = payload.accepted.filter { (id, rev) -> sentRev[id] != rev }.map { it.first }.toSet()
            val downloadedIds = changedTaskIds(sentAt, payload.tasks).toMutableSet()
            payload.rejected.forEach { downloadedIds.add(it.id) }
            val downloadedTasks = payload.tasks.filter { it.id in downloadedIds }

            val rows = mutableListOf<ChangeRow>()
            dirty.filter { it.id in uploadedIds }
                .forEach { rows.add(ChangeRow("↑", it.title, Ui.ACCENT_DEEP)) }
            if (alarmOps.isNotEmpty()) {
                rows.add(ChangeRow("↑", "闹钟改动 ${alarmOps.size} 个", Ui.ACCENT_DEEP))
            }
            // dirtyNotes 是「全量便签快照」，恒非空——拿它判断会每次都多出一行「便签 0 条」
            if (noteUploaded > 0) {
                rows.add(ChangeRow("↑", "便签 $noteUploaded 条", Ui.ACCENT_DEEP))
            }
            payload.rejected.forEach { rows.add(ChangeRow("↺", it.title, Ui.WARN)) }
            downloadedTasks.forEach { task ->
                if (task.deleted) {
                    rows.add(ChangeRow("×", task.title, Ui.MUTED))
                } else {
                    rows.add(ChangeRow("↓", task.title, Ui.INK_2))
                }
            }
            if (beforeAlarms.sortedBy { it.id } != payload.alarms.sortedBy { it.id }) {
                rows.add(ChangeRow("↓", "闹钟 ${payload.alarms.size} 个", Ui.INK_2))
            }
            if (noteChanges > 0) {
                rows.add(ChangeRow("↓", "便签 $noteChanges 条", Ui.INK_2))
            }
            cal.titles.forEach { rows.add(ChangeRow("×", it, Ui.MUTED)) }

            ReminderScheduler.rescheduleAll(applicationContext)
            writeCalendarQuietly()
            val deleted = downloadedTasks.count { it.deleted }
            // 闹钟 / 便签 / 任务是三条独立通道：任务的 0 条不代表另外两条没同步，摘要里分开写清楚
            val alarmChanged = beforeAlarms.sortedBy { it.id } != payload.alarms.sortedBy { it.id }
            // 服务端回的是**全量快照**（它不看 since），payload.tasks 恒等于云端总数；
            // 直接拿它的 size 当「下载条数」，每条同步都会报「下载 146 条」，
            // 用户会以为在反复重复下载。downloadedTasks 已按 updated_at 变没变筛过，只算它。
            val downloaded = downloadedTasks.size
            val sb = StringBuilder("上传任务 $uploaded 条，下载任务 $downloaded 条")
            if (imported > 0) sb.append("，日历导入 $imported 条")
            if (deleted > 0) sb.append("（含删除 $deleted 条）")
            if (cal.removed > 0) sb.append("，日历删除 ${cal.removed} 条")
            if (alarmOps.isNotEmpty()) sb.append("，闹钟改动 ${alarmOps.size} 个")
            if (alarmChanged) sb.append("，闹钟 ${payload.alarms.size} 个")
            // dirtyNotes 是「全量便签快照」，恒非空——拿它判断会每次都多报「便签 上传 0 / 下载 0 条」
            val noteTouched = noteUploaded > 0 || noteChanges > 0
            if (noteTouched) {
                sb.append("，便签 上传 $noteUploaded / 下载 $noteChanges 条")
            }
            val noChange = uploaded == 0 && downloadedTasks.isEmpty() &&
                alarmOps.isEmpty() && !alarmChanged && payload.rejected.isEmpty() &&
                !noteTouched && cal.removed == 0 && imported == 0
            // 日历权限没给时导入被静默跳过：不说明白，用户只会看到「没有变化」，
            // 以为同步坏了——本次报障就是这个症状。
            val hint = if (checkSelfPermission(Manifest.permission.READ_CALENDAR)
                != PackageManager.PERMISSION_GRANTED
            ) "\n没有「日历」权限，读不到手机日历里的日程。去「设置 → 系统日历」授权后再同步。" else ""
            Outcome((if (noChange) "已是最新，没有变化" else sb.toString()) + hint, rows)
        }
    }

    /** 按 (id → updated_at) 的映射比较前后差异：新增、删除、改过都算一条。 */
    private fun changedCount(before: Map<String, Long>, after: Map<String, Long>): Int =
        (before.keys + after.keys).count { before[it] != after[it] }

    /** 全量快照里服务端会把没变的条目也回传；只有 rev 变了的才算真正上传。 */
    private fun uploadedCount(accepted: List<Pair<String, Long>>, sentRev: Map<String, Long>): Int =
        accepted.count { (id, rev) -> sentRev[id] != rev }

    /** 真正被服务端版本改写的任务 id；全量快照里绝大多数条目不该出现在结果里。 */
    private fun changedTaskIds(before: Map<String, Long>, tasks: List<Task>): Set<String> =
        tasks.filter { before[it.id] != it.updatedAt }.map { it.id }.toSet()

    /**
     * 同步前静默导入系统日历：刚在手机日历里写的日程先变成本地任务（标脏），
     * 才能跟着这一轮的全量快照推上云端。没授权、读不到就安静跳过，不拦同步。
     * 返回这一轮新导入/更新的条数。
     */
    private fun importCalendarQuietly(): Int {
        if (checkSelfPermission(Manifest.permission.READ_CALENDAR)
            != PackageManager.PERMISSION_GRANTED) return 0
        return try {
            SystemCalendar.importInto(db, SystemCalendar.read(this))
        } catch (e: Exception) {
            0
        }
    }

    /** 同步完顺手把任务写进系统日历；没授权或没有可写日历就安静跳过。 */
    private fun writeCalendarQuietly() {
        if (!CalendarWriter.hasPermission(this)) return
        runCatching { CalendarWriter.sync(applicationContext, db) }
    }

    private fun session(): Pair<String, String>? {
        val base = Api.normalize(db.getMeta("server_url") ?: Api.DefaultServerUrl)
        if (base.isNullOrEmpty()) {
            summaryView.text = "先到「设置」里填服务器地址并登入"
            return null
        }
        val token = db.getMeta("token")
        if (token == null) {
            summaryView.text = if (!db.getMeta("user").isNullOrEmpty()) {
                "未登入：账号还在，点上面的提示输密码即可"
            } else {
                "先到「设置」里登入"
            }
            return null
        }
        return base to token
    }

    /** 去设置页；未登入但账号还在时带 reauth，进去直接补密码。 */
    private fun openSettings() {
        val lost = db.getMeta("token") == null && !db.getMeta("user").isNullOrEmpty()
        startActivity(
            Intent(this, SettingsActivity::class.java).putExtra("reauth", lost)
        )
    }

    private fun runAsync(label: String, work: () -> Outcome) {
        setBusy(true)
        summaryView.text = "$label…"
        Thread {
            var done = false
            var authLost = false
            val outcome = try {
                work().also { done = true }
            } catch (e: Exception) {
                if (e is ApiException && e.code == 401) {
                    // 服务端不认这枚 token：只作废 token，账号和服务器地址都留着，
                    // 跳到设置页补个密码就能恢复，不用重填账号
                    db.setMeta("token", null)
                    authLost = true
                    Outcome("登录已失效，正在打开登录页…", emptyList())
                } else {
                    Outcome("失败：" + describe(e), emptyList())
                }
            }
            runOnUiThread {
                setBusy(false)
                summaryView.text = outcome.summary
                summaryView.setTextColor(if (authLost) Ui.WARN else Ui.INK_2)
                renderChanges(outcome.rows)
                if (done) {
                    db.setMeta("last_sync_at", System.currentTimeMillis().toString())
                    resultTime.text = "刚刚"
                }
                updateStatus()
                // 留半秒让用户看清提示再跳，避免「点了没反应」的错觉
                if (authLost) summaryView.postDelayed({ openSettings() }, 600)
            }
        }.start()
    }

    private fun setBusy(busy: Boolean) {
        actionButtons.forEach { it.isEnabled = !busy }
    }

    private fun renderChanges(rows: List<ChangeRow>) {
        changeBox.removeAllViews()
        if (rows.isEmpty()) {
            changeBox.addView(TextView(this).apply {
                text = "这次没有新增或变化的条目"
                textSize = 12f
                typeface = Ui.body
                setTextColor(Ui.MUTED)
                setPadding(0, dp(8), 0, 0)
            })
            return
        }

        rows.take(MAX_ROWS).forEach { row ->
            changeBox.addView(LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(0, dp(9), 0, dp(9))
                addView(TextView(this@MainActivity).apply {
                    text = row.arrow
                    textSize = 15f
                    typeface = Ui.mono
                    setTextColor(row.color)
                    gravity = Gravity.CENTER
                }, LinearLayout.LayoutParams(dp(22), ViewGroup.LayoutParams.WRAP_CONTENT))
                addView(TextView(this@MainActivity).apply {
                    text = row.title
                    textSize = 14f
                    typeface = Ui.body
                    setTextColor(Ui.INK)
                    maxLines = 1
                    ellipsize = android.text.TextUtils.TruncateAt.END
                }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            })
        }

        if (rows.size > MAX_ROWS) {
            changeBox.addView(TextView(this).apply {
                text = "…还有 ${rows.size - MAX_ROWS} 条"
                textSize = 12f
                typeface = Ui.body
                setTextColor(Ui.MUTED)
                setPadding(0, dp(4), 0, 0)
            })
        }
    }

    /**
     * 失败原因翻译。这里最要紧的是把「网络不通」和「登录失效」分开：
     * 前者是网络/地址问题，重登没用；后者才需要换凭据。
     * 混成一句「failed to connect …」的话，用户只会以为掉线了。
     */
    private fun describe(e: Exception): String = when (e) {
        is ApiException -> e.message ?: "HTTP ${e.code}"
        is java.net.UnknownHostException -> "连不上服务器：地址写错了，或 DNS 不通（不是登录问题）"
        is java.net.SocketTimeoutException -> "连不上服务器：超时了，网络不通或这个端口被挡了（不是登录问题）"
        is java.net.ConnectException -> "连不上服务器：连接被拒绝，地址或端口不对（不是登录问题）"
        is java.net.SocketException -> "连不上服务器：网络中断了（不是登录问题）"
        else -> e.message ?: e.javaClass.simpleName
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    @Suppress("DEPRECATION")
    private fun statusBarHeight(): Int {
        val id = resources.getIdentifier("status_bar_height", "dimen", "android")
        return if (id > 0) resources.getDimensionPixelSize(id) else dp(24)
    }

    private companion object {
        const val MAX_ROWS = 60
    }
}
