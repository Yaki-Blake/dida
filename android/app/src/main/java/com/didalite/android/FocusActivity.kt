package com.didalite.android

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.LinearLayout

/**
 * 专注页：把原来的「时间」（闹钟）与「便签」合并成一个一级入口。
 *
 * 页内是两个子 tab，仿学业页那条分段样式：上面切换、下面换内容，
 * 不跳页、不重开 Activity，底栏始终停在「专注」上。
 *
 * 闹钟与便签各抽成 [AlarmPanel] / [NotesPanel]，它们只管画自己那块；
 * 便签的编辑仍然交给 [NotesActivity]（全屏编辑页），回来时重画列表。
 */
class FocusActivity : Activity() {

    private companion object {
        const val TAB_ALARM = 0
        const val TAB_NOTE = 1
        const val TOP_PADDING_DP = 10
    }

    private lateinit var db: Db
    private var tab = TAB_ALARM

    private lateinit var tabs: LinearLayout
    private lateinit var body: FrameLayout

    private lateinit var alarmPanel: AlarmPanel
    private lateinit var notesPanel: NotesPanel
    private var alarmView: View? = null
    private var notesView: View? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        db = Db(this)
        ensurePanels()
        tab = when (intent?.getStringExtra("tab")) {
            "note", "notes" -> TAB_NOTE
            else -> TAB_ALARM
        }
        setContentView(NavBar.wrap(this, buildUi(), NavBar.FOCUS))
        applyDidaTheme()
        render()
    }

    override fun onResume() {
        super.onResume()
        // 从便签编辑页回来：列表可能变了，重画当前面板
        if (tab == TAB_NOTE) notesPanel.render() else alarmPanel.render()
    }

    override fun onDestroy() {
        super.onDestroy()
        db.close()
    }

    // ---------- 骨架 ----------

    private fun buildUi(): View {
        val column = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        // 左右不留边距：两个面板自己带 20dp 内边距，这里再加会变成双份缩进
        column.setPadding(0, statusBarHeight() + dpx(TOP_PADDING_DP), 0, 0)

        tabs = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(dpx(20), 0, dpx(20), 0)
        }
        column.addView(tabs)
        // 分隔线跟着左右缩进 20dp，与两个面板的内容对齐
        column.addView(LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(dpx(20), 0, dpx(20), 0)
            addView(pageRule(10))
        })

        body = FrameLayout(this)
        column.addView(
            body,
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f),
        )
        return column
    }

    private fun render() {
        tabs.removeAllViews()
        tabs.addView(tabPill("闹钟与日程", TAB_ALARM), gapParams(weight = 1f, end = 6))
        tabs.addView(tabPill("便签", TAB_NOTE), gapParams(weight = 1f))

        body.removeAllViews()
        val view = when (tab) {
            TAB_ALARM -> alarmView ?: alarmPanel.build().also { alarmView = it }
            else -> notesView ?: notesPanel.build().also { notesView = it }
        }
        body.addView(view, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT
        ))
    }

    private fun tabPill(label: String, target: Int): View =
        pill(label, primary = tab == target, small = true).also { view ->
            view.setOnClickListener {
                if (tab == target) return@setOnClickListener
                tab = target
                render()
            }
        }

    /** 便签面板要开编辑器：转给全屏编辑页，改完回来自会重画。 */
    private fun openNoteEditor(noteId: String?, isNew: Boolean) {
        val target = if (isNew) Note.create().also { db.saveNote(it) } else db.note(noteId.orEmpty())
        if (target == null) return
        startActivity(
            Intent(this, NotesActivity::class.java)
                .putExtra(NotesActivity.EXTRA_NOTE_ID, target.id)
                .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP)
        )
    }

    private fun statusBarHeight(): Int {
        val id = resources.getIdentifier("status_bar_height", "dimen", "android")
        return if (id > 0) resources.getDimensionPixelSize(id) else dpx(24)
    }

    /** 两个面板要等 db 就绪才能建，所以显式准备一次，别靠 lateinit 的隐式顺序。 */
    private fun ensurePanels() {
        if (!::alarmPanel.isInitialized) alarmPanel = AlarmPanel(this, db)
        if (!::notesPanel.isInitialized) {
            notesPanel = NotesPanel(this, db) { noteId, isNew -> openNoteEditor(noteId, isNew) }
        }
    }
}
