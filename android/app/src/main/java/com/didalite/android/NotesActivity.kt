package com.didalite.android

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.Editable
import android.text.InputType
import android.text.TextWatcher
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast

/**
 * 便签页。
 *
 * 数据在本机 SQLite 里，跟着 /sync 与电脑端合并（独立 note 游标）。
 * 与系统便签的互通只有分享这一条合法通道——Android 没有公开 API 直接读写系统便签：
 *   · 往外：把便签交给系统便签 / 备忘录（单条「发到系统便签」，多条长按勾选后「分享」）
 *   · 往里：别的应用「分享 → 滴答便签」，单条 ACTION_SEND、多条 ACTION_SEND_MULTIPLE
 */
class NotesActivity : Activity() {

    companion object {
        /** 由「专注」页的便签面板传进来：直接打开这条便签的编辑页。 */
        const val EXTRA_NOTE_ID = "note_id"
    }

    private lateinit var db: Db

    /** 正在同步：防止连点重复推拉。 */
    private var syncing = false

    private var editing: Note? = null

    /** 多选状态：长按任意一条进入，列表重建时靠 id 保留勾选。 */
    private var selectMode = false
    private val selected = linkedSetOf<String>()

    private lateinit var titleInput: EditText
    private lateinit var bodyInput: EditText

    private val saveHandler = Handler(Looper.getMainLooper())
    private val saveRunnable = Runnable { persist() }

    private val watcher = object : TextWatcher {
        override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) {}
        override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {}
        override fun afterTextChanged(s: Editable?) {
            saveHandler.removeCallbacks(saveRunnable)
            saveHandler.postDelayed(saveRunnable, 600)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        db = Db(this)

        val incoming = sharedTexts(intent)
        val targetId = intent?.getStringExtra(EXTRA_NOTE_ID)
        if (incoming.isNotEmpty()) {
            // 从别的应用分享过来：单条直接进编辑器，多条一次性落库
            importIncoming(incoming, sharedSubject(intent))
        } else if (!targetId.isNullOrEmpty()) {
            // 从「专注」页点进来的：直接编辑这条
            val note = db.note(targetId)
            if (note != null) showEditor(note) else showList()
        } else {
            showList()
        }

        applyDidaTheme()
    }

    override fun onNewIntent(intent: Intent?) {
        super.onNewIntent(intent)
        setIntent(intent)
        val texts = sharedTexts(intent)
        if (texts.isNotEmpty()) {
            persist()
            importIncoming(texts, sharedSubject(intent))
            return
        }
        // 「专注」页再点一条便签：旧实例还在（SINGLE_TOP），直接换到那条的编辑页
        val targetId = intent?.getStringExtra(EXTRA_NOTE_ID)
        if (!targetId.isNullOrEmpty()) {
            persist()
            val note = db.note(targetId)
            if (note != null) showEditor(note) else showList()
        }
    }

    override fun onPause() {
        super.onPause()
        persist()
    }

    override fun onBackPressed() {
        when {
            selectMode -> {
                selectMode = false
                selected.clear()
                showList()
            }
            editing != null -> {
                persist()
                showList()
            }
            else -> super.onBackPressed()
        }
    }

    // ---------- 列表 ----------

