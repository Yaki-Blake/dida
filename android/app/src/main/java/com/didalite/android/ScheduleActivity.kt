package com.didalite.android

import android.app.Activity
import android.app.AlertDialog
import android.app.Dialog
import android.content.ActivityNotFoundException
import android.content.Intent
import android.content.res.ColorStateList
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.drawable.ColorDrawable
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.graphics.drawable.StateListDrawable
import android.net.Uri
import android.os.Bundle
import android.text.InputType
import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.util.Log
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.HorizontalScrollView
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import org.json.JSONObject
import java.time.LocalDate

/**
 * 课表页（启动页）：本机有课表就按周显示格子，没有就给「从教务导入」入口。
 * 导入走教务网页端表单登录（学号 + 密码 + 验证码），登录后直接抓课表页解析。
 * 账号密码只用于当次登录，密码不落盘；学号和学期存下来方便下次回填。
 */
class ScheduleActivity : Activity() {

    private lateinit var db: Db
    private lateinit var contentBox: LinearLayout

    /** 工具条一行三块：导入 / 周数+翻周 / 本周，三者同高同排。 */
    private lateinit var importButton: View
    private lateinit var weekNavGroup: View
    private lateinit var weekLabel: android.widget.TextView
    private lateinit var todayButton: View

    /** 一次导入的上下文：密码只在内存里挂着，成功或取消就丢。 */
    private class Pending(
        val user: String,
        val pwd: String,
        val term: String,
        val session: JwxtApi.Session,
    )

    private var pending: Pending? = null

    /** 当前看的周次，0 表示还没初始化。 */
    private var week = 0

    private var renderedRaw: String? = null
    private var renderedWeek = Int.MIN_VALUE
    private var cachedRaw: String? = null
    private var cachedEntries: List<ScheduleText.Entry> = emptyList()

    /** 表格线：比纸色深一点点，够看出来又不抢课块。 */
    private val GRID = 0xFFE0E9E1.toInt()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        db = Db(this)
        setContentView(scheduleRoot(buildUi()))
        applyDidaTheme()
    }

    /**
     * 课表页不用 NavBar.wrap 的「内容底部垫底栏高度」：底栏变高后被压缩的那块空间
     * 直接让课表覆盖——课表网格铺满整屏、底部被底栏盖住，课程块大小不受影响。
     */
    private fun scheduleRoot(content: View): View {
        val root = FrameLayout(this).apply { setBackgroundColor(Ui.PAPER) }
        root.addView(content, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT
        ))
        root.addView(NavBar.bar(this, NavBar.SCHEDULE), FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, dpx(NavBar.BAR_HEIGHT_DP), Gravity.BOTTOM
        ).apply { bottomMargin = dpx(NavBar.BAR_LIFT_DP) })
        return root
    }

    override fun onResume() {
        super.onResume()
        // 冷启动或切回课表页时顺手在后台同步一轮：日历里新写的日程不必再去「专注」页点同步。
        // 内部有 30 秒节流、没登入就跳过、失败静默，不打扰课表页。
        Thread { SyncEngine.autoRun(applicationContext, db) }.start()
        // 只有课表文本或周次变了才重建整张格子；从别的 tab 切回来不再重复渲染。
        val raw = db.getMeta("schedule_text")
        if (raw == renderedRaw && week == renderedWeek) return
        if (renderedWeek == Int.MIN_VALUE) {
            // 第一次进来：先让标题和底栏出一帧，格子随后补上，别让冷启动卡在空白上
            contentBox.post { render() }
        } else {
            render()
        }
    }

    // ---------- 界面 ----------

    private fun buildUi(): View {
        val root = ScrollView(this).apply {
            isFillViewport = true
            setBackgroundColor(Ui.PAPER)
        }
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            // 顶部 TOP_PADDING_DP（10dp）：与首页/便签/闹钟同一水平（y=244px），
            // 四页切换时工具条不会上下跳。底部要留出底栏（NavBar 65dp）的高度再多一点，
            // 否则滚到最底时第 10 节的色块会被覆盖在课表之上的底栏盖住、显示不全。
            // 底栏上提 BAR_LIFT_DP 后，内容底部要多让出这段高度，滚到最底第十节才不被盖住
            setPadding(dpx(20), statusBarHeight() + dpx(TOP_PADDING_DP), dpx(20), dpx(NavBar.BAR_HEIGHT_DP + NavBar.BAR_LIFT_DP + 8))
        }

        // 工具条一行三块：导入（左）· 周数+翻周（居中）· 本周（右）。
        // 三者必须同高、同排：之前「导入」单独占一行、周数条另起一行，
        // 就出现了你说的上下错位。「导入」与「本周」都是两字小胶囊、同宽，
        // 所以中间那组拿到权重 1 后天然落在整行正中，不需要额外占位补偿。
        val toolbar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }

        importButton = pill("导入", small = true).apply {
            setOnClickListener { showCredentialsDialog() }
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, dpx(WEEK_NAV_HEIGHT)
            )
        }
        toolbar.addView(importButton)

        val navGroup = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER
            visibility = View.INVISIBLE
        }
        navGroup.addView(weekNavButton("◀", -1) { if (week > 1) { week--; saveWeek(); render() } })

        weekLabel = TextView(this).apply {
            text = "第 1 周"
            textSize = 16f
            typeface = Ui.medium
            setTextColor(Ui.INK)
            gravity = Gravity.CENTER
            setPadding(dpx(14), 0, dpx(14), 0)
        }
        navGroup.addView(weekLabel, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT, dpx(WEEK_NAV_HEIGHT)
        ).apply { gravity = Gravity.CENTER_VERTICAL })

        navGroup.addView(weekNavButton("▶", 1) { if (week < MAX_WEEK) { week++; saveWeek(); render() } })
        toolbar.addView(navGroup, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        weekNavGroup = navGroup

        todayButton = pill("本周", small = true).apply {
            setOnClickListener { week = currentWeek(); saveWeek(); render() }
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, dpx(WEEK_NAV_HEIGHT)
            )
            visibility = View.INVISIBLE
        }
        toolbar.addView(todayButton)

        column.addView(toolbar)

        contentBox = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
        }
        // 工具条到课表的间距 16 -> 8dp：工具条本身已经够高，不用再撑这段空
        column.addView(contentBox, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
        ).apply { topMargin = dpx(8) })

        root.addView(column)
        return root
    }

    /**
     * 按当前周次刷新工具条中间那段。
     * 没有课表时把「周数 + 本周」藏起来（用 INVISIBLE 而不是 GONE：
     * GONE 会让中间塌掉、「导入」贴到屏幕正中，INVISIBLE 保住布局位置）。
     */
    private fun updateToolbar(hasSchedule: Boolean) {
        weekLabel.text = "第 $week 周"
        weekNavGroup.visibility = if (hasSchedule) View.VISIBLE else View.INVISIBLE
        todayButton.visibility = if (hasSchedule) View.VISIBLE else View.INVISIBLE
    }

    private fun render() {
        val raw = db.getMeta("schedule_text")
        contentBox.removeAllViews()
        if (raw.isNullOrBlank()) {
            renderedRaw = null
            renderedWeek = week
            updateToolbar(hasSchedule = false)   // 没有课表就别显示周数
            renderEmpty()
            return
        }
        val entries = entriesOf(raw)
        if (entries.isEmpty()) {
            updateToolbar(hasSchedule = false)
            contentBox.addView(TextView(this).apply {
                text = ScheduleText.polish(raw)
                textSize = 14f
                typeface = Ui.mono
                setTextColor(Ui.INK)
            })
            renderedRaw = raw
            renderedWeek = week
            return
        }
        ensureWeek()
        updateToolbar(hasSchedule = true)
        contentBox.addView(weekGrid(entries), LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
        ).apply { topMargin = dpx(10) })
        renderedRaw = raw
        renderedWeek = week
    }

    /** 解析结果按原文缓存，翻周次时不必重新拆一遍。 */
    private fun entriesOf(raw: String): List<ScheduleText.Entry> {
        if (raw != cachedRaw) {
            cachedRaw = raw
            cachedEntries = ScheduleText.entries(raw)
        }
        return cachedEntries
    }

    private fun renderEmpty() {
        contentBox.addView(TextView(this).apply {
            text = "还没有课表"
            textSize = 16f
            typeface = Ui.medium
            setTextColor(Ui.INK_2)
        })
        contentBox.addView(TextView(this).apply {
            text = "输入教务学号和密码，在 App 里直接登录教务系统拉取课表。校外请先连学校 VPN。"
            textSize = 13f
            typeface = Ui.body
            setTextColor(Ui.MUTED)
            setPadding(0, dpx(6), 0, dpx(16))
        })
        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        actions.addView(pill("从教务导入", primary = true).apply {
            setOnClickListener { showCredentialsDialog() }
        })
        actions.addView(pill("用浏览器打开", small = true).apply {
            setOnClickListener { openBrowser() }
        }, gapParams(start = 8))
        contentBox.addView(actions)
    }

    // ---------- 按周看的格子课表 ----------

    /**
     * 翻周按钮：原 28×34dp 远低于 Android 无障碍建议的 48dp 触控尺寸，很难点准。
     * 现在 44×40dp，横向略扁——在手机竖屏里比正方形更贴合工具条，
     * 用固定 dp 尺寸而不是靠字形撑开，左右两个才严格一样大。
     */
    private fun weekNavButton(glyph: String, direction: Int, onClick: () -> Unit): TextView {
        val radius = dpx(12).toFloat()

        fun shape(fill: Int) = GradientDrawable().apply {
            cornerRadius = radius
            setColor(fill)
            setStroke(dpx(1), Ui.RULE)
        }

        val background = StateListDrawable().apply {
            addState(
                intArrayOf(),
                RippleDrawable(
                    ColorStateList.valueOf(Color.parseColor("#1F000000")),
                    shape(Ui.PAPER_2),
                    shape(Color.WHITE),
                ),
            )
        }

        return TextView(this).apply {
            text = glyph
            textSize = 14f
            typeface = Ui.medium
            gravity = Gravity.CENTER
            this.background = background
            setTextColor(Ui.INK_2)
            isClickable = true
            isFocusable = true
            contentDescription = if (direction < 0) "上一周" else "下一周"
            minWidth = 0
            minimumWidth = 0
            setPadding(0, 0, 0, 0)
            layoutParams = LinearLayout.LayoutParams(dpx(WEEK_NAV_WIDTH), dpx(WEEK_NAV_HEIGHT))
            setOnClickListener { onClick() }
        }
    }

    private fun weekGrid(entries: List<ScheduleText.Entry>): View {
        val periods = maxOf(entries.maxOfOrNull { ScheduleText.jieRange(it.jie).last } ?: 10, 10)
        val axisW = dpx(20)
        val lineW = dpx(1)
        // 七列铺满屏宽，不横向滚，一眼看全一周
        val colW = (resources.displayMetrics.widthPixels - dpx(40) - axisW) / 7 - lineW
        val innerW = colW - dpx(10)

        // 每天每节的块：起点 → 连着几节（连堂同一门课算一块，文字框也一起跨过去）
        val spans = LinkedHashMap<Pair<String, Int>, Int>()
        DAYS.forEach { day ->
            var p = 1
            while (p <= periods) {
                val hits = hitsAt(entries, day, p)
                val only = hits.singleOrNull()
                if (only == null) {
                    if (hits.isNotEmpty()) spans[day to p] = 1
                    p++
                    continue
                }
                var end = p
                while (end < periods) {
                    val next = hitsAt(entries, day, end + 1).singleOrNull() ?: break
                    if (!sameCourse(next, only)) break
                    end++
                }
                spans[day to p] = end - p + 1
                p = end + 1
            }
        }

        // 每个节次行固定高度：课程色块高度 = 跨越节数 × 固定节高，所有块整齐统一。
        // 不再按文字内容自适应撑行高（那样会让块高低不齐）。块内三行文字仍用
        // WRAP_CONTENT 自适应换行（见 courseChip）。
        // 72dp：一节课通常跨 2 节 = 144dp，够放课程名+教室+教师三行（约 86dp），
        // 又比原来的 100dp 紧凑，一屏能看到更多节；超屏靠外层 ScrollView 上下滑动。
        val rowHeight = dpx(72)
        val rowH = IntArray(periods + 1) { rowHeight }
        val totalH = (1..periods).sumOf { rowH[it] }
        Log.d("ScheduleGrid", "periods=$periods rowH=${rowH.joinToString(",")} totalH=$totalH screenH=${resources.displayMetrics.heightPixels}")

        val board = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }

        // 表头两行：上「周几」下「日期」（11.12 这样）。
        // 日期按第 1 周周一 = 2026-09-07 推算，随翻周一起变，
        // 不用往上翻也知道这一列是哪天。
        // 行高 38 -> 32dp：两行字本身只占 10+9sp，38dp 会在上下再空出一截。
        val head = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        head.addView(View(this), LinearLayout.LayoutParams(axisW, dpx(32)))
        val weekdays = weekDates()
        DAYS.forEachIndexed { index, day ->
            val col = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                gravity = Gravity.CENTER
            }
            col.addView(TextView(this).apply {
                text = day
                textSize = 12f
                typeface = Ui.medium
                setTextColor(Ui.ACCENT)
                gravity = Gravity.CENTER
            })
            col.addView(TextView(this).apply {
                text = weekdays.getOrNull(index) ?: ""
                textSize = 11f
                typeface = Ui.mono
                setTextColor(Ui.MUTED)
                gravity = Gravity.CENTER
            })
            head.addView(col, LinearLayout.LayoutParams(colW + lineW, dpx(32)))
        }
        board.addView(head)
        board.addView(View(this).apply { setBackgroundColor(GRID) },
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, lineW))

        val body = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }

        val axisCol = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        for (p in 1..periods) {
            axisCol.addView(TextView(this).apply {
                text = "$p"
                textSize = 11f
                typeface = Ui.mono
                setTextColor(Ui.MUTED)
                gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
            }, LinearLayout.LayoutParams(axisW, rowH[p]))
        }
        body.addView(axisCol, LinearLayout.LayoutParams(axisW, totalH))

        DAYS.forEach { day ->
            body.addView(View(this).apply { setBackgroundColor(GRID) },
                LinearLayout.LayoutParams(lineW, totalH))
            val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
            var p = 1
            while (p <= periods) {
                val span = spans[day to p]
                if (span == null) {
                    col.addView(cellBox(Ui.PAPER, emptyList()), LinearLayout.LayoutParams(colW, rowH[p]))
                    p++
                } else {
                    val h = (p until p + span).sumOf { rowH[it] }
                    val cellEntries = hitsAt(entries, day, p)
                    val chips = cellEntries.map { courseChip(it) }
                    col.addView(cellBox(cellEntries.firstOrNull()?.let { courseColor(it) } ?: Ui.PAPER, chips),
                        LinearLayout.LayoutParams(colW, h))
                    p += span
                }
            }
            body.addView(col, LinearLayout.LayoutParams(colW, totalH))
        }
        // 每条横线在哪些列上要让开（连堂块跨过的边界不画线）
        val skip = HashMap<Int, MutableList<IntRange>>()
        var dayX = axisW
        DAYS.forEach { day ->
            dayX += lineW
            val x0 = dayX
            val x1 = x0 + colW
            var s = 1
            while (s <= periods) {
                val span = spans[day to s]
                if (span == null) { s++; continue }
                if (span > 1) {
                    for (b in s until s + span - 1) {
                        skip.getOrPut(b) { mutableListOf() }.add(x0 until x1)
                    }
                }
                s += span
            }
            dayX = x1
        }

        val bodyWrap = FrameLayout(this)
        bodyWrap.addView(body, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, totalH
        ))
        // 横线整条贯穿整行，只在连堂块跨过的地方断一下
        bodyWrap.addView(object : View(this@ScheduleActivity) {
            override fun onDraw(canvas: Canvas) {
                super.onDraw(canvas)
                val paint = Paint().apply { color = GRID; style = Paint.Style.FILL }
                var y = 0
                for (p in 1 until periods) {
                    y += rowH[p]
                    val cuts = skip[p]?.sortedBy { it.first }
                    if (cuts.isNullOrEmpty()) {
                        canvas.drawRect(0f, y.toFloat(), width.toFloat(), (y + lineW).toFloat(), paint)
                    } else {
                        var start = 0
                        cuts.forEach { r ->
                            if (r.first > start) {
                                canvas.drawRect(start.toFloat(), y.toFloat(), r.first.toFloat(), (y + lineW).toFloat(), paint)
                            }
                            start = maxOf(start, r.last + 1)
                        }
                        if (start < width) {
                            canvas.drawRect(start.toFloat(), y.toFloat(), width.toFloat(), (y + lineW).toFloat(), paint)
                        }
                    }
                }
                canvas.drawRect(0f, (totalH - lineW).toFloat(), width.toFloat(), totalH.toFloat(), paint)
            }
        }, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, totalH))
        board.addView(bodyWrap, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, totalH
        ))

        return HorizontalScrollView(this).apply {
            isHorizontalScrollBarEnabled = false
            addView(board)
        }
    }

    /** 一格：课块是实心色片；网格线由整块画布统一画在最上层。 */
    private fun cellBox(color: Int, content: List<View>): View {
        val cell = FrameLayout(this)
        cell.setBackgroundColor(Ui.PAPER)
        if (content.isNotEmpty()) {
            val chip = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                background = GradientDrawable().apply {
                    setColor(color)
                    cornerRadius = dpx(10).toFloat()
                }
                setPadding(dpx(4), dpx(3), dpx(4), dpx(3))
            }
            // 内容自适应高度（WRAP_CONTENT），不撑满色块：固定分区总高
            // （课程名+教室+教师）超过本格高度时，weight=1 撑满会让内容溢出、
            // 盖住相邻色块与横线。色块背景仍撑满整格，空白处也能点。
            content.forEach {
                chip.addView(it, LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
                ))
            }
            // 整块可点：点空白处也触发第一个内容块的备注
            chip.setOnClickListener { content.first().performClick() }
            cell.addView(chip, FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT
            ).apply { setMargins(dpx(1), dpx(1), dpx(1), dpx(2)) })
        }
        // 底线永远压在最上层
        return cell
    }

    private fun courseColor(entry: ScheduleText.Entry) =
        COURSE_COLORS[Math.abs(entry.course.hashCode()) % COURSE_COLORS.size]

    /** 把几个块叠起来量一下要占多高（宽度固定、高度不限）。 */
    private fun measureStack(width: Int, chips: List<View>): Int {
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        chips.forEach { chip ->
            box.addView(chip, LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
            ))
        }
        box.measure(
            View.MeasureSpec.makeMeasureSpec(width, View.MeasureSpec.EXACTLY),
            View.MeasureSpec.makeMeasureSpec(0, View.MeasureSpec.UNSPECIFIED),
        )
        Log.d("ScheduleGrid", "measureStack w=$width n=${chips.size} h=${box.measuredHeight}")
        return box.measuredHeight
    }

    /** 这一格里该出现的课（同一格可能排了两门）。 */
    private fun hitsAt(entries: List<ScheduleText.Entry>, day: String, period: Int) = entries.filter {
        it.day == day && period in ScheduleText.jieRange(it.jie) &&
            ScheduleText.weekSet(it.weeks).contains(week)
    }

    private fun sameCourse(a: ScheduleText.Entry, b: ScheduleText.Entry) =
        a.course == b.course && a.room == b.room && a.weeks == b.weeks

    private fun courseChip(entry: ScheduleText.Entry, showText: Boolean = true): View {
        val box = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Color.TRANSPARENT)
        }
        if (!showText) return box
        // 三个分区：课程名 → 教室 → 教师，每行水平居中对齐。
        // 高度用 WRAP_CONTENT + minHeight：短文字保住原来舒展的分区间距（minHeight 兜底），
        // 长文字（窄列里教室地址、教师名会换成两三行）则自动长高，不再溢出盖住下一行。
        // 之前写死 32/20/20dp：一行地址换三行时要 42dp 却只给 20dp，直接压到教师那行，
        // 这就是「老师把地址盖住、显示不全」的根因；measureStack 也永远量成 82dp，
        // 行高 rowH 跟着撑不够。改成自适应后测量与渲染用的是同一套真实高度。
        box.addView(TextView(this).apply {
            text = entry.course
            // 11.5f：真机每列文本可用宽约 36.5dp，3 个中文字 ≈34.5dp 刚好填满、
            // 4 个字 ≈46dp 放不下 → 稳定保持「一行三个字」；字距色块边还有 chip
            // 的 4dp padding 兜底，不会贴合边框，也不会越界。
            textSize = 11.5f
            typeface = Ui.medium
            setTextColor(Ui.INK)
            gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
            minHeight = dpx(30)
        }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        box.addView(TextView(this).apply {
            text = entry.room
            textSize = 10f
            typeface = Ui.body
            setTextColor(Ui.INK_2)
            gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
            minHeight = dpx(20)
        }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dpx(5) })
        box.addView(TextView(this).apply {
            text = entry.teacher
            textSize = 10f
            typeface = Ui.body
            setTextColor(Ui.INK_2)
            gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
            minHeight = dpx(20)
        }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dpx(5) })
        val note = noteFor(entry)
        if (note.isNotBlank()) {
            box.addView(TextView(this).apply {
                text = "※ $note"
                textSize = 9f
                typeface = Ui.medium
                setTextColor(Ui.ACCENT_DEEP)
                gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
            }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dpx(5) })
        }
        box.setOnClickListener { showNoteSheet(entry) }
        return box
    }

    // ---------- 备注：单次（只看这一周）/ 长期（每周都显示） ----------

    private fun slotKey(entry: ScheduleText.Entry) = "${entry.day}|${entry.jie}|${entry.course}"

    private fun onceKey(entry: ScheduleText.Entry) = "$week|${slotKey(entry)}"

    private fun noteFor(entry: ScheduleText.Entry): String =
        notesOnce()[onceKey(entry)] ?: notesLong()[slotKey(entry)] ?: ""

    private fun notesOnce() = readNoteMap("schedule_note_once")

    private fun notesLong() = readNoteMap("schedule_note_long")

    private fun readNoteMap(key: String): MutableMap<String, String> {
        val map = mutableMapOf<String, String>()
        val raw = db.getMeta(key) ?: return map
        try {
            val obj = JSONObject(raw)
            obj.keys().forEach { k -> map[k] = obj.optString(k) }
        } catch (e: Exception) {
            // 存的不是合法 JSON 就当没有，别让课表页崩掉
        }
        return map
    }

    private fun writeNoteMap(key: String, map: Map<String, String>) {
        val obj = JSONObject()
        map.forEach { (k, v) -> obj.put(k, v) }
        db.setMeta(key, obj.toString())
    }

    /** 底部上滑的备注面板：两个输入框既是显示也是修改，点「完成」或点外面就存。 */
    private fun showNoteSheet(entry: ScheduleText.Entry) {
        val onceInput = input("单次备注（只在第 $week 周显示）", InputType.TYPE_CLASS_TEXT)
        onceInput.setText(notesOnce()[onceKey(entry)] ?: "")
        val longInput = input("长期备注（本学期每周都显示）", InputType.TYPE_CLASS_TEXT)
        longInput.setText(notesLong()[slotKey(entry)] ?: "")

        val sheet = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            background = GradientDrawable().apply {
                setColor(Ui.PAPER)
                cornerRadii = floatArrayOf(
                    dpx(22).toFloat(), dpx(22).toFloat(),
                    dpx(22).toFloat(), dpx(22).toFloat(),
                    0f, 0f, 0f, 0f,
                )
            }
            setPadding(dpx(20), dpx(12), dpx(20), dpx(22))
        }
        sheet.addView(View(this).apply { setBackgroundColor(Ui.RULE) },
            LinearLayout.LayoutParams(dpx(36), dpx(4)).apply { gravity = Gravity.CENTER_HORIZONTAL })
        sheet.addView(TextView(this).apply {
            text = entry.course
            textSize = 15f
            typeface = Ui.medium
            setTextColor(Ui.INK)
        }, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
        ).apply { topMargin = dpx(14) })
        sheet.addView(TextView(this).apply {
            text = listOf(entry.day, entry.jie, entry.room).filter { it.isNotBlank() }.joinToString(" · ")
            textSize = 11f
            typeface = Ui.body
            setTextColor(Ui.MUTED)
        }, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
        ).apply { topMargin = dpx(2) })
        sheet.addView(onceInput, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
        ).apply { topMargin = dpx(14) })
        sheet.addView(longInput, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
        ).apply { topMargin = dpx(8) })
        sheet.addView(pill("完成", primary = true).apply { setOnClickListener { } },
            LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply { gravity = Gravity.END; topMargin = dpx(16) })

        val dialog = Dialog(this)
        dialog.requestWindowFeature(android.view.Window.FEATURE_NO_TITLE)
        dialog.setContentView(sheet)
        dialog.window?.apply {
            setBackgroundDrawable(ColorDrawable(Color.TRANSPARENT))
            setLayout(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
            setGravity(Gravity.BOTTOM)
            addFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND)
            setDimAmount(0.32f)
            setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
        }
        dialog.setOnDismissListener { saveNotes(entry, onceInput, longInput); render() }
        (sheet.getChildAt(sheet.childCount - 1)).setOnClickListener { dialog.dismiss() }
        dialog.show()
        sheet.post {
            sheet.translationY = sheet.height.toFloat()
            sheet.animate().translationY(0f).setDuration(200).start()
        }
    }

    private fun saveNotes(entry: ScheduleText.Entry, onceInput: EditText, longInput: EditText) {
        val once = notesOnce()
        val long = notesLong()
        val one = onceInput.text.toString().trim()
        val forever = longInput.text.toString().trim()
        if (one.isBlank()) once.remove(onceKey(entry)) else once[onceKey(entry)] = one
        if (forever.isBlank()) long.remove(slotKey(entry)) else long[slotKey(entry)] = forever
        writeNoteMap("schedule_note_once", once)
        writeNoteMap("schedule_note_long", long)
    }

    private fun ensureWeek() {
        if (week <= 0) {
            week = db.getMeta("schedule_week")?.toIntOrNull()?.takeIf { it in 1..MAX_WEEK }
                ?: currentWeek()
        }
    }

    private fun saveWeek() = db.setMeta("schedule_week", week.toString())

    /** 学期第 1 周从 2026-09-07（周一）算起。 */
    private fun currentWeek(): Int {
        val start = LocalDate.of(2026, 9, 7).toEpochDay()
        val days = LocalDate.now().toEpochDay() - start
        return ((days / 7) + 1).toInt().coerceIn(1, MAX_WEEK)
    }

    /**
     * 当前这一周七天的日期，格式 `11.12`（月.日，都不补前导零）。
     * 只取月和日：年份在这一屏里没有信息量，宽度也只有一列那么点。
     */
    private fun weekDates(): List<String> {
        val monday = LocalDate.of(2026, 9, 7).plusWeeks((week - 1).toLong())
        return (0..6).map { offset ->
            val d = monday.plusDays(offset.toLong())
            "${d.monthValue}.${d.dayOfMonth}"
        }
    }

    // ---------- 导入：账号 → 验证码 → 登录 → 拉课表 ----------

    private fun showCredentialsDialog() {
        val box = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dpx(20), dpx(8), dpx(20), 0)
        }
        val userInput = input("学号", InputType.TYPE_CLASS_TEXT)
        val pwdInput = input("教务密码", InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD)
        val termInput = input("学期（如 2026-2027-1）", InputType.TYPE_CLASS_TEXT)
        userInput.setText(db.getMeta("jwxt_xh") ?: "")
        termInput.setText(db.getMeta("jwxt_term") ?: DEFAULT_TERM)
        box.addView(userInput)
        box.addView(pwdInput, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
        ).apply { topMargin = dpx(10) })
        box.addView(termInput, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
        ).apply { topMargin = dpx(10) })

        AlertDialog.Builder(this)
            .setTitle("从教务导入课表")
            .setMessage("密码只用于这次登录，不会存在手机上。")
            .setView(box)
            .setPositiveButton("下一步") { _, _ ->
                val user = userInput.text.toString().trim()
                val pwd = pwdInput.text.toString()
                val term = termInput.text.toString().trim()
                if (user.isBlank() || pwd.isBlank() || term.isBlank()) {
                    toast("学号、密码、学期都要填")
                    return@setPositiveButton
                }
                pending = Pending(user, pwd, term, JwxtApi.Session())
                loadCaptcha()
            }
            .setNegativeButton("取消", null)
            .show()
    }

    private fun loadCaptcha() {
        val p = pending ?: return
        toast("正在获取验证码…")
        Thread {
            try {
                val bytes = JwxtApi.captcha(JwxtApi.DEFAULT_BASE, p.session)
                val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                    ?: throw JwxtApi.JwxtException("验证码图片解不开")
                runOnUiThread { showCaptchaDialog(bitmap) }
            } catch (e: Exception) {
                val msg = e.message ?: e.javaClass.simpleName
                runOnUiThread { fail("取验证码失败", msg) }
            }
        }.start()
    }

    private fun showCaptchaDialog(bitmap: Bitmap) {
        val box = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dpx(20), dpx(12), dpx(20), 0)
        }
        box.addView(ImageView(this).apply {
            setImageBitmap(bitmap)
            scaleType = ImageView.ScaleType.FIT_CENTER
            adjustViewBounds = true
            setBackgroundColor(Ui.PAPER_2)
            setPadding(dpx(6), dpx(6), dpx(6), dpx(6))
        }, LinearLayout.LayoutParams(dpx(195), dpx(96)))
        val codeInput = input("验证码（看左边图）", InputType.TYPE_CLASS_TEXT)
        box.addView(codeInput, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
        ).apply { topMargin = dpx(12) })

        AlertDialog.Builder(this)
            .setTitle("输入验证码")
            .setView(box)
            .setPositiveButton("登录") { _, _ ->
                val code = codeInput.text.toString().trim()
                if (code.isBlank()) {
                    toast("验证码没填")
                    return@setPositiveButton
                }
                doImport(code)
            }
            .setNeutralButton("换一张") { _, _ -> loadCaptcha() }
            .setNegativeButton("取消") { _, _ -> pending = null }
            .show()
    }

    private fun doImport(code: String) {
        val p = pending ?: return
        toast("正在登录教务…")
        Thread {
            try {
                JwxtApi.login(JwxtApi.DEFAULT_BASE, p.session, p.user, p.pwd, code)
                val text = JwxtApi.schedule(JwxtApi.DEFAULT_BASE, p.session, p.term)
                db.setMeta("jwxt_xh", p.user)
                db.setMeta("jwxt_term", p.term)
                db.setMeta("schedule_text", text)
                pending = null
                runOnUiThread {
                    render()
                    toast("课表已导入")
                }
            } catch (e: Exception) {
                val msg = e.message ?: e.javaClass.simpleName
                runOnUiThread {
                    if (msg.contains("验证码")) {
                        // 验证码错了很常见，直接换一张重来，不用重填账号
                        toast(msg)
                        loadCaptcha()
                    } else {
                        fail("导入失败", msg)
                    }
                }
            }
        }.start()
    }

    private fun fail(title: String, msg: String) {
        pending = null
        AlertDialog.Builder(this)
            .setTitle(title)
            .setMessage("$msg\n\n也可以点「用浏览器打开」，在浏览器里登录教务后到电脑端同步。")
            .setPositiveButton("知道了", null)
            .show()
    }

    private fun openBrowser() {
        try {
            startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(JWXT_URL)))
        } catch (e: ActivityNotFoundException) {
            toast("没有可用的浏览器")
        }
    }

    private fun input(hint: String, inputType: Int) = EditText(this).apply {
        this.hint = hint
        this.inputType = inputType
        textSize = 15f
        setTextColor(Ui.INK)
        setHintTextColor(Ui.MUTED)
        layoutParams = LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT
        )
    }

    private fun toast(msg: String) = Toast.makeText(this, msg, Toast.LENGTH_SHORT).show()

    @Suppress("DEPRECATION")
    private fun statusBarHeight(): Int {
        val id = resources.getIdentifier("status_bar_height", "dimen", "android")
        return if (id > 0) resources.getDimensionPixelSize(id) else dpx(24)
    }

    private companion object {
        const val JWXT_URL = "http://jwxt.gdufe.edu.cn/jsxsd/"
        const val DEFAULT_TERM = "2026-2027-1"
        const val MAX_WEEK = 30

        /**
         * 工具条控件高度（dp）。
         * 与首页 / 便签 / 闹钟三页标题行的小胶囊保持一致（那边是 pill(small) 的 34dp），
         * 四页切换时按钮不会忽高忽低。所有控件共用这一个常量，保证同排齐高。
         */
        const val WEEK_NAV_WIDTH = 44
        const val WEEK_NAV_HEIGHT = 34

        /**
         * 顶部边距（状态栏之下）：10dp。
         * 和首页 / 便签 / 闹钟三页对齐，四页切换时工具条不会上下跳。
         * buildUi 与下面的高度换算共用这一个值，不许各写各的。
         */
        const val TOP_PADDING_DP = 10
        val DAYS = listOf("周一", "周二", "周三", "周四", "周五", "周六", "周日")

        /** 同名课程取同一个色，不同课基本不同色。 */
        val COURSE_COLORS = intArrayOf(
            0xFFE3F0E6.toInt(), 0xFFF7E7D6.toInt(), 0xFFE4EBF7.toInt(), 0xFFF3E3F0.toInt(),
            0xFFEAF3D9.toInt(), 0xFFF9E9E7.toInt(), 0xFFE0EEF0.toInt(), 0xFFEFE8D8.toInt(),
            0xFFE9E4F5.toInt(), 0xFFDFEEEA.toInt(), 0xFFF5EBDF.toInt(), 0xFFE6EDF2.toInt(),
        )
    }
}
