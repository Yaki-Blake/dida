package com.didalite.android

import android.app.Activity
import android.app.Dialog
import android.content.Intent
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.drawable.ColorDrawable
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Bundle
import android.text.InputType
import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import java.util.Locale

/**
 * 学业页：学分绩点。
 *
 * 第二课堂已经独立成底栏的「二课堂」一级入口，这里不再放它。
 * 数据走教务（强智 jsxsd，要验证码）：抓到的明细存 Db，下次打开先显示缓存，再手动刷新。
 */
class StudyActivity : Activity() {

    private companion object {
        const val TAB_GRADES = 0
        const val TOP_PADDING_DP = 10
    }

    private lateinit var db: Db
    private var tab = TAB_GRADES
    private var busy = false

    private var grades: EduGrades.Report? = null

    private lateinit var tabs: LinearLayout
    private lateinit var body: LinearLayout

    /** 抓取中的波动圆点动画，render 重画前先收掉。 */
    private var busyDots: List<android.animation.ObjectAnimator> = emptyList()

    /** 当前在等什么，busy 时显示；空串用默认文案。 */
    private var busyLabel = ""

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        db = Db(this)
        grades = EduStore.loadGrades(db)
        setContentView(NavBar.wrap(this, buildUi(), NavBar.STUDY))
        applyDidaTheme()
        render()
    }

    override fun onDestroy() {
        busyDots.forEach { it.cancel() }
        super.onDestroy()
        db.close()
    }

    // ---------- 骨架 ----------

    private fun buildUi(): View {
        val column = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        column.setPadding(dpx(20), statusBarHeight() + dpx(TOP_PADDING_DP), dpx(20), dpx(4))

        val top = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        tabs = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        top.addView(tabs, gapParams(weight = 1f, end = 8))
        top.addView(button("刷新") { refresh() })
        column.addView(top)
        column.addView(pageRule(10))

        body = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        column.addView(
            // fillViewport：内容不足一屏时 body 也撑满，空态才能居中显示
            ScrollView(this).apply { isFillViewport = true; addView(body) },
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f),
        )
        return column
    }

    private fun render() {
        busyDots.forEach { it.cancel() }
        busyDots = emptyList()
        tabs.removeAllViews()
        tabs.addView(tabPill("学分绩点", TAB_GRADES), gapParams(weight = 1f))

        body.removeAllViews()
        if (busy) body.addView(busyRow())
        renderGrades()
    }

    /** 抓取中：大字提示 + 三个圆点错峰上下波动。 */
    private fun busyRow(): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        row.setPadding(0, dpx(16), 0, dpx(4))
        row.addView(TextView(this).apply {
            text = busyLabel.ifBlank { "正在抓取成绩" }
            textSize = 18f
            typeface = Ui.medium
            setTextColor(Ui.INK)
        })
        val dots = (0..2).map {
            TextView(this).apply {
                text = "•"
                textSize = 24f
                typeface = Ui.medium
                setTextColor(Ui.ACCENT)
                setPadding(dpx(3), 0, 0, 0)
            }
        }
        val up = -dpx(6).toFloat()
        busyDots = dots.mapIndexed { i, dot ->
            android.animation.ObjectAnimator.ofFloat(dot, "translationY", 0f, up, 0f).apply {
                duration = 800
                startDelay = i * 150L
                repeatCount = android.animation.ValueAnimator.INFINITE
                interpolator = android.view.animation.AccelerateDecelerateInterpolator()
                start()
            }
        }
        dots.forEach { row.addView(it) }
        return row
    }

    private fun tabPill(label: String, target: Int): TextView =
        pill(label, primary = tab == target, small = true).also { view ->
            view.setOnClickListener {
                if (tab == target) return@setOnClickListener
                tab = target
                render()
            }
        }

    private fun renderGrades() {
        val report = grades
        if (report == null) {
            emptyState(
                title = "还没有学分绩点",
                hint = "用教务系统的学号与密码，验证码和课表那边一样。",
                action = "登录教务",
            )
            return
        }
        // 登录成功但一条成绩都没有：屏幕正中显示大字「暂无成绩」，不退回登录页
        if (report.items.isEmpty()) {
            body.addView(TextView(this).apply {
                text = "暂无成绩"
                textSize = 26f
                typeface = Ui.display
                setTextColor(Ui.INK_2)
                gravity = Gravity.CENTER
            }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))
            return
        }

        body.addView(
            statRow(
                "绩点" to format(report.gpa, 2),
                "加权均分" to format(report.average, 1),
                "已修学分" to format(report.earned, 1),
            )
        )
        if (report.failed > 0) {
            body.addView(caption("挂科 ${format(report.failed, 1)} 学分，不计入已修", Ui.WARN, dpx(6)))
        }

        report.byTerm.forEach { (term, courses) ->
            body.addView(sectionTitle(term))
            courses.forEach { course ->
                val point = if (course.point > 0) format(course.point, 1) else "—"
                body.addView(
                    line(
                        name = course.name,
                        right = "${course.score} · $point",
                        color = if (EduGrades.passed(course)) Ui.INK else Ui.WARN,
                    )
                )
            }
        }
    }

    private fun emptyState(title: String, hint: String, action: String) {
        body.addView(TextView(this).apply {
            text = title
            textSize = 18f
            typeface = Ui.medium
            setTextColor(Ui.INK)
            setPadding(0, dpx(22), 0, 0)
        })
        body.addView(TextView(this).apply {
            text = hint
            textSize = 13f
            typeface = Ui.body
            setTextColor(Ui.MUTED)
            setPadding(0, dpx(8), 0, dpx(16))
        })
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        row.addView(
            button(action, primary = true, small = false) { askLogin() },
            gapParams(weight = 1f, end = 8),
        )
        row.addView(button("用浏览器打开", small = false) { openBrowser() }, gapParams(weight = 1f))
        body.addView(row)
    }

    // ---------- 抓取 ----------

    private fun refresh() {
        if (busy) {
            toast("正在抓取，稍等")
            return
        }
        askLogin()
    }

    private fun askLogin() {
        if (busy) {
            toast("正在抓取，稍等")
            return
        }
        val column = dialogCard("登录教务")
        val user = input("学号", InputType.TYPE_CLASS_NUMBER)
        user.setText(db.getMeta("jwxt_xh").orEmpty())
        val pwd = input(
            "教务密码",
            InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD,
        )
        column.addView(user, wide())
        column.addView(pwd, wide().apply { topMargin = dpx(10) })

        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        actions.addView(button("取消") { }, gapParams(weight = 1f, end = 8))
        actions.addView(button("开始", primary = true) { }, gapParams(weight = 1f))
        column.addView(actions, wide().apply { topMargin = dpx(16) })

        val dialog = showCard(column)
        // 点「开始」才校验并关闭；「取消」直接关
        (actions.getChildAt(1) as TextView).setOnClickListener {
            val name = user.text.toString().trim()
            val pass = pwd.text.toString()
            if (name.isEmpty() || pass.isEmpty()) {
                toast("学号和密码都要填")
                return@setOnClickListener
            }
            dialog.dismiss()
            askCaptcha(name, pass)
        }
        (actions.getChildAt(0) as TextView).setOnClickListener { dialog.dismiss() }
    }

    /** 纸色圆角弹窗卡片：与课表页底部备注面板同一套样式。 */
    private fun dialogCard(title: String): LinearLayout {
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            background = GradientDrawable().apply {
                cornerRadius = dpx(22).toFloat()
                setColor(Ui.PAPER)
            }
            setPadding(dpx(20), dpx(18), dpx(20), dpx(18))
        }
        column.addView(TextView(this).apply {
            text = title
            textSize = 17f
            typeface = Ui.medium
            setTextColor(Ui.INK)
            setPadding(0, 0, 0, dpx(14))
        })
        return column
    }

    private fun showCard(content: LinearLayout): Dialog {
        val dialog = Dialog(this)
        dialog.requestWindowFeature(android.view.Window.FEATURE_NO_TITLE)
        dialog.setContentView(
            content,
            android.view.ViewGroup.LayoutParams(
                android.view.ViewGroup.LayoutParams.MATCH_PARENT,
                android.view.ViewGroup.LayoutParams.WRAP_CONTENT,
            ),
        )
        dialog.window?.apply {
            setBackgroundDrawable(ColorDrawable(Color.TRANSPARENT))
            setLayout(android.view.ViewGroup.LayoutParams.MATCH_PARENT,
                android.view.ViewGroup.LayoutParams.WRAP_CONTENT)
            setGravity(Gravity.CENTER)
            addFlags(android.view.WindowManager.LayoutParams.FLAG_DIM_BEHIND)
            setDimAmount(0.32f)
            setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
        }
        dialog.show()
        return dialog
    }

    private fun askCaptcha(user: String, pwd: String) {
        val session = EduHttp.session()
        busy = true
        busyLabel = "正在取验证码"
        render()
        Thread {
            runCatching { JwxtApi.captcha(JwxtApi.DEFAULT_BASE, session) }
                .onSuccess { bytes ->
                    runOnUiThread {
                        busy = false
                        busyLabel = ""
                        render()
                        showCaptcha(session, bytes, user, pwd)
                    }
                }
                .onFailure { error ->
                    runOnUiThread {
                        busy = false
                        busyLabel = ""
                        render()
                        fail("拿验证码失败", error)
                    }
                }
        }.start()
    }

    private fun showCaptcha(session: JwxtApi.Session, bytes: ByteArray, user: String, pwd: String) {
        val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
        if (bitmap == null) {
            fail("验证码不是图片", EduException("教务可能改版了，去浏览器里看一眼"))
            return
        }

        val column = dialogCard("验证码")
        column.addView(
            ImageView(this).apply {
                setImageBitmap(bitmap)
                adjustViewBounds = true
                setBackgroundColor(Ui.PAPER_2)
                setPadding(dpx(6), dpx(6), dpx(6), dpx(6))
            },
            LinearLayout.LayoutParams(dpx(195), dpx(60)),
        )
        val code = input("输入验证码", InputType.TYPE_CLASS_TEXT)
        column.addView(code, wide().apply { topMargin = dpx(10) })

        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        actions.addView(button("取消") { }, gapParams(end = 8))
        actions.addView(button("换一张") { }, gapParams(end = 8))
        actions.addView(button("登录", primary = true) { }, gapParams(weight = 1f))
        column.addView(actions, wide().apply { topMargin = dpx(16) })

        val dialog = showCard(column)
        (actions.getChildAt(0) as TextView).setOnClickListener { dialog.dismiss() }
        (actions.getChildAt(1) as TextView).setOnClickListener { dialog.dismiss(); askCaptcha(user, pwd) }
        (actions.getChildAt(2) as TextView).setOnClickListener {
            val text = code.text.toString().trim()
            if (text.isEmpty()) {
                toast("填一下验证码")
                return@setOnClickListener
            }
            dialog.dismiss()
            doGrades(session, user, pwd, text)
        }
    }

    private fun doGrades(session: JwxtApi.Session, user: String, pwd: String, code: String) {
        busy = true
        busyLabel = "正在登录教务并抓取成绩"
        render()
        Thread {
            runCatching {
                JwxtApi.login(JwxtApi.DEFAULT_BASE, session, user, pwd, code)
                EduGrades.fetch(JwxtApi.DEFAULT_BASE, session)
            }
                .onSuccess { report ->
                    db.setMeta("jwxt_xh", user)
                    EduStore.saveGrades(db, report)
                    runOnUiThread {
                        busy = false
                        busyLabel = ""
                        grades = report
                        render()
                    }
                }
                .onFailure { error ->
                    val message = error.message.orEmpty()
                    runOnUiThread {
                        busy = false
                        busyLabel = ""
                        render()
                        if (message.contains("验证码")) askCaptcha(user, pwd) else fail("抓成绩失败", error)
                    }
                }
        }.start()
    }

    private fun fail(title: String, error: Throwable) {
        val message = error.message ?: error.javaClass.simpleName
        val column = dialogCard(title)
        column.addView(TextView(this).apply {
            text = message
            textSize = 14f
            typeface = Ui.body
            setTextColor(Ui.INK_2)
        })
        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        actions.addView(button("知道了") { }, gapParams(weight = 1f, end = 8))
        actions.addView(button("用浏览器打开", primary = true) { }, gapParams(weight = 1f))
        column.addView(actions, wide().apply { topMargin = dpx(16) })

        val dialog = showCard(column)
        (actions.getChildAt(0) as TextView).setOnClickListener { dialog.dismiss() }
        (actions.getChildAt(1) as TextView).setOnClickListener {
            dialog.dismiss()
            openBrowser()
        }
    }

    private fun openBrowser() {
        runCatching { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("${JwxtApi.DEFAULT_BASE}/jsxsd/"))) }
            .onFailure { toast("这台设备没有能打开的浏览器") }
    }

    // ---------- 小控件 ----------

    private fun button(
        text: String,
        primary: Boolean = false,
        small: Boolean = true,
        onClick: () -> Unit,
    ): TextView = pill(text, primary = primary, small = small).also { view ->
        view.setOnClickListener { onClick() }
    }

    private fun statRow(vararg cells: Pair<String, String>): View {
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        row.setPadding(0, dpx(14), 0, dpx(4))
        cells.forEach { (label, value) ->
            val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            box.addView(TextView(this).apply {
                text = value
                textSize = 25f
                typeface = Ui.display
                setTextColor(Ui.INK)
            })
            box.addView(TextView(this).apply {
                text = label
                textSize = 11f
                typeface = Ui.mono
                setTextColor(Ui.MUTED)
                setPadding(0, dpx(2), 0, 0)
            })
            row.addView(box, gapParams(weight = 1f))
        }
        return row
    }

    private fun sectionTitle(text: String): View = TextView(this).apply {
        this.text = text
        textSize = 13f
        typeface = Ui.medium
        setTextColor(Ui.MUTED)
        setPadding(0, dpx(18), 0, dpx(2))
    }

    private fun caption(text: String, color: Int = Ui.MUTED, top: Int = 0): View = TextView(this).apply {
        this.text = text
        textSize = 11f
        typeface = Ui.mono
        setTextColor(color)
        setPadding(0, top, 0, 0)
    }

    private fun line(name: String, right: String, color: Int, big: Boolean = false): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        row.setPadding(0, dpx(if (big) 14 else 7), 0, dpx(if (big) 14 else 7))
        row.addView(TextView(this).apply {
            text = name
            textSize = if (big) 18f else 14f
            typeface = Ui.body
            setTextColor(Ui.INK)
            maxLines = 2
            ellipsize = TextUtils.TruncateAt.END
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        row.addView(TextView(this).apply {
            text = right
            textSize = if (big) 16f else 12.5f
            typeface = Ui.mono
            setTextColor(color)
            gravity = Gravity.END
        }, gapParams(start = 10))
        return row
    }

    private fun input(hint: String, type: Int): EditText = EditText(this).apply {
        this.hint = hint
        inputType = type
        textSize = 15f
        typeface = Ui.body
        setTextColor(Ui.INK)
        setHintTextColor(Ui.MUTED)
        setSingleLine()
    }

    private fun wide(): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)

    private fun toast(message: String) = Toast.makeText(this, message, Toast.LENGTH_SHORT).show()

    private fun statusBarHeight(): Int {
        val id = resources.getIdentifier("status_bar_height", "dimen", "android")
        return if (id > 0) resources.getDimensionPixelSize(id) else dpx(24)
    }

    private fun format(value: Double, digits: Int): String =
        String.format(Locale.CHINA, "%.${digits}f", value)
}