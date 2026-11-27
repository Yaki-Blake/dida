package com.didalite.android

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.graphics.drawable.GradientDrawable
import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast

/**
 * 便签面板：列表 + 多选 + 分享，挂进「专注」页当一个子 tab。
 *
 * 编辑走 [NotesActivity]（传 note_id / new_note 直接进全屏编辑页），
 * 这样编辑与分享那套逻辑不用在这里再抄一遍；编辑完回来自会重画列表。
 */
class NotesPanel(
    private val act: Activity,
    private val db: Db,
    private val openEditor: (noteId: String?, isNew: Boolean) -> Unit,
) {

    private lateinit var listBox: LinearLayout
    private lateinit var header: LinearLayout
    private lateinit var selectActions: LinearLayout

    private var selectMode = false
    private val selected = linkedSetOf<String>()

    /** 正在同步：防止连点重复推拉。 */
    private var syncing = false

    var onChanged: (() -> Unit)? = null

    fun build(): View {
        val scroll = ScrollView(act).apply {
            isFillViewport = true
            setBackgroundColor(Ui.PAPER)
        }
        val column = LinearLayout(act).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(act.dpx(20), act.dpx(10), act.dpx(20), act.dpx(20))
        }

        header = LinearLayout(act).apply { orientation = LinearLayout.VERTICAL }
        column.addView(header)

        selectActions = LinearLayout(act).apply { orientation = LinearLayout.HORIZONTAL }

        column.addView(act.pageRule(topMargin = 18))
        listBox = LinearLayout(act).apply { orientation = LinearLayout.VERTICAL }
        column.addView(listBox)

        scroll.addView(column)
        render()
        return scroll
    }

    fun render() {
        header.removeAllViews()
        if (selectMode) {
            header.addView(selectHeader())
            header.addView(TextView(act).apply {
                text = "勾选后点「分享」导出，或「删除」清掉"
                textSize = 12f
                typeface = Ui.body
                setTextColor(Ui.MUTED)
                setPadding(0, act.dpx(8), 0, 0)
            })
            selectActions.removeAllViews()
            selectActions.addView(act.pill("全选", small = true).apply {
                setOnClickListener {
                    val all = db.notes().map { it.id }
                    if (selected.size == all.size) selected.clear() else {
                        selected.clear(); selected.addAll(all)
                    }
                    render()
                }
            }, act.gapParams(weight = 1f))
            selectActions.addView(act.pill("删除", small = true).apply {
                setTextColor(Ui.WARN)
                setOnClickListener { deleteSelected() }
            }, act.gapParams(weight = 1f, start = 8))
            header.addView(selectActions, topGap(12))
        } else {
            header.addView(normalHeader())
        }

        listBox.removeAllViews()
        val notes = db.notes()
        selected.retainAll(notes.map { it.id }.toSet())
        if (notes.isEmpty()) {
            listBox.addView(TextView(act).apply {
                text = if (selectMode) "没有可选的便签" else "还没有便签\n点右上角「+ 新建」写第一条"
                textSize = 14f
                typeface = Ui.body
                setTextColor(Ui.MUTED)
                gravity = Gravity.CENTER
                setPadding(0, act.dpx(48), 0, act.dpx(48))
            })
        } else {
            notes.forEach { listBox.addView(noteRow(it), topGap(10)) }
        }
        onChanged?.invoke()
    }

    private fun normalHeader(): View {
        val titleRow = LinearLayout(act).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        titleRow.addView(View(act), LinearLayout.LayoutParams(0, 1, 1f))
        titleRow.addView(act.pill("+ 新建", small = true).apply {
            setOnClickListener { openEditor(null, true) }
        }, act.gapParams(start = 6))
        titleRow.addView(act.pill("同步", primary = true, small = true).apply {
            setOnClickListener { doSync() }
        }, act.gapParams(start = 6))
        return titleRow
    }

    private fun selectHeader(): View {
        val row = LinearLayout(act).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        row.addView(act.pill("取消", small = true).apply {
            setOnClickListener {
                selectMode = false
                selected.clear()
                render()
            }
        })
        row.addView(TextView(act).apply {
            text = "已选 ${selected.size} 条"
            textSize = 13f
            typeface = Ui.mono
            setTextColor(Ui.INK_2)
            gravity = Gravity.CENTER
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        row.addView(act.pill("分享", primary = true, small = true).apply {
            isEnabled = selected.isNotEmpty()
            setOnClickListener { shareSelected() }
        })
        return row
    }

    private fun topGap(px: Int) = LinearLayout.LayoutParams(
        ViewGroup.LayoutParams.MATCH_PARENT,
        ViewGroup.LayoutParams.WRAP_CONTENT,
    ).apply { topMargin = act.dpx(px) }

    private fun noteRow(note: Note): View {
        val picked = note.id in selected
        val card = LinearLayout(act).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(act.dpx(14), act.dpx(12), act.dpx(14), act.dpx(12))
            background = GradientDrawable().apply {
                cornerRadius = act.dpx(12).toFloat()
                setColor(if (picked) Ui.PAPER_3 else Ui.PAPER_2)
                setStroke(act.dpx(if (picked) 2 else 1), if (picked) Ui.ACCENT else Ui.RULE)
            }
            setOnClickListener {
                if (selectMode) toggle(note.id) else openEditor(note.id, false)
            }
            setOnLongClickListener {
                if (!selectMode) {
                    selectMode = true
                    selected.clear()
                    selected.add(note.id)
                    render()
                }
                true
            }
        }

        val head = LinearLayout(act).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        head.addView(TextView(act).apply {
            text = note.displayTitle
            textSize = 16f
            typeface = Ui.medium
            setTextColor(Ui.INK)
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        if (note.pinned) {
            head.addView(TextView(act).apply {
                text = "置顶"
                textSize = 11f
                typeface = Ui.mono
                setTextColor(Ui.ACCENT_DEEP)
            }, act.gapParams(start = 8))
        }
        if (selectMode) {
            head.addView(TextView(act).apply {
                text = if (picked) "✓" else "○"
                textSize = 15f
                typeface = Ui.monoBold
                setTextColor(if (picked) Ui.ACCENT else Ui.MUTED)
            }, act.gapParams(start = 8))
        }
        card.addView(head)

        if (note.preview.isNotEmpty()) {
            card.addView(TextView(act).apply {
                text = note.preview
                textSize = 13f
                typeface = Ui.body
                setTextColor(Ui.INK_2)
                maxLines = 2
                setPadding(0, act.dpx(5), 0, 0)
            })
        }
        card.addView(TextView(act).apply {
            text = noteStamp(note.updatedAt)
            textSize = 11f
            typeface = Ui.mono
            setTextColor(Ui.MUTED)
            setPadding(0, act.dpx(6), 0, 0)
        })
        return card
    }

    private fun toggle(id: String) {
        if (!selected.add(id)) selected.remove(id)
        render()
    }

    private fun doSync() {
        if (syncing) return
        syncing = true
        toast("同步中…")
        Thread {
            val message = try {
                SyncEngine.run(act.applicationContext, db)
            } catch (e: ApiException) {
                if (e.code == 401) db.setMeta("token", null)
                "同步失败：" + (e.message ?: "HTTP ${e.code}")
            } catch (e: Exception) {
                "同步失败：" + (e.message ?: e.javaClass.simpleName)
            }
            act.runOnUiThread {
                syncing = false
                toast(message)
                render()
            }
        }.start()
    }

    private fun shareSelected() {
        val notes = selected.mapNotNull { db.note(it) }.filter { !it.deleted }
        if (notes.isEmpty()) {
            toast("先勾几条再分享")
            return
        }
        val text = notes.joinToString("\n\n———\n\n") { noteText(it) }
        val send = Intent(Intent.ACTION_SEND).apply {
            type = "text/plain"
            putExtra(Intent.EXTRA_SUBJECT, "滴答便签 ${notes.size} 条")
            putExtra(Intent.EXTRA_TEXT, text)
        }
        act.startActivity(Intent.createChooser(send, "分享 ${notes.size} 条便签"))
    }

    private fun deleteSelected() {
        val targets = selected.toList()
        if (targets.isEmpty()) {
            toast("先勾几条再删除")
            return
        }
        AlertDialog.Builder(act)
            .setMessage("删除 ${targets.size} 条便签？两端都会消失")
            .setPositiveButton("删除") { _, _ ->
                val now = System.currentTimeMillis()
                targets.forEach { id ->
                    db.note(id)?.let { db.saveNote(it.copy(deleted = true, updatedAt = now, dirty = true)) }
                }
                selectMode = false
                selected.clear()
                render()
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
        Toast.makeText(act, message, Toast.LENGTH_SHORT).show()
}
