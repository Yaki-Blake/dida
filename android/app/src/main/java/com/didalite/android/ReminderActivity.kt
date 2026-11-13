package com.didalite.android

import android.app.Activity
import android.os.Build
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.LinearLayout
import android.widget.TextView
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** 息屏时全屏弹出的提醒页，三个动作与通知栏一致（计划书 6.5）。 */
class ReminderActivity : Activity() {

    private lateinit var db: Db
    private var task: Task? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1) {
            setShowWhenLocked(true)
            setTurnScreenOn(true)
        } else {
            window.addFlags(
                WindowManager.LayoutParams.FLAG_SHOW_WHEN_LOCKED or
                    WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON
            )
        }
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        db = Db(this)
        val found = intent.getStringExtra(ReminderNotifier.EXTRA_TASK_ID)?.let { db.find(it) }
        if (found == null) {
            finish()
            return
        }
        task = found
        setContentView(buildUi(found))
        applyDidaTheme()
    }

    private fun buildUi(task: Task): LinearLayout {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(24), dp(24), dp(24), dp(32))
            setBackgroundColor(Ui.PAPER)
        }

        root.addView(TextView(this).apply {
            text = "提醒"
            textSize = 12f
            typeface = Ui.medium
            setTextColor(Ui.MUTED)
        })

        root.addView(TextView(this).apply {
            text = task.title
            textSize = 26f
            typeface = Ui.display
            setTextColor(Ui.INK)
            setLineSpacing(dp(4).toFloat(), 1f)
            setPadding(0, dp(10), 0, dp(10))
        })

        root.addView(TextView(this).apply {
            text = describe(task)
            textSize = 14f
            typeface = Ui.mono
            setTextColor(Ui.ACCENT_DEEP)
        })

        task.note?.takeIf { it.isNotBlank() }?.let { note ->
            root.addView(TextView(this).apply {
                text = note
                textSize = 14f
                typeface = Ui.body
                setTextColor(Ui.INK_2)
                setLineSpacing(dp(4).toFloat(), 1f)
                setPadding(0, dp(14), 0, 0)
            })
        }

        root.addView(pill("完成", primary = true).apply {
            setOnClickListener { ReminderActions.complete(this@ReminderActivity, task); finish() }
        }, wide(topMargin = 32))
        root.addView(pill("推迟 10 分钟").apply {
            setOnClickListener {
                ReminderActions.snooze(this@ReminderActivity, task, 10 * 60 * 1000L)
                finish()
            }
        }, wide())
        root.addView(pill("推迟 1 天").apply {
            setOnClickListener {
                ReminderActions.snooze(this@ReminderActivity, task, 24 * 60 * 60 * 1000L)
                finish()
            }
        }, wide())

        root.addView(TextView(this).apply {
            text = "关闭"
            textSize = 13f
            typeface = Ui.medium
            setTextColor(Ui.MUTED)
            gravity = Gravity.CENTER
            setPadding(0, dp(20), 0, 0)
            isClickable = true
            setOnClickListener { finish() }
        })

        return root
    }

    private fun wide(topMargin: Int = 12) = LinearLayout.LayoutParams(
        ViewGroup.LayoutParams.MATCH_PARENT,
        ViewGroup.LayoutParams.WRAP_CONTENT
    ).apply { this.topMargin = dp(topMargin) }

    private fun describe(task: Task): String {
        val at = ReminderPlanner.effectiveRemindAt(task) ?: return "到点了"
        val text = SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.CHINA).format(Date(at))
        return if (task.allDay) "$text · 全天" else text
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()
}
