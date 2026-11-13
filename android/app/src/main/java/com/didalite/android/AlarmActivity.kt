package com.didalite.android

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.os.Bundle
import android.provider.AlarmClock
import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import java.util.Calendar

/**
 * 闹钟页：只展示电脑端「闹钟」页同步下来的独立闹钟。
 *
 * 任务提醒、从系统日历导入的日程都不进这里——它们不是闹钟。
 * Android 没有公开接口能直接往时钟 App 的闹钟列表里写数据
 * （`com.android.deskclock/.AlarmProvider` 受 signature|privileged 权限保护），
 * 所以点「写入」只是把时分、名称、重复日预填到时钟 App 的新建页，保存与否由用户决定。
 */
class AlarmActivity : Activity() {

    private lateinit var db: Db
    private lateinit var listBox: LinearLayout
    private lateinit var emptyView: TextView
    private lateinit var batchBar: LinearLayout
    private lateinit var batchCount: TextView
    private lateinit var batchToggle: TextView
    private val batchActions = mutableListOf<TextView>()
    private var selectMode = false
    private val selected = mutableSetOf<String>()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        db = Db(this)
        setContentView(buildUi())
        applyDidaTheme()
        render()
    }

    // ---------- 界面 ----------

    private fun buildUi(): View {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Ui.PAPER)
        }
        val scroll = ScrollView(this).apply {
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
            text = "闹钟"
            textSize = 22f
            typeface = Ui.display
            setTextColor(Ui.INK)
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        batchToggle = pill("批量", small = true).apply {
            setOnClickListener {
                selectMode = !selectMode
                selected.clear()
                render()
            }
        }
        titleRow.addView(batchToggle, gapParams(start = 6))
        titleRow.addView(pill("打开时钟", small = true).apply {
            setOnClickListener { openClockApp() }
        }, gapParams(start = 6))
        column.addView(titleRow)

        column.addView(TextView(this).apply {
            text = "只显示电脑端「闹钟」里的闹钟。停用、删除会在下次同步时一起改到电脑上；「写入」把时分和名称预填进系统时钟。"
            textSize = 12f
            typeface = Ui.body
            setTextColor(Ui.MUTED)
            setLineSpacing(dp(3).toFloat(), 1f)
            setPadding(0, dp(10), 0, 0)
        })

        emptyView = TextView(this).apply {
            text = "电脑上还没有闹钟\n同步一次就会出现在这里"
            textSize = 13f
            typeface = Ui.body
            setTextColor(Ui.MUTED)
            setLineSpacing(dp(4).toFloat(), 1f)
            setPadding(0, dp(28), 0, 0)
            visibility = View.GONE
        }
        column.addView(emptyView)

        listBox = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(10), 0, 0)
        }
        column.addView(listBox)

        batchBar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setBackgroundColor(Ui.PAPER_2)
            setPadding(dp(14), dp(8), dp(14), dp(8))
            visibility = View.GONE
        }
        batchCount = TextView(this).apply {
            textSize = 12f
            typeface = Ui.mono
            setTextColor(Ui.INK_2)
        }
        batchBar.addView(batchCount, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        batchBar.addView(batchAction("全选") { toggleSelectAll() })
        batchBar.addView(batchAction("启用") { batchEnabled(true) }, gapParams(start = 6))
        batchBar.addView(batchAction("停用") { batchEnabled(false) }, gapParams(start = 6))
        batchBar.addView(batchAction("删除", Ui.WARN) { confirmBatchDelete() }, gapParams(start = 6))

        scroll.addView(column)
        root.addView(scroll, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))
        root.addView(batchBar)
        return root
    }

    private fun batchAction(label: String, color: Int = Ui.INK, action: () -> Unit) =
        pill(label, small = true).apply {
            setTextColor(color)
            setOnClickListener { action() }
            batchActions.add(this)
        }

    private fun render() {
        listBox.removeAllViews()
        val items = db.alarms()
        selected.retainAll(items.map { it.id }.toSet())
        if (selectMode && items.isEmpty()) {
            selectMode = false
            selected.clear()
        }
        batchToggle.text = if (selectMode) "完成" else "批量"
        batchBar.visibility = if (selectMode) View.VISIBLE else View.GONE
        batchCount.text = "已选 ${selected.size} 个"
        val hasSelection = selected.isNotEmpty()
        batchActions.forEach {
            it.isEnabled = hasSelection
            it.alpha = if (hasSelection) 1f else 0.45f
        }
        emptyView.visibility = if (items.isEmpty()) View.VISIBLE else View.GONE
        items.forEachIndexed { index, alarm ->
            listBox.addView(alarmRow(alarm))
            if (index != items.lastIndex) listBox.addView(pageRule())
        }
    }

    private fun alarmRow(alarm: Alarm): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, dp(12), 0, dp(12))
        }

        if (selectMode) {
            val on = alarm.id in selected
            row.addView(TextView(this).apply {
                text = if (on) "●" else "○"
                textSize = 16f
                typeface = Ui.body
                setTextColor(if (on) Ui.ACCENT_DEEP else Ui.MUTED)
                gravity = Gravity.CENTER
            }, LinearLayout.LayoutParams(dp(28), ViewGroup.LayoutParams.WRAP_CONTENT))
            row.isClickable = true
            row.setOnClickListener {
                if (!selected.add(alarm.id)) selected.remove(alarm.id)
                render()
            }
        }

        val info = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        info.addView(TextView(this).apply {
            text = alarm.timeText
            textSize = 22f
            typeface = Ui.monoBold
            setTextColor(if (alarm.enabled) Ui.INK else Ui.MUTED)
        })
        info.addView(TextView(this).apply {
            text = alarm.label.ifBlank { "闹钟" }
            textSize = 14f
            typeface = Ui.medium
            setTextColor(if (alarm.enabled) Ui.INK_2 else Ui.MUTED)
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
            setPadding(0, dp(3), 0, 0)
        })
        info.addView(TextView(this).apply {
            text = repeatText(alarm)
            textSize = 12f
            typeface = Ui.body
            setTextColor(Ui.MUTED)
            setPadding(0, dp(1), 0, 0)
        })
        row.addView(info, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))

        // 批量模式下不显示单条操作，点整行就是选中 / 取消
        if (selectMode) return row

        val actions = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }

        val topLine = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.END
        }
        topLine.addView(pill("写入", small = true).apply {
            setOnClickListener {
                if (openAlarmEditor(alarm)) {
                    text = "已唤起"
                    isEnabled = false
                } else {
                    Toast.makeText(this@AlarmActivity, "没有找到系统时钟应用", Toast.LENGTH_SHORT).show()
                }
            }
        })
        actions.addView(topLine)

        val bottomLine = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.END
        }
        bottomLine.addView(pill(if (alarm.enabled) "停用" else "启用", small = true).apply {
            setOnClickListener {
                db.setAlarmEnabled(alarm.id, !alarm.enabled)
                Toast.makeText(this@AlarmActivity, "下次同步会带到电脑", Toast.LENGTH_SHORT).show()
                render()
            }
        })
        bottomLine.addView(pill("删除", small = true).apply {
            setTextColor(Ui.WARN)
            setOnClickListener { confirmDelete(alarm) }
        }, gapParams(start = 6))
        actions.addView(
            bottomLine,
            LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply { topMargin = dp(6) }
        )

        row.addView(actions, gapParams(start = 12))
        return row
    }

    private fun confirmDelete(alarm: Alarm) {
        AlertDialog.Builder(this)
            .setTitle("删除这个闹钟？")
            .setMessage(
                alarm.timeText + "  " + alarm.label.ifBlank { "闹钟" } +
                    "\n手机上删掉后，下次同步电脑端也会删掉。"
            )
            .setPositiveButton("删除") { _, _ ->
                db.deleteAlarm(alarm.id)
                render()
                Toast.makeText(this, "已删除，下次同步会同步到电脑", Toast.LENGTH_SHORT).show()
            }
            .setNegativeButton("取消", null)
            .show()
    }

    // ---------- 批量 ----------

    private fun toggleSelectAll() {
        val ids = db.alarms().map { it.id }
        if (ids.isNotEmpty() && selected.size == ids.size) selected.clear() else selected.addAll(ids)
        render()
    }

    private fun batchEnabled(enabled: Boolean) {
        if (selected.isEmpty()) return
        val ids = selected.toList()
        ids.forEach { db.setAlarmEnabled(it, enabled) }
        val verb = if (enabled) "启用" else "停用"
        selected.clear()
        selectMode = false
        render()
        Toast.makeText(this, "已$verb ${ids.size} 个，下次同步会带到电脑", Toast.LENGTH_SHORT).show()
    }

    private fun confirmBatchDelete() {
        if (selected.isEmpty()) return
        val ids = selected.toList()
        AlertDialog.Builder(this)
            .setTitle("删除选中的 ${ids.size} 个闹钟？")
            .setMessage("手机上删掉后，下次同步电脑端也会删掉。")
            .setPositiveButton("删除") { _, _ ->
                db.deleteAlarms(ids)
                selected.clear()
                selectMode = false
                render()
                Toast.makeText(this, "已删除 ${ids.size} 个，下次同步会同步到电脑", Toast.LENGTH_SHORT).show()
            }
            .setNegativeButton("取消", null)
            .show()
    }

    // ---------- 动作 ----------

    /** 唤起时钟 App 的新建闹钟页：时分与重复日按闹钟本身预填。 */
    private fun openAlarmEditor(alarm: Alarm): Boolean {
        val intent = Intent(AlarmClock.ACTION_SET_ALARM).apply {
            putExtra(AlarmClock.EXTRA_HOUR, alarm.hour)
            putExtra(AlarmClock.EXTRA_MINUTES, alarm.minute)
            if (alarm.label.isNotBlank()) putExtra(AlarmClock.EXTRA_MESSAGE, alarm.label)
            repeatDays(alarm)?.let { putIntegerArrayListExtra(AlarmClock.EXTRA_DAYS, it) }
            putExtra(AlarmClock.EXTRA_SKIP_UI, false)
        }
        return runCatching { startActivity(intent) }.isSuccess
    }

    /** once 不带重复日；daily 全选七天；weekday 只选周一到周五。 */
    private fun repeatDays(alarm: Alarm): ArrayList<Int>? = when (alarm.repeatRule) {
        "daily" -> arrayListOf(
            Calendar.SUNDAY, Calendar.MONDAY, Calendar.TUESDAY, Calendar.WEDNESDAY,
            Calendar.THURSDAY, Calendar.FRIDAY, Calendar.SATURDAY,
        )
        "weekday" -> arrayListOf(
            Calendar.MONDAY, Calendar.TUESDAY, Calendar.WEDNESDAY,
            Calendar.THURSDAY, Calendar.FRIDAY,
        )
        else -> null
    }

    private fun repeatText(alarm: Alarm): String {
        val rule = when (alarm.repeatRule) {
            "once" -> "单次"
            "weekday" -> "工作日"
            else -> "每天"
        }
        return if (alarm.enabled) rule else "$rule  ·  已停用"
    }

    private fun openClockApp() {
        val intent = Intent(AlarmClock.ACTION_SHOW_ALARMS)
        if (runCatching { startActivity(intent) }.isFailure) {
            Toast.makeText(this, "没有找到系统时钟应用", Toast.LENGTH_SHORT).show()
        }
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    @Suppress("DEPRECATION")
    private fun statusBarHeight(): Int {
        val id = resources.getIdentifier("status_bar_height", "dimen", "android")
        return if (id > 0) resources.getDimensionPixelSize(id) else dp(24)
    }
}
