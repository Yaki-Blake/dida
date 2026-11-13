package com.didalite.android

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

/**
 * 手机端只做同步面板：仅上传 / 仅下载 / 同步 / 设置，
 * 下面列出这一次具体新上传了哪些、新下载了哪些。
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
        titleRow.addView(pill("闹钟", small = true).apply {
            setOnClickListener { startActivity(Intent(this@MainActivity, AlarmActivity::class.java)) }
        }, gapParams(start = 6))
        titleRow.addView(pill("设置", small = true).apply {
            setOnClickListener { startActivity(Intent(this@MainActivity, SettingsActivity::class.java)) }
        }, gapParams(start = 6))
        column.addView(titleRow)

        statusView = TextView(this).apply {
            textSize = 14f
            typeface = Ui.body
            setTextColor(Ui.INK_2)
            setPadding(0, dp(14), 0, 0)
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

        val actionRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(0, dp(20), 0, 0)
        }
        actionRow.addView(actionButton("仅上传") { doUpload() }, gapParams(weight = 1f))
        actionRow.addView(actionButton("仅下载") { doDownload() }, gapParams(weight = 1f, start = 8))
        actionRow.addView(actionButton("同步", primary = true) { doSync() }, gapParams(weight = 1f, start = 8))
        column.addView(actionRow)

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
            text = "点「同步」，两边的改动会合到一起"
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

    private fun actionButton(label: String, primary: Boolean = false, action: () -> Unit) =
        pill(label, primary).apply {
            setOnClickListener { action() }
            actionButtons.add(this)
        }

    private fun updateStatus() {
        val logged = db.getMeta("token") != null
        val name = db.getMeta("user") ?: ""
        statusView.text = if (logged) "已登入：$name" else "未登入 · 点右上角「设置」"
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

    private fun doUpload() {
        val (base, token) = session() ?: return
        runAsync("上传中") {
            val dirty = db.dirty()
            val alarmOps = db.alarmOps()
            if (dirty.isEmpty() && alarmOps.isEmpty()) {
                return@runAsync Outcome("没有待上传的改动", emptyList())
            }

            val sentAt = dirty.associate { it.id to it.updatedAt }
            // since 取极大值：服务端只回 accepted / rejected，不推任何任务，避免「仅上传」顺带改了本地
            val payload = Api.push(base, token, Long.MAX_VALUE / 4, dirty, alarmOps)
            payload.accepted.forEach { (id, rev) -> db.markSynced(id, rev, sentAt[id]) }
            payload.rejected.forEach { db.applyServer(it) }
            db.clearAlarmOps()

            val acceptedIds = payload.accepted.map { it.first }.toSet()
            val rows = mutableListOf<ChangeRow>()
            dirty.filter { it.id in acceptedIds }
                .forEach { rows.add(ChangeRow("↑", it.title, Ui.ACCENT_DEEP)) }
            payload.rejected.forEach { rows.add(ChangeRow("↺", it.title, Ui.WARN)) }
            if (alarmOps.isNotEmpty()) {
                rows.add(ChangeRow("↑", "闹钟改动 ${alarmOps.size} 个", Ui.ACCENT_DEEP))
            }

            ReminderScheduler.rescheduleAll(applicationContext)
            val alarmNote = if (alarmOps.isEmpty()) "" else "，闹钟改动 ${alarmOps.size} 个"
            Outcome(
                "上传 ${payload.accepted.size} 条$alarmNote，被拒 ${payload.rejected.size} 条（已改用电脑版本）",
                rows,
            )
        }
    }

    private fun doDownload() {
        val (base, token) = session() ?: return
        runAsync("下载中") {
            val beforeAlarms = db.alarms()
            val payload = Api.pull(base, token, db.cursor())
            payload.tasks.forEach { db.applyServer(it) }
            db.setCursor(payload.maxRev)
            db.replaceAlarms(payload.alarms)

            val rows = payload.tasks.mapTo(mutableListOf()) { task ->
                if (task.deleted) {
                    ChangeRow("×", task.title, Ui.MUTED)
                } else {
                    ChangeRow("↓", task.title, Ui.INK_2)
                }
            }
            if (beforeAlarms.sortedBy { it.id } != payload.alarms.sortedBy { it.id }) {
                rows.add(ChangeRow("↓", "闹钟 ${payload.alarms.size} 个", Ui.INK_2))
            }
            ReminderScheduler.rescheduleAll(applicationContext)
            writeCalendarQuietly()
            Outcome(summaryForDownload(payload.tasks), rows)
        }
    }

    private fun doSync() {
        val (base, token) = session() ?: return
        runAsync("同步中") {
            val dirty = db.dirty()
            val alarmOps = db.alarmOps()
            val sentAt = dirty.associate { it.id to it.updatedAt }
            val beforeAlarms = db.alarms()
            val payload = Api.push(base, token, db.cursor(), dirty, alarmOps)
            payload.accepted.forEach { (id, rev) -> db.markSynced(id, rev, sentAt[id]) }
            payload.rejected.forEach { db.applyServer(it) }
            payload.tasks.forEach { db.applyServer(it) }
            db.setCursor(payload.maxRev)
            db.replaceAlarms(payload.alarms)
            db.clearAlarmOps()

            val acceptedIds = payload.accepted.map { it.first }.toSet()
            val rows = mutableListOf<ChangeRow>()
            dirty.filter { it.id in acceptedIds }
                .forEach { rows.add(ChangeRow("↑", it.title, Ui.ACCENT_DEEP)) }
            if (alarmOps.isNotEmpty()) {
                rows.add(ChangeRow("↑", "闹钟改动 ${alarmOps.size} 个", Ui.ACCENT_DEEP))
            }
            payload.rejected.forEach { rows.add(ChangeRow("↺", it.title, Ui.WARN)) }
            payload.tasks.forEach { task ->
                if (task.deleted) {
                    rows.add(ChangeRow("×", task.title, Ui.MUTED))
                } else {
                    rows.add(ChangeRow("↓", task.title, Ui.INK_2))
                }
            }
            if (beforeAlarms.sortedBy { it.id } != payload.alarms.sortedBy { it.id }) {
                rows.add(ChangeRow("↓", "闹钟 ${payload.alarms.size} 个", Ui.INK_2))
            }

            ReminderScheduler.rescheduleAll(applicationContext)
            writeCalendarQuietly()
            val deleted = payload.tasks.count { it.deleted }
            Outcome(
                "上传 ${payload.accepted.size} 条，下载 ${payload.tasks.size} 条" +
                    if (deleted > 0) "（含删除 $deleted 条）" else "",
                rows,
            )
        }
    }

    /** 同步完顺手把任务写进系统日历；没授权或没有可写日历就安静跳过。 */
    private fun writeCalendarQuietly() {
        if (!CalendarWriter.hasPermission(this)) return
        runCatching { CalendarWriter.sync(applicationContext, db) }
    }

    private fun summaryForDownload(tasks: List<Task>): String {
        val deleted = tasks.count { it.deleted }
        return if (tasks.isEmpty()) {
            "电脑上没有新改动"
        } else {
            "下载 ${tasks.size} 条" + if (deleted > 0) "（含删除 $deleted 条）" else ""
        }
    }

    private fun session(): Pair<String, String>? {
        val base = db.getMeta("server_url")?.let { Api.normalize(it) }
        if (base.isNullOrEmpty()) {
            summaryView.text = "先到「设置」里填服务器地址并登入"
            return null
        }
        val token = db.getMeta("token")
        if (token == null) {
            summaryView.text = "先到「设置」里登入"
            return null
        }
        return base to token
    }

    private fun runAsync(label: String, work: () -> Outcome) {
        setBusy(true)
        summaryView.text = "$label…"
        Thread {
            var done = false
            val outcome = try {
                work().also { done = true }
            } catch (e: Exception) {
                Outcome("失败：" + describe(e), emptyList())
            }
            runOnUiThread {
                setBusy(false)
                summaryView.text = outcome.summary
                summaryView.setTextColor(Ui.INK_2)
                renderChanges(outcome.rows)
                if (done) {
                    db.setMeta("last_sync_at", System.currentTimeMillis().toString())
                    resultTime.text = "刚刚"
                }
                updateStatus()
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

    private fun describe(e: Exception): String = when (e) {
        is ApiException -> e.message ?: "HTTP ${e.code}"
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