    private fun showList() {
        editing = null

        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dpx(20), statusBarHeight() + dpx(10), dpx(20), dpx(28))
        }

        column.addView(if (selectMode) selectHeader() else normalHeader())

        if (selectMode) {
            column.addView(TextView(this).apply {
                text = "勾选后点「分享」导出，或「删除」清掉"
                textSize = 12f
                typeface = Ui.body
                setTextColor(Ui.MUTED)
                setPadding(0, dpx(8), 0, 0)
            })
        }

        // 全选 / 删除 贴在顶部，条目多的时候不用滚到底
        if (selectMode) column.addView(selectActions(), topGap(12))

        column.addView(pageRule(topMargin = 18))

        val notes = db.notes()
        if (notes.isEmpty()) {
            column.addView(TextView(this).apply {
                text = if (selectMode) "没有可选的便签" else "还没有便签\n点右上角「+ 新建」写第一条"
                textSize = 14f
                typeface = Ui.body
                setTextColor(Ui.MUTED)
                gravity = Gravity.CENTER
                setPadding(0, dpx(48), 0, dpx(48))
            })
        } else {
            notes.forEach { column.addView(noteRow(it), topGap(10)) }
        }

        val scroll = ScrollView(this).apply {
            isFillViewport = true
            setBackgroundColor(Ui.PAPER)
            addView(column)
        }
        setContentView(NavBar.wrap(this, scroll, NavBar.NOTES))
    }

    private fun normalHeader(): View {
        val titleRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        titleRow.addView(View(this), LinearLayout.LayoutParams(0, 1, 1f))
        titleRow.addView(pill("+ 新建", small = true).apply {
            setOnClickListener {
                val note = Note.create()
                db.saveNote(note)
                showEditor(note)
            }
        }, gapParams(start = 6))
        // 「同步」统一放最右、统一用绿色主色：三个页面里只有它是主操作，
        // 其余按钮一律白底，一眼就能看出该点哪个
        titleRow.addView(pill("同步", primary = true, small = true).apply {
            setOnClickListener { doSync() }
        }, gapParams(start = 6))
        return titleRow
    }

    /** 右上角最右端的「同步」：不区分页面，一把推拉全部数据。 */
    private fun doSync() {
        if (syncing) return
        syncing = true
        toast("同步中…")
        Thread {
            val message = try {
                SyncEngine.run(applicationContext, db)
            } catch (e: ApiException) {
                if (e.code == 401) db.setMeta("token", null)
                "同步失败：" + (e.message ?: "HTTP ${e.code}")
            } catch (e: Exception) {
                "同步失败：" + (e.message ?: e.javaClass.simpleName)
            }
            runOnUiThread {
                syncing = false
                toast(message)
                showList()
            }
        }.start()
    }

    private fun selectHeader(): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        row.addView(pill("取消", small = true).apply {
            setOnClickListener {
                selectMode = false
                selected.clear()
                showList()
            }
        })
        row.addView(TextView(this).apply {
            text = "已选 ${selected.size} 条"
            textSize = 13f
            typeface = Ui.mono
            setTextColor(Ui.INK_2)
            gravity = Gravity.CENTER
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        row.addView(pill("分享", primary = true, small = true).apply {
            isEnabled = selected.isNotEmpty()
            setOnClickListener { shareSelected() }
        })
        return row
    }

    private fun selectActions(): View {
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        row.addView(pill("全选", small = true).apply {
            setOnClickListener {
                val all = db.notes().map { it.id }
                if (selected.size == all.size) {
                    selected.clear()
                } else {
                    selected.clear()
                    selected.addAll(all)
                }
                showList()
            }
        }, gapParams(weight = 1f))
        row.addView(pill("删除", small = true).apply {
            setTextColor(Ui.WARN)
            setOnClickListener { deleteSelected() }
        }, gapParams(weight = 1f, start = 8))
        return row
    }

    /** 撑满宽度、只留上间距的行参数。 */
    private fun topGap(px: Int) = LinearLayout.LayoutParams(
        ViewGroup.LayoutParams.MATCH_PARENT,
        ViewGroup.LayoutParams.WRAP_CONTENT,
    ).apply { topMargin = dpx(px) }

    private fun noteRow(note: Note): View {
        val picked = note.id in selected
        val card = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dpx(14), dpx(12), dpx(14), dpx(12))
            background = GradientDrawable().apply {
                cornerRadius = dpx(12).toFloat()
                setColor(if (picked) Ui.PAPER_3 else Ui.PAPER_2)
                setStroke(dpx(if (picked) 2 else 1), if (picked) Ui.ACCENT else Ui.RULE)
            }
            setOnClickListener {
                if (selectMode) toggle(note.id) else showEditor(note)
            }
            setOnLongClickListener {
                if (!selectMode) {
                    selectMode = true
                    selected.clear()
                    selected.add(note.id)
                    showList()
                }
                true
            }
        }

        val head = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        head.addView(TextView(this).apply {
            text = note.displayTitle
            textSize = 16f
            typeface = Ui.medium
            setTextColor(Ui.INK)
            maxLines = 1
            ellipsize = android.text.TextUtils.TruncateAt.END
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))

        if (note.pinned) {
            head.addView(TextView(this).apply {
                text = "置顶"
                textSize = 11f
                typeface = Ui.mono
                setTextColor(Ui.ACCENT_DEEP)
            }, gapParams(start = 8))
        }
        if (selectMode) {
            head.addView(TextView(this).apply {
                text = if (picked) "✓" else "○"
                textSize = 15f
                typeface = Ui.monoBold
                setTextColor(if (picked) Ui.ACCENT else Ui.MUTED)
            }, gapParams(start = 8))
        }
        card.addView(head)

        if (note.preview.isNotEmpty()) {
            card.addView(TextView(this).apply {
                text = note.preview
                textSize = 13f
                typeface = Ui.body
                setTextColor(Ui.INK_2)
                maxLines = 2
                setPadding(0, dpx(5), 0, 0)
            })
        }

        card.addView(TextView(this).apply {
            text = noteStamp(note.updatedAt)
            textSize = 11f
            typeface = Ui.mono
            setTextColor(Ui.MUTED)
            setPadding(0, dpx(6), 0, 0)
        })

        return card
    }

    private fun toggle(id: String) {
        if (!selected.add(id)) selected.remove(id)
        showList()
    }

    // ---------- 编辑 ----------

    private fun showEditor(note: Note) {
        editing = note

        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dpx(20), statusBarHeight() + dpx(10), dpx(20), dpx(20))
        }

        val titleRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        titleRow.addView(pill("‹ 便签", small = true).apply {
            setOnClickListener {
                persist()
                showList()
            }
        })
        titleRow.addView(TextView(this).apply {
            text = noteStamp(note.updatedAt).ifEmpty { "便签" }
            textSize = 12f
            typeface = Ui.mono
            setTextColor(Ui.MUTED)
            gravity = Gravity.CENTER
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        titleRow.addView(pill("发到系统便签", small = true).apply {
            setOnClickListener { shareOut() }
        })
        column.addView(titleRow)

        titleInput = EditText(this).apply {
            setText(note.title)
            hint = "标题（可留空）"
            textSize = 18f
            typeface = Ui.medium
            setTextColor(Ui.INK)
            setHintTextColor(Ui.MUTED)
            background = null
            setPadding(0, dpx(14), 0, dpx(6))
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_CAP_SENTENCES
            addTextChangedListener(watcher)
        }
        column.addView(titleInput)

        column.addView(pageRule(topMargin = 0))

        bodyInput = EditText(this).apply {
            setText(note.body)
            hint = "随手记一段…"
            textSize = 15f
            typeface = Ui.body
            setTextColor(Ui.INK)
            setHintTextColor(Ui.MUTED)
            background = null
            gravity = Gravity.TOP
            inputType = InputType.TYPE_CLASS_TEXT or
                InputType.TYPE_TEXT_FLAG_MULTI_LINE or
                InputType.TYPE_TEXT_FLAG_CAP_SENTENCES
            setPadding(0, dpx(12), 0, dpx(12))
            addTextChangedListener(watcher)
        }
        column.addView(bodyInput, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f
        ))

        val actions = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setPadding(0, dpx(10), 0, 0)
        }
        actions.addView(pill(if (note.pinned) "取消置顶" else "置顶", small = true).apply {
            setOnClickListener {
                persist()
                val current = editing ?: return@setOnClickListener
                val flipped = current.copy(
                    pinned = !current.pinned,
                    updatedAt = System.currentTimeMillis(),
                    dirty = true,
                )
                db.saveNote(flipped)
                showEditor(flipped)
            }
        }, gapParams(weight = 1f))
        actions.addView(pill("删除", small = true).apply {
            setTextColor(Ui.WARN)
            setOnClickListener {
                val current = editing ?: return@setOnClickListener
                db.saveNote(
                    current.copy(
                        title = titleInput.text.toString(),
                        body = bodyInput.text.toString(),
                        deleted = true,
                        updatedAt = System.currentTimeMillis(),
                        dirty = true,
                    )
                )
                editing = null
                showList()
            }
        }, gapParams(weight = 1f, start = 8))
        column.addView(actions)

        setContentView(ScrollView(this).apply {
            setBackgroundColor(Ui.PAPER)
            addView(column)
        })

        bodyInput.requestFocus()
        titleInput.setSelection(titleInput.text.length)
    }

    /** 内容变了才写库；写的时候刷新 modified 时间并标脏，下一轮同步才会被推上去。 */
    private fun persist() {
        saveHandler.removeCallbacks(saveRunnable)
        val note = editing ?: return

        val title = titleInput.text.toString()
        val body = bodyInput.text.toString()
        if (note.title == title && note.body == body) return

        val updated = note.copy(
            title = title,
            body = body,
            updatedAt = System.currentTimeMillis(),
            dirty = true,
        )
        db.saveNote(updated)
        editing = updated
    }

    // ---------- 收：分享进来 ----------

    /** 一次收多条：每条文本各落一条便签；只有一条时按老路子直接进编辑器。 */
    private fun importIncoming(texts: List<String>, subject: String?) {
        val created = texts.map { body ->
            Note.create(title = if (texts.size == 1) subject.orEmpty() else "", body = body)
        }
        created.forEach { db.saveNote(it) }

        selectMode = false
        selected.clear()

        if (created.size == 1) {
            showEditor(created.first())
        } else {
            showList()
            toast("已收下 ${created.size} 条便签")
        }
    }

    private fun sharedTexts(intent: Intent?): List<String> {
        if (intent == null) return emptyList()
        val type = intent.type ?: return emptyList()
        if (!type.startsWith("text/")) return emptyList()

        val out = when (intent.action) {
            Intent.ACTION_SEND, Intent.ACTION_SEND_MULTIPLE -> extraTexts(intent).toMutableList()
            else -> return emptyList()
        }
        // 有些应用把多条文本做成多个 text/* 文件流，EXTRA_TEXT 是空的
        if (out.isEmpty()) out.addAll(readStreams(intent))
        return out.map { it.trim() }.filter { it.isNotEmpty() }
    }

    /** EXTRA_TEXT 的形态不止一种：单个字符串、ArrayList（标准写法）、String[]（部分发送方）。 */
    private fun extraTexts(intent: Intent): List<String> {
        val raw: Any = intent.extras?.get(Intent.EXTRA_TEXT) ?: return emptyList()
        return when (raw) {
            is CharSequence -> listOf(raw.toString())
            is ArrayList<*> -> raw.filterIsInstance<CharSequence>().map { it.toString() }
            is Array<*> -> raw.filterIsInstance<CharSequence>().map { it.toString() }
            else -> emptyList()
        }
    }

    private fun readStreams(intent: Intent): List<String> {
        @Suppress("DEPRECATION")
        val uris: List<Uri> = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java).orEmpty()
        } else {
            intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM).orEmpty()
        }

        val texts = mutableListOf<String>()
        uris.forEach { uri ->
            runCatching {
                contentResolver.openInputStream(uri)?.use { input ->
                    val text = input.bufferedReader().readText()
                    if (text.isNotBlank()) texts.add(text)
                }
            }
        }
        return texts
    }

    private fun sharedSubject(intent: Intent?): String? =
        intent?.getStringExtra(Intent.EXTRA_SUBJECT)?.takeIf { it.isNotBlank() }

    // ---------- 发：分享出去 ----------

    /** 交给系统：ACTION_SEND 会列出设备上的便签 / 备忘录应用，选一个就能存进去。 */
    private fun shareOut() {
        persist()
        val note = editing ?: return
        val text = noteText(note)
        if (text.isBlank()) return

        val send = Intent(Intent.ACTION_SEND).apply {
            type = "text/plain"
            putExtra(Intent.EXTRA_SUBJECT, note.displayTitle)
            putExtra(Intent.EXTRA_TEXT, text)
        }
        startActivity(Intent.createChooser(send, "发送到系统便签"))
    }

    /** 多选导出。系统便签只接单条 SEND，所以默认合并成一条，另给逐条的多条模式。 */
    private fun shareSelected() {
        val notes = selected.mapNotNull { db.note(it) }.filter { !it.deleted }
        if (notes.isEmpty()) {
            toast("先勾几条再分享")
            return
        }
        if (notes.size == 1) {
            shareSingle(notes.first())
            return
        }

        AlertDialog.Builder(this)
            .setTitle("分享 ${notes.size} 条便签")
            .setItems(
                arrayOf("合并成一条发送", "分开多条发送（仅部分应用支持）")
            ) { _, which ->
                if (which == 0) shareMerged(notes) else shareSplit(notes)
            }
            .setNegativeButton("取消", null)
            .show()
    }

    private fun shareSingle(note: Note) {
        val send = Intent(Intent.ACTION_SEND).apply {
            type = "text/plain"
            putExtra(Intent.EXTRA_SUBJECT, note.displayTitle)
            putExtra(Intent.EXTRA_TEXT, noteText(note))
        }
        startActivity(Intent.createChooser(send, "分享便签"))
    }

    private fun shareMerged(notes: List<Note>) {
        val text = notes.joinToString("\n\n———\n\n") { noteText(it) }
        val send = Intent(Intent.ACTION_SEND).apply {
            type = "text/plain"
            putExtra(Intent.EXTRA_SUBJECT, "滴答便签 ${notes.size} 条")
            putExtra(Intent.EXTRA_TEXT, text)
        }
        startActivity(Intent.createChooser(send, "分享 ${notes.size} 条便签"))
    }

    private fun shareSplit(notes: List<Note>) {
        val send = Intent(Intent.ACTION_SEND_MULTIPLE).apply {
            type = "text/plain"
            putStringArrayListExtra(Intent.EXTRA_TEXT, ArrayList(notes.map { noteText(it) }))
        }
        startActivity(Intent.createChooser(send, "分享 ${notes.size} 条便签"))
    }

    private fun deleteSelected() {
        val targets = selected.toList()
        if (targets.isEmpty()) {
            toast("先勾几条再删除")
            return
        }
        AlertDialog.Builder(this)
            .setMessage("删除 ${targets.size} 条便签？两端都会消失")
            .setPositiveButton("删除") { _, _ ->
                val now = System.currentTimeMillis()
                targets.forEach { id ->
                    db.note(id)?.let { db.saveNote(it.copy(deleted = true, updatedAt = now, dirty = true)) }
                }
                selectMode = false
                selected.clear()
                showList()
                toast("已删除 ${targets.size} 条")
            }
            .setNegativeButton("取消", null)
            .show()
    }

    private fun noteText(note: Note): String = buildString {
        if (note.title.isNotBlank()) append(note.title.trim()).append("\n\n")
        append(note.body)
    }

    private fun toast(message: String) =
        Toast.makeText(this, message, Toast.LENGTH_SHORT).show()

    private fun statusBarHeight(): Int {
        val id = resources.getIdentifier("status_bar_height", "dimen", "android")
        return if (id > 0) resources.getDimensionPixelSize(id) else dpx(24)
    }
}
