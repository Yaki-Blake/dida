package com.didalite.android

import android.app.Activity
import android.app.AlertDialog
import android.app.Dialog
import android.content.Context
import android.content.Intent
import android.content.res.ColorStateList
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.drawable.ColorDrawable
import android.graphics.drawable.GradientDrawable
import android.net.Uri
import android.os.Bundle
import android.text.InputType
import android.text.TextUtils
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import org.json.JSONObject
import java.util.Locale

/**
 * Hallmark · app screen · shape: second class
 * critique: 一屏只做一件事——顶部定「看哪一档」，中部是这一档的清单，底部定「看哪一个面」；
 * 层级靠字号与留白，颜色只标当前选中与状态。
 *
 * 第二课堂页：活动 / 组织 / 个人，三项二级导航压在这页自己的底部（一级导航之上）。
 *
 * - 活动：顶部左右切换「历史活动 | 报名中」，最右是线上/线下筛选（服务端不认这个参数，
 *   只能拿回列表后按 oto 客户端过滤）。列表进缓存，分「报名中」「历史」两档各存一份。
 * - 组织：社团/学生会清单 → 详情（简介、人数、加入、该组织办过的活动）。
 * - 个人：学分进度 + 我的组织 / 我的报名 / 我的申报 / 学籍信息 / 签到签退记录五个子页。
 *
 * 刷新：整页抖动是因为以前 busy 时把列表拆了重建、还把提示插在列表最前面。
 * 现在数据与结构分开——busy 只控制一层居中遮罩（半透明 + 圆形进度），不碰内容；
 * 页面到顶时下拉也能刷新，提示浮在列表上方淡入。
 *
 * 写操作只有两个（报名、加入组织），都要二次确认；成功后强制实时刷新，不吃缓存。
 * 学号密码记住了：密码用 Android Keystore 加密后存库。
 */
class SecondClassActivity : Activity() {

    private companion object {
        const val TAB_ACTS = 0
        const val TAB_ORGS = 1
        const val TAB_ME = 2
        const val TOP_PADDING_DP = 10
        const val PAGE_SIZE = 100

        /** 二级导航高度（dp）：比一级底栏矮一截，但仍然是一整条通栏，不是胶囊。 */
        const val SUB_BAR_HEIGHT_DP = 46

        /** 二级导航里每个标签胶囊的高度（dp）。 */
        const val SUB_CHIP_HEIGHT_DP = 28

        /** 下拉刷新：拖过 64dp 的 0.8 倍松手就刷。两处判定共用这几个数，别各写一份。 */
        const val PULL_THRESHOLD_DP = 64
        const val PULL_TRIGGER = 0.8f

        /** 起手 8dp 之内不算下拉，也不抢手势：点按和轻蹭都不该惊动滚动。 */
        const val PULL_DEAD_ZONE_DP = 8

        /** 线上/线下筛选。服务端 oto 参数不生效，这里只能客户端过滤。 */
        const val FILTER_ALL = 0
        const val FILTER_ONLINE = 1
        const val FILTER_OFFLINE = 2

        /** 线下活动的校区筛选：只在「线下」下出现。 */
        const val CAMPUS_ALL = 0
        const val CAMPUS_GUANGZHOU = 1
        const val CAMPUS_FOSHAN = 2

        /** 活动自身归属的校区，由活动名里的（广州）/（佛山）判断；没有标记的算全校。 */
        const val CAMPUS_BOTH = 3

        /** 调外部扫码 App 的请求码，仅用来接它的扫码结果。 */
        const val REQ_SCAN = 1001
    }

    private lateinit var db: Db
    private var tab = TAB_ACTS

    /** true = 历史活动（actiStatusId=5），false = 报名中（actiStatusId=1）。 */
    private var pastOnly = false
    private var onlineFilter = FILTER_ALL

    /** 只有 onlineFilter == FILTER_OFFLINE 时才生效的校区筛选。 */
    private var campusFilter = CAMPUS_ALL
    /** 组织分类：""=全部，college=学院(nature=3)，assoc=协会(nature=2 且名字含"协会")，club=其余社团。 */
    private var orgNature = ""
    private var orgKeyword = ""

    private var actsCurrent: List<SecondClassApi.Act>? = null
    private var actsPast: List<SecondClassApi.Act>? = null
    private var orgs: List<SecondClassApi.Org>? = null
    private var credit: EduSecondClass.Report? = null

    private var actsError: String? = null
    private var orgsError: String? = null

    private lateinit var topBar: LinearLayout
    private lateinit var topRule: View
    private lateinit var body: LinearLayout
    private lateinit var scroll: PullScroll
    private lateinit var pullHint: TextView
    private lateinit var subNav: LinearLayout
    private lateinit var busyOverlay: FrameLayout
    private lateinit var busyText: TextView

    private var busy = false

    /** 最近一次签到/签退的时间：用来让「签到签退记录」这一屏知道数据可能变了。 */
    private var lastSignAt = 0L

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        db = Db(this)
        actsCurrent = SecondClassStore.loadActs(db)
        actsPast = SecondClassStore.loadActs(db, past = true)
        credit = EduStore.loadSecond(db)

        setContentView(NavBar.wrap(this, buildUi(), NavBar.SECOND))
        applyDidaTheme()
        renderChrome()
        renderContent()

        // 有缓存先给看，后台悄悄换一份新的；没缓存就老实拉一次
        if (SecondClassStore.loadLogin(db) != null) {
            if (currentActs() == null) loadActs(silent = false) else autoRefresh()
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        db.close()
    }

    /**
     * 接外部扫码 App 的结果。zxing 的扫码约定把二维码内容放在 "SCAN_RESULT" 里，
     * 拿到后交给 onScanResult 解析、确认、提交。
     */
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != REQ_SCAN || resultCode != RESULT_OK) return
        val raw = data?.getStringExtra("SCAN_RESULT")
        if (raw.isNullOrBlank()) {
            toast("扫码没有拿到内容，改用手输口令")
            askSignCode()
            return
        }
        onScanResult(raw)
    }

    // ---------- 骨架 ----------

    private fun buildUi(): View {
        val root = FrameLayout(this)

        val column = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        // 左右不留白：滚动区要通到屏幕边，滚动条才不压卡片（留白改由 body 自己给）
        column.setPadding(0, statusBarHeight() + dpx(TOP_PADDING_DP), 0, 0)

        topBar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dpx(20), 0, dpx(20), 0)
        }
        column.addView(topBar)
        topRule = pageRule()
        column.addView(
            topRule,
            LinearLayout.LayoutParams(MATCH_PARENT, dpx(1)).apply {
                topMargin = dpx(10)
                leftMargin = dpx(20)
                rightMargin = dpx(20)
            },
        )

        // 左右 20dp 从外层搬到 body 自己身上：滚动条画在 ScrollView 的右边缘，
        // 而 ScrollView 原来被外层缩进 20dp，条子正好压在卡片右边框上。
        // 把滚动区拉到通栏、让内容自己留 20dp，条子就落到屏幕最右侧，不再挡字。
        body = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dpx(20), 0, dpx(20), 0)
        }

        val host = FrameLayout(this)
        scroll = PullScroll(this).apply {
            isFillViewport = true
            addView(body)
            onPull = { fraction ->
                val pull = fraction.coerceAtLeast(0f)
                // 内容跟着手指往下走一点点：只弹一颗小胶囊会让人以为页面卡住了，
                // 有位移才有「真的在拉」的手感（到位后不再跟着走，免得像橡皮筋）。
                val shift = (pull * dpx(56)).coerceAtMost(dpx(84).toFloat())
                body.translationY = shift
                pullHint.translationY = shift
                if (pull <= 0f) {
                    pullHint.alpha = 0f
                } else {
                    pullHint.alpha = minOf(1f, pull * 1.6f)
                    pullHint.text = if (pull >= PULL_TRIGGER) "松开刷新" else "下拉刷新"
                }
            }
            onRelease = { triggered -> if (triggered) beginRefresh() }
        }
        host.addView(scroll, FrameLayout.LayoutParams(MATCH_PARENT, MATCH_PARENT))

        pullHint = TextView(this).apply {
            text = "下拉刷新"
            textSize = 11f
            typeface = Ui.mono
            setTextColor(Ui.MUTED)
            setPadding(dpx(10), dpx(4), dpx(10), dpx(4))
            background = GradientDrawable().apply {
                cornerRadius = dpx(8).toFloat()
                setColor(Ui.PAPER_2)
                setStroke(dpx(1), Ui.RULE)
            }
            alpha = 0f
        }
        host.addView(
            pullHint,
            FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.TOP or Gravity.CENTER_HORIZONTAL,
            ).apply { topMargin = dpx(6) },
        )
        column.addView(host, LinearLayout.LayoutParams(MATCH_PARENT, 0, 1f))

        // 二级导航：样式对齐一级底栏——顶上一条 1dp 分隔线划出明显上界，
        // 下面是一条通栏、无圆角无描边的行，选中「填色 + 纸色字」，未选中只用墨色字。
        // column 现在左右不留白了，这里直接通栏，不用再拿负外边距抵消。
        val subWrap = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        subWrap.addView(
            View(this).apply { setBackgroundColor(Ui.RULE) },
            LinearLayout.LayoutParams(MATCH_PARENT, dpx(1)),
        )
        subNav = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setBackgroundColor(Ui.PAPER)
        }
        subWrap.addView(subNav, LinearLayout.LayoutParams(MATCH_PARENT, dpx(SUB_BAR_HEIGHT_DP)))
        column.addView(
            subWrap,
            LinearLayout.LayoutParams(MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT),
        )

        // 刷新遮罩只盖内容区（host），不盖顶部分类条和二级导航——
        // 这样刷新活动时照样能点「组织 / 个人」切页。
        host.addView(buildBusyOverlay(), FrameLayout.LayoutParams(MATCH_PARENT, MATCH_PARENT))
        root.addView(column, FrameLayout.LayoutParams(MATCH_PARENT, MATCH_PARENT))
        return root
    }

    /**
     * 抓取遮罩：一层薄黑 + 居中一张纸色小卡（转圈 + 一句在抓什么）。
     *
     * 放在整页最上层而不是插进列表里——这就是「刷新时页面不抖」的关键：
     * busy 只改这一层的可见性，下面的列表一个视图都不动。
     */
    private fun buildBusyOverlay(): View {
        busyOverlay = FrameLayout(this).apply {
            setBackgroundColor(Ui.SCRIM)
            visibility = View.GONE
            isClickable = true
        }

        val card = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            background = GradientDrawable().apply {
                cornerRadius = dpx(16).toFloat()
                setColor(Ui.PAPER)
                setStroke(dpx(1), Ui.RULE)
            }
            setPadding(dpx(24), dpx(20), dpx(24), dpx(16))
        }

        val spinner = ProgressBar(this, null, android.R.attr.progressBarStyleSmall).apply {
            indeterminateTintList = ColorStateList.valueOf(Ui.ACCENT)
        }
        card.addView(
            spinner,
            LinearLayout.LayoutParams(dpx(28), dpx(28)).apply { gravity = Gravity.CENTER_HORIZONTAL },
        )

        busyText = TextView(this).apply {
            text = "正在抓取"
            textSize = 11f
            typeface = Ui.mono
            setTextColor(Ui.MUTED)
            gravity = Gravity.CENTER
        }
        card.addView(
            busyText,
            LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
            ).apply {
                topMargin = dpx(10)
                gravity = Gravity.CENTER_HORIZONTAL
            },
        )

        busyOverlay.addView(
            card,
            FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.CENTER,
            ),
        )
        return busyOverlay
    }

    /** busy 只切遮罩，绝不重建内容——所以不会抖。 */
    private fun setBusy(on: Boolean, label: String = "") {
        busy = on
        if (on) busyText.text = label.ifBlank { "正在抓取" }
        busyOverlay.visibility = if (on) View.VISIBLE else View.GONE
    }

    // ---------- 顶部与二级导航 ----------

    private fun renderChrome() {
        topBar.removeAllViews()

        when (tab) {
            TAB_ACTS -> {
                topBar.visibility = View.VISIBLE
                topRule.visibility = View.VISIBLE
                // 左：历史活动；中：报名中（两块占大部分宽度）；最右：线上/线下筛选
                topBar.addView(
                    segButton("历史活动", pastOnly) { setPast(true) },
                    LinearLayout.LayoutParams(0, dpx(38), 1f).apply { rightMargin = dpx(6) },
                )
                topBar.addView(
                    segButton("报名中", !pastOnly) { setPast(false) },
                    LinearLayout.LayoutParams(0, dpx(38), 1f).apply { rightMargin = dpx(8) },
                )
                topBar.addView(
                    segButton(filterLabel(), onlineFilter != FILTER_ALL) { pickFilter() },
                    LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dpx(38)),
                )
            }
            TAB_ORGS -> {
                topBar.visibility = View.VISIBLE
                topRule.visibility = View.VISIBLE
                // 四档分类：全部 / 学院 / 协会 / 社团；右侧留一颗搜索键按名字过滤。
                listOf("" to "全部", "college" to "学院", "assoc" to "协会", "club" to "社团")
                    .forEachIndexed { index, (value, label) ->
                        topBar.addView(
                            segButton(label, orgNature == value) {
                                if (orgNature == value) return@segButton
                                orgNature = value
                                renderChrome()
                                renderContent()
                            },
                            LinearLayout.LayoutParams(0, dpx(38), 1f).apply {
                                rightMargin = dpx(6)
                            },
                        )
                    }
                topBar.addView(
                    searchButton(orgKeyword.isNotBlank()) { askOrgKeyword() },
                    LinearLayout.LayoutParams(dpx(38), dpx(38)),
                )
            }
            else -> {
                // 个人页没有切换条。签到已并进下方「总分 / 完成 / 签到」这一行，
                // 顶部只留一条与原高一致的空白，保证统计行位置不变。
                topBar.visibility = View.VISIBLE
                topRule.visibility = View.VISIBLE
            }
        }

        renderSubNav()
    }

    private fun renderSubNav() {
        subNav.removeAllViews()
        listOf(
            TAB_ACTS to "活动",
            TAB_ORGS to "组织",
            TAB_ME to "个人",
        ).forEachIndexed { index, (target, label) ->
            val selected = tab == target
            // 每个标签自己带一个胶囊框，让层级一眼看得见：
            // 选中是实心绿底白字，未选中是纸色底 + 1dp 描边 + 墨色字。
            // 框只包住文字（WRAP_CONTENT），三格仍然通栏平分，外面那条 1dp 分隔线就是这层的上界。
            val chip = TextView(this).apply {
                text = label
                textSize = 13f
                typeface = if (selected) Ui.medium else Ui.body
                setTextColor(if (selected) Ui.ACCENT_INK else Ui.INK_2)
                gravity = Gravity.CENTER
                setPadding(dpx(18), 0, dpx(18), 0)
                background = GradientDrawable().apply {
                    cornerRadius = dpx(30).toFloat()
                    setColor(if (selected) Ui.ACCENT_DEEP else Ui.PAPER_2)
                    if (!selected) setStroke(dpx(1), Ui.RULE)
                }
                isClickable = true
                setOnClickListener { switchTab(target) }
            }
            val slot = FrameLayout(this).apply {
                isClickable = true
                setOnClickListener { switchTab(target) }
                addView(
                    chip,
                    FrameLayout.LayoutParams(
                        ViewGroup.LayoutParams.WRAP_CONTENT,
                        dpx(SUB_CHIP_HEIGHT_DP),
                        Gravity.CENTER,
                    ),
                )
            }
            subNav.addView(slot, LinearLayout.LayoutParams(0, MATCH_PARENT, 1f))
        }
    }

    /** 顶栏搜索键：经典放大镜图标（自绘，不用 emoji/第三方库）。有搜索词时填深绿，否则浅底细边。 */
    private fun searchButton(active: Boolean, onClick: () -> Unit): View = object : View(this) {
        // 图标用描边画；底色一律用实心填充，描边靠「两层实心圆角矩形」叠出来，避免 stroke 被裁导致粗细不均。
        private val iconPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            style = Paint.Style.STROKE
            strokeWidth = dpx(2).toFloat()
        }
        private val fillPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.FILL }

        override fun onDraw(canvas: Canvas) {
            super.onDraw(canvas)
            val w = width.toFloat()
            val h = height.toFloat()
            val radius = dpx(10).toFloat()
            val edge = dpx(1).toFloat() // 边框厚度 1dp，四边一致
            if (active) {
                fillPaint.color = Ui.ACCENT_DEEP
                canvas.drawRoundRect(0f, 0f, w, h, radius, radius, fillPaint)
            } else {
                // 先铺一层浅描边色，再内缩 1dp 铺纸色，得到均匀不被裁的细边
                fillPaint.color = Ui.RULE
                canvas.drawRoundRect(0f, 0f, w, h, radius, radius, fillPaint)
                fillPaint.color = Ui.PAPER_2
                canvas.drawRoundRect(edge, edge, w - edge, h - edge, radius - edge, radius - edge, fillPaint)
            }
            // 放大镜：圆环 + 斜向右下的手柄
            iconPaint.color = if (active) Ui.ACCENT_INK else Ui.INK_2
            val r = dpx(9).toFloat()
            val cx = w / 2f - dpx(2)
            val cy = h / 2f - dpx(2)
            canvas.drawCircle(cx, cy, r, iconPaint)
            val hx = cx + r * 0.71f
            val hy = cy + r * 0.71f
            canvas.drawLine(hx, hy, hx + dpx(5), hy + dpx(5), iconPaint)
        }

        init {
            isClickable = true
            contentDescription = "搜索组织"
            setOnClickListener { onClick() }
        }
    }

    private fun segButton(label: String, selected: Boolean, onClick: () -> Unit): TextView =
        TextView(this).apply {
            text = label
            textSize = 12.5f
            typeface = if (selected) Ui.medium else Ui.body
            setTextColor(if (selected) Ui.ACCENT_INK else Ui.INK_2)
            gravity = Gravity.CENTER
            maxLines = 1
            ellipsize = TextUtils.TruncateAt.END
            setPadding(dpx(8), 0, dpx(8), 0)
            background = GradientDrawable().apply {
                cornerRadius = dpx(10).toFloat()
                setColor(if (selected) Ui.ACCENT_DEEP else Ui.PAPER_2)
                if (!selected) setStroke(dpx(1), Ui.RULE)
            }
            isClickable = true
            setOnClickListener { onClick() }
        }

    /**
     * 顶部那一格筛选按钮的文字。线下带校区时把校区一起写进去，
     * 这样外面不用再排一格「校区 ▾」，筛选条件也不会藏在弹窗里看不见。
     */
    private fun filterLabel(): String = when (onlineFilter) {
        FILTER_ONLINE -> "线上 ▾"
        // 线下档把校区一并写出来：选了「全部校区」就写「全部」，
        // 否则光一个「线下」看不出还有没有夹着校区条件
        FILTER_OFFLINE -> when (campusFilter) {
            CAMPUS_GUANGZHOU -> "线下 广州 ▾"
            CAMPUS_FOSHAN -> "线下 佛山 ▾"
            else -> "线下 全部 ▾"
        }
        else -> "全部 ▾"
    }

    /** 空态文案里用的短称，比 filterLabel() 少了箭头和校区后缀，读起来通顺。 */
    private fun filterHint(): String = when (onlineFilter) {
        FILTER_ONLINE -> "线上"
        FILTER_OFFLINE -> "线下"
        else -> "全部"
    }

    private fun campusName(): String = when (campusFilter) {
        CAMPUS_GUANGZHOU -> "广州校区"
        CAMPUS_FOSHAN -> "佛山校区"
        else -> "全部校区"
    }

    /**
     * 第一层选线上/线下；点到「线下」时，弹窗里当场缩进一行长出校区选项，
     * 不再另开一个弹窗，也不用为了校区在顶部多排一格。
     */
    private fun pickFilter() {
        val dialog = Dialog(this)
        dialog.requestWindowFeature(android.view.Window.FEATURE_NO_TITLE)
        val column = dialogCard("只看")

        // 每次点选都重建这张卡里的选项区，缩进层级才能跟着「线下」出现/收起
        fun rebuild() {
            while (column.childCount > 1) column.removeViewAt(1)
            listOf(
                FILTER_ALL to "全部",
                FILTER_ONLINE to "线上",
                FILTER_OFFLINE to "线下",
            ).forEach { (value, label) ->
                val checked = onlineFilter == value
                column.addView(optionRow(label, checked, indent = false) {
                    // 只有「第一次点线下」需要把卡片留着展开校区；其余情况（全部 / 线上 /
                    // 已经在线下再点一次确认）都算选完了，直接退出去，不必再点关闭。
                    val willExpand = value == FILTER_OFFLINE && !checked
                    applyFilter(value, if (value == FILTER_OFFLINE) campusFilter else CAMPUS_ALL)
                    if (willExpand) rebuild() else dialog.dismiss()
                }, wide().apply { topMargin = dpx(2) })

                // 线下被选中时，紧贴它下面缩进列出三个校区，视觉上像子项
                if (value == FILTER_OFFLINE && checked) {
                    listOf(
                        CAMPUS_ALL to "全部校区",
                        CAMPUS_GUANGZHOU to "广州校区",
                        CAMPUS_FOSHAN to "佛山校区",
                    ).forEach { (campus, campusText) ->
                        column.addView(
                            optionRow(campusText, campusFilter == campus, indent = true) {
                                // 校区是最后一级：选完这一行就完事了，卡片自己退出去
                                applyFilter(FILTER_OFFLINE, campus)
                                dialog.dismiss()
                            },
                            wide().apply { topMargin = dpx(2) },
                        )
                    }
                }
            }
        }
        rebuild()
        column.addView(button("关闭", primary = true) { dialog.dismiss() }, wide().apply {
            topMargin = dpx(16)
        })
        showCard(dialog, column)
    }

    /**
     * 筛选弹窗里的一行：整行可点，选中的行右侧打勾。
     *
     * 一级（全部/线上/线下）选中时给整行一个浅底 + 描边，表明它是当前的档；
     * 二级（校区）不再套框，只用缩进 + 绿字 + 勾，一眼看得出是从属关系，不会和父项抢层级。
     */
    private fun optionRow(label: String, checked: Boolean, indent: Boolean, onClick: () -> Unit): View =
        LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(
                if (indent) dpx(34) else dpx(14),
                dpx(if (indent) 9 else 12),
                dpx(14),
                dpx(if (indent) 9 else 12),
            )
            if (!indent) {
                background = GradientDrawable().apply {
                    cornerRadius = dpx(10).toFloat()
                    setColor(if (checked) Ui.PAPER_3 else Color.TRANSPARENT)
                    if (checked) setStroke(dpx(1), Ui.ACCENT)
                }
            }
            isClickable = true
            setOnClickListener { onClick() }

            addView(TextView(this@SecondClassActivity).apply {
                text = label
                textSize = if (indent) 13.5f else 15f
                typeface = if (checked) Ui.medium else Ui.body
                setTextColor(
                    when {
                        indent && checked -> Ui.ACCENT_DEEP
                        indent -> Ui.MUTED
                        checked -> Ui.ACCENT_DEEP
                        else -> Ui.INK_2
                    }
                )
            }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            addView(TextView(this@SecondClassActivity).apply {
                text = if (checked) "✓" else ""
                textSize = 14f
                typeface = Ui.medium
                setTextColor(Ui.ACCENT)
            })
        }

    private fun applyFilter(online: Int, campus: Int) {
        onlineFilter = online
        // 不在线下档就不留校区条件，免得切回线下时还夹着上次的校区
        campusFilter = if (online == FILTER_OFFLINE) campus else CAMPUS_ALL
        renderChrome()
        renderContent()
    }

    /**
     * 活动挂在哪个校区：官方接口没有校区字段，只有活动名里写着（广州）/（佛山）/(佛山) 这类标记，
     * 没写的（比如「（全校）」）算两个校区都适用。
     */
    private fun campusOf(act: SecondClassApi.Act): Int {
        val name = act.name
        val gz = name.contains("（广州）") || name.contains("(广州)")
        val fs = name.contains("（佛山）") || name.contains("(佛山)")
        return when {
            gz && fs -> CAMPUS_BOTH
            gz -> CAMPUS_GUANGZHOU
            fs -> CAMPUS_FOSHAN
            else -> CAMPUS_BOTH
        }
    }

    private fun setPast(past: Boolean) {
        if (pastOnly == past) return
        pastOnly = past
        renderChrome()
        renderContent()
        if (currentActs() == null && SecondClassStore.loadLogin(db) != null) loadActs(silent = false)
    }

    private fun switchTab(target: Int) {
        if (tab == target) return
        // 刷新只属于发起它的那一页。切走时收掉上一页的遮罩与 busy 标志，
        // 免得它盖住新页面、也免得拦住新页面的刷新；后台任务回来仍会照常存缓存。
        if (busy) setBusy(false)
        tab = target
        renderChrome()
        renderContent()
        when (target) {
            TAB_ORGS -> if (orgs == null && SecondClassStore.loadLogin(db) != null) loadOrgs()
            TAB_ME -> if (credit == null && SecondClassStore.loadLogin(db) != null) loadCredit()
        }
    }

    // ---------- 内容 ----------

    private fun renderContent() {
        body.removeAllViews()
        when (tab) {
            TAB_ACTS -> renderActs()
            TAB_ORGS -> renderOrgs()
            else -> renderMe()
        }
    }

    private fun currentActs(): List<SecondClassApi.Act>? = if (pastOnly) actsPast else actsCurrent

    private fun visibleActs(source: List<SecondClassApi.Act>): List<SecondClassApi.Act> =
        source.filter {
            val onlineOk = when (onlineFilter) {
                FILTER_ONLINE -> it.online
                FILTER_OFFLINE -> !it.online
                else -> true
            }
            if (!onlineOk) return@filter false
            // 校区筛选只在线下档有意义；线上活动不区分校区，全校活动（CAMPUS_BOTH）两个校区都算
            if (onlineFilter != FILTER_OFFLINE || campusFilter == CAMPUS_ALL) return@filter true
            val campus = campusOf(it)
            campus == CAMPUS_BOTH || campus == campusFilter
        }

    private fun renderActs() {
        if (SecondClassStore.loadLogin(db) == null) {
            body.addView(
                stateBlock(
                    title = "还没登录第二课堂",
                    hint = "第二课堂系统在校内网里，校外要先连学校 VPN。学号密码记住一次就行。",
                    primaryLabel = "登录第二课堂",
                    primary = { askLogin() },
                    secondaryLabel = "用浏览器打开",
                    secondary = { openBrowser() },
                ),
            )
            return
        }

        actsError?.let { message ->
            body.addView(
                stateBlock(
                    title = "活动没拉到",
                    hint = message,
                    primaryLabel = "重试",
                    primary = { loadActs(silent = false) },
                    secondaryLabel = "用浏览器打开",
                    secondary = { openBrowser() },
                ),
            )
            return
        }

        val source = currentActs()
        if (source == null) {
            body.addView(stateBlock("还没有活动数据", "下拉刷新，或者点下面重试一次。", "重试") { loadActs(silent = false) })
            return
        }

        val list = visibleActs(source)
        if (list.isEmpty()) {
            if (source.isEmpty()) {
                val hint = if (pastOnly) "历史里还没有活动，切到「报名中」看看。" else "现在没有报名中的活动，切到「历史活动」看看。"
                body.addView(
                    stateBlock(
                        title = "这里空着",
                        hint = hint,
                        primaryLabel = if (pastOnly) "看报名中" else "看历史活动",
                        primary = { setPast(!pastOnly) },
                    ),
                )
                return
            }
            // 有活动、只是被筛选掉：优先只退校区这一层，别把线上/线下也一起清掉
            if (onlineFilter == FILTER_OFFLINE && campusFilter != CAMPUS_ALL) {
                body.addView(
                    stateBlock(
                        title = "这里空着",
                        hint = "线下的${campusName()}没有活动。",
                        primaryLabel = "看两个校区",
                        primary = { applyFilter(FILTER_OFFLINE, CAMPUS_ALL) },
                        secondaryLabel = "清掉筛选",
                        secondary = { applyFilter(FILTER_ALL, CAMPUS_ALL) },
                    ),
                )
                return
            }
            body.addView(
                stateBlock(
                    title = "这里空着",
                    hint = "这一档没有${filterHint()}的活动。",
                    primaryLabel = "看全部",
                    primary = { applyFilter(FILTER_ALL, CAMPUS_ALL) },
                ),
            )
            return
        }

        body.addView(metaRow("共 ${list.size} 条", syncLabel(pastOnly), top = dpx(10)))
        list.forEach { body.addView(actCard(it), topGap(10)) }
    }

    private fun renderOrgs() {
        if (SecondClassStore.loadLogin(db) == null) {
            body.addView(
                stateBlock(
                    title = "还没登录第二课堂",
                    hint = "组织清单也走同一套登录，登录一次就能看。",
                    primaryLabel = "登录第二课堂",
                    primary = { askLogin() },
                    secondaryLabel = "用浏览器打开",
                    secondary = { openBrowser() },
                ),
            )
            return
        }

        orgsError?.let { message ->
            body.addView(
                stateBlock(
                    title = "组织没拉到",
                    hint = message,
                    primaryLabel = "重试",
                    primary = { loadOrgs() },
                    secondaryLabel = "用浏览器打开",
                    secondary = { openBrowser() },
                ),
            )
            return
        }

        val source = orgs
        if (source == null) {
            body.addView(stateBlock("还没有组织数据", "点下面拉一次。", "拉取组织") { loadOrgs() })
            return
        }

        // 分类按真实数据映射：学院=nature3；协会=nature2 且名字含"协会"；社团=其余。
        var list = when (orgNature) {
            "college" -> source.filter { it.nature == 3 }
            "assoc" -> source.filter { it.nature == 2 && it.name.contains("协会") }
            "club" -> source.filter { it.nature != 3 && !(it.nature == 2 && it.name.contains("协会")) }
            else -> source
        }
        if (orgKeyword.isNotBlank()) {
            val kw = orgKeyword.trim()
            list = list.filter { it.name.contains(kw, ignoreCase = true) }
        }
        if (list.isEmpty()) {
            body.addView(stateBlock("这一档没有组织", "换个分类或清掉搜索词看看。", "看全部") {
                orgNature = ""
                orgKeyword = ""
                renderChrome()
                renderContent()
            })
            return
        }

        body.addView(metaRow("共 ${list.size} 个组织", "", top = dpx(10)))
        list.forEach { body.addView(orgCard(it), topGap(10)) }
    }

    /** 右上角搜索：按组织名字过滤（客户端过滤，不再多打一次接口）。 */
    private fun askOrgKeyword() {
        val column = dialogCard("搜索组织")
        column.addView(caption("输入组织名字里的关键词，留空就是清掉搜索。", top = dpx(2)), wide())
        val field = input("比如：学院、协会、学生会", InputType.TYPE_CLASS_TEXT).apply {
            setText(orgKeyword)
            setPadding(dpx(12), dpx(10), dpx(12), dpx(10))
            setBackgroundColor(Color.TRANSPARENT)
        }
        column.addView(field, wide().apply { topMargin = dpx(12) })
        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        actions.addView(button("取消") { }, gapParams(weight = 1f))
        if (orgKeyword.isNotBlank()) {
            actions.addView(button("清除") { }, gapParams(weight = 1f, start = 6))
        }
        actions.addView(button("搜索", primary = true) { }, gapParams(weight = 1f, start = 6))
        column.addView(actions, wide().apply { topMargin = dpx(14) })
        val dialog = showCard(column)
        (actions.getChildAt(0) as TextView).setOnClickListener { dialog.dismiss() }
        var idx = 1
        if (orgKeyword.isNotBlank()) {
            (actions.getChildAt(idx++) as TextView).setOnClickListener {
                orgKeyword = ""
                dialog.dismiss()
                renderChrome()
                renderContent()
            }
        }
        (actions.getChildAt(idx) as TextView).setOnClickListener {
            orgKeyword = field.text?.toString()?.trim().orEmpty()
            dialog.dismiss()
            renderChrome()
            renderContent()
        }
    }

    /**
     * 学分颜色三档：够了绿、空的或不到一半红、其余黄。
     * need<=0 视为没有要求，按「空」处理（红），免得拿 0 当分母算出满格。
     */
    private fun creditColor(got: Double, need: Double): Int = when {
        need <= 0 || got <= 0 || got < need / 2.0 -> Color.parseColor("#C0392B")
        got >= need -> Ui.ACCENT_DEEP
        else -> Ui.WARN
    }

    private fun renderMe() {
        val report = credit
        if (SecondClassStore.loadLogin(db) == null) {
            body.addView(
                stateBlock(
                    title = "还没登录第二课堂",
                    hint = "登录一次，学分进度、申报、学籍、签到记录都能在这看。",
                    primaryLabel = "登录第二课堂",
                    primary = { askLogin() },
                    secondaryLabel = "用浏览器打开",
                    secondary = { openBrowser() },
                ),
            )
        } else if (report == null) {
            body.addView(stateBlock("还没有学分数据", "点下面拉一次，登录信息已经记住了。", "拉取学分") { loadCredit() })
        } else {
            // 统计行三格对称：总分 / 完成 / 签到（签到原本在顶部工具条，现并到这一行右侧）。
            val stats = statRow(
                "总分" to format(report.total, 1),
                "完成" to "${report.percent.toInt()}%",
            )
            stats.addView(signStatCell(), gapParams(weight = 1f))
            body.addView(stats)
            report.items.forEach { item ->
                body.addView(
                    line(
                        name = item.name,
                        right = "${format(item.credit, 1)}  /  ${format(item.need, 1)}",
                        color = creditColor(item.credit, item.need),
                        big = true,
                        flushRight = true,
                    ),
                )
            }
            if (report.fetchedAt > 0) {
                body.addView(caption("上次同步：${stamp(report.fetchedAt)}", top = dpx(14)))
            }
        }

        // 五个子页：都是弹层，不跳页
        body.addView(sectionTitle("我的"))
        body.addView(
            entryRow("我的组织", "我加入的组织与班级") { openMyOrgs() },
            topGap(8),
        )
        body.addView(
            entryRow("我的报名", "我报过的活动与审核结果") { openJoins() },
            topGap(8),
        )
        body.addView(
            entryRow("我的申报", "报过的项目与审核结果") { openApplies() },
            topGap(8),
        )
        body.addView(
            entryRow("学籍信息", "校区、学院、专业、班级与学分") { openProfile() },
            topGap(8),
        )
        body.addView(
            entryRow("签到签退记录", "每场活动的进出时间与有效时长") { openSigns() },
            topGap(8),
        )
    }

    private fun syncLabel(past: Boolean): String {
        val at = SecondClassStore.actsAt(db, past = past)
        return if (at > 0) "上次同步 ${stamp(at)}" else ""
    }

    // ---------- 卡片 ----------

    private fun actCard(act: SecondClassApi.Act): View {
        val card = cardBox { openDetail(act) }

        val head = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.TOP
        }
        head.addView(TextView(this).apply {
            text = act.name
            textSize = 16f
            typeface = Ui.medium
            setTextColor(Ui.INK)
            maxLines = 2
            ellipsize = TextUtils.TruncateAt.END
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        head.addView(tag(if (act.online) "线上" else "线下", act.online), gapParams(start = 8))
        card.addView(head)

        // 学分类型（文体艺术 / 技能培训 / 志愿公益 …）直接标在卡片上，
        // 不用点进详情就能看出这场活动算哪一类第二课堂学分。
        val labels = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, dpx(6), 0, 0)
        }
        if (act.category.isNotBlank()) {
            labels.addView(tag(act.category, accent = true), gapParams(end = 8))
        }
        listOf(act.org, act.typeName).filter { it.isNotBlank() }.forEachIndexed { index, text ->
            if (index > 0) {
                labels.addView(View(this), LinearLayout.LayoutParams(dpx(8), 1))
            }
            labels.addView(TextView(this).apply {
                this.text = text
                textSize = 12f
                typeface = Ui.body
                setTextColor(Ui.MUTED)
                maxLines = 1
                ellipsize = TextUtils.TruncateAt.END
            })
        }
        if (labels.childCount > 0) card.addView(labels)

        val line = buildString {
            val whenText = SecondClassApi.prettySpan(act.startTime, act.endTime)
            if (whenText.isNotEmpty()) append(whenText)
            if (act.hours > 0) {
                if (isNotEmpty()) append("　　")
                append("${format(act.hours, 1)} 分")
            }
        }
        if (line.isNotEmpty()) {
            card.addView(TextView(this).apply {
                text = line
                textSize = 12f
                typeface = Ui.mono
                setTextColor(Ui.INK_2)
                setPadding(0, dpx(6), 0, 0)
            })
        }
        return card
    }

    private fun orgCard(org: SecondClassApi.Org): View {
        val card = cardBox { openOrg(org) }
        val head = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.TOP
        }
        head.addView(TextView(this).apply {
            text = org.name
            textSize = 16f
            typeface = Ui.medium
            setTextColor(Ui.INK)
            maxLines = 2
            ellipsize = TextUtils.TruncateAt.END
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        head.addView(tag(org.rank, false), gapParams(start = 8))
        card.addView(head)

        val sub = buildList {
            if (org.kind.isNotBlank()) add(org.kind)
            if (org.memberCount > 0) add("${org.memberCount} 人")
            if (org.needAudit) add("需审核")
        }
        if (sub.isNotEmpty()) {
            card.addView(TextView(this).apply {
                text = sub.joinToString("　　")
                textSize = 12f
                typeface = Ui.body
                setTextColor(Ui.MUTED)
                setPadding(0, dpx(6), 0, 0)
            })
        }
        return card
    }

    private fun cardBox(onClick: () -> Unit): LinearLayout =
        LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dpx(14), dpx(12), dpx(14), dpx(12))
            background = GradientDrawable().apply {
                cornerRadius = dpx(12).toFloat()
                setColor(Ui.PAPER_2)
                setStroke(dpx(1), Ui.RULE)
            }
            isClickable = true
            setOnClickListener { onClick() }
        }

    private fun entryRow(title: String, hint: String, onClick: () -> Unit): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dpx(14), dpx(12), dpx(14), dpx(12))
            background = GradientDrawable().apply {
                cornerRadius = dpx(12).toFloat()
                setColor(Ui.PAPER_2)
                setStroke(dpx(1), Ui.RULE)
            }
            isClickable = true
            setOnClickListener { onClick() }
        }
        val texts = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        texts.addView(TextView(this).apply {
            text = title
            textSize = 15f
            typeface = Ui.medium
            setTextColor(Ui.INK)
        })
        texts.addView(TextView(this).apply {
            text = hint
            textSize = 11.5f
            typeface = Ui.body
            setTextColor(Ui.MUTED)
            setPadding(0, dpx(3), 0, 0)
        })
        row.addView(texts, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        row.addView(TextView(this).apply {
            text = "›"
            textSize = 20f
            typeface = Ui.body
            setTextColor(Ui.MUTED)
        }, gapParams(start = 8))
        return row
    }

    private fun tag(text: String, accent: Boolean): TextView = TextView(this).apply {
        this.text = text
        textSize = 11f
        typeface = Ui.mono
        setTextColor(if (accent) Ui.ACCENT_DEEP else Ui.MUTED)
        setPadding(dpx(7), dpx(2), dpx(7), dpx(2))
        background = GradientDrawable().apply {
            cornerRadius = dpx(6).toFloat()
            setColor(if (accent) Ui.PAPER_3 else Ui.PAPER)
            setStroke(dpx(1), if (accent) Ui.ACCENT else Ui.RULE)
        }
    }

    private fun sectionTitle(text: String): View = TextView(this).apply {
        this.text = text
        textSize = 13f
        typeface = Ui.medium
        setTextColor(Ui.INK_2)
        setPadding(0, dpx(22), 0, dpx(8))
    }

    private fun metaRow(left: String, right: String, top: Int = 0): View =
        LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, top, 0, dpx(2))
            addView(caption(left), LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            if (right.isNotBlank()) addView(caption(right), gapParams(start = 8))
        }

    /**
     * 四态里除「满」之外的那三块：空 / 错 / 还没拉，都给一句说明加一个补它的动作。
     *
     * 注意参数顺序：主行动放在最后，这样尾随 lambda 才会落在 `primary` 上
     * （之前在 `secondary` 后面，尾随 lambda 全被当成「次按钮」，空态里一个按钮都不出来）。
     */
    private fun stateBlock(
        title: String,
        hint: String,
        primaryLabel: String? = null,
        secondaryLabel: String? = null,
        secondary: (() -> Unit)? = null,
        primary: (() -> Unit)? = null,
    ): View {
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        box.setPadding(0, dpx(26), 0, 0)
        box.addView(TextView(this).apply {
            text = title
            textSize = 18f
            typeface = Ui.medium
            setTextColor(Ui.INK)
        })
        box.addView(TextView(this).apply {
            text = hint
            textSize = 13f
            typeface = Ui.body
            setTextColor(Ui.MUTED)
            setPadding(0, dpx(8), 0, 0)
        })
        if (primaryLabel != null && primary != null) {
            val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
            row.addView(button(primaryLabel, primary = true) { primary() }, gapParams(weight = 1f, end = 8))
            if (secondaryLabel != null && secondary != null) {
                row.addView(button(secondaryLabel) { secondary() }, gapParams(weight = 1f))
            }
            box.addView(row, wide().apply { topMargin = dpx(16) })
            // 只有「还没登录」那类空态才需要提示初始密码
            if (primaryLabel.startsWith("登录")) {
                box.addView(caption("账号是学号，初始密码为学号@abc。", top = dpx(12)))
            }
        }
        return box
    }

    // ---------- 活动详情 / 报名 ----------

    private fun openDetail(act: SecondClassApi.Act) {
        fetchThen(
            label = "正在取活动详情",
            failTitle = "取活动详情失败",
            work = { session, token -> SecondClassApi.detail(session, token, act.id) },
        ) { detail -> showDetail(detail) }
    }

    private fun showDetail(detail: SecondClassApi.Detail) {
        val column = dialogCard(detail.name)
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }

        fun row(label: String, value: String) {
            if (value.isBlank()) return
            val line = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                setPadding(0, dpx(5), 0, dpx(5))
            }
            line.addView(TextView(this).apply {
                text = label
                textSize = 12f
                typeface = Ui.mono
                setTextColor(Ui.MUTED)
                width = dpx(64)
            })
            line.addView(TextView(this).apply {
                text = value
                textSize = 13.5f
                typeface = Ui.body
                setTextColor(Ui.INK_2)
            }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            box.addView(line)
        }

        row("时间", SecondClassApi.prettySpan(detail.startTime, detail.endTime))
        row("地点", detail.place)
        row("报名截止", SecondClassApi.prettyTime(detail.enrollEndTime))
        row("分", if (detail.hours > 0) "${format(detail.hours, 1)} 分" else "")
        // 名额：正常显示「已报 312/800」；遇到 peopleCount 已经超上限的脏数据
        // （官方接口里确实有 311/1 这种）就说清是已报人数与上限，别让人以为分母写错了
        val quota = when {
            detail.peopleLimit <= 0 -> "不限"
            detail.peopleCount > detail.peopleLimit -> "已报 ${detail.peopleCount} 人（上限 ${detail.peopleLimit}）"
            else -> "${detail.peopleCount}/${detail.peopleLimit}"
        }
        row("名额", quota)
        row("主办", detail.zhubanName)
        row("承办", detail.xiebanName)
        row("指导", detail.teacherName)
        row("指导分", if (detail.classHours > 0) "${format(detail.classHours, 1)} 分" else "")
        row("联系人", listOf(detail.adminName, detail.adminContact).filter { it.isNotBlank() }.joinToString(" "))
        if (detail.introduce.isNotBlank()) {
            box.addView(TextView(this).apply {
                text = detail.introduce
                textSize = 13f
                typeface = Ui.body
                setTextColor(Ui.INK_2)
                setPadding(0, dpx(10), 0, 0)
            })
        }

        column.addView(
            ScrollView(this).apply { addView(box) },
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dpx(320)),
        )

        if (!detail.enroll.canJoin) {
            column.addView(caption(detail.enroll.reason, Ui.WARN, dpx(10)))
        }

        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        actions.addView(button("关闭") { }, gapParams(weight = 1f, end = 8))
        actions.addView(
            button(if (detail.enroll.canJoin) "报名" else "不可报名", primary = true) { },
            gapParams(weight = 1f),
        )
        column.addView(actions, wide().apply { topMargin = dpx(16) })

        val dialog = showCard(column)
        (actions.getChildAt(0) as TextView).setOnClickListener { dialog.dismiss() }
        val joinButton = actions.getChildAt(1) as TextView
        if (detail.enroll.canJoin) {
            joinButton.setOnClickListener {
                dialog.dismiss()
                confirmJoin(detail)
            }
        } else {
            joinButton.isEnabled = false
            joinButton.alpha = 0.5f
        }
    }

    /**
     * 报名二次确认：写操作不能手滑。官方前端原话「报名后无法取消」，
     * 所以这里把活动名、时间、地点三条摊开，让人看清再点。
     */
    private fun confirmJoin(detail: SecondClassApi.Detail) {
        val message = buildString {
            append(detail.name).append("\n\n")
            val time = SecondClassApi.prettySpan(detail.startTime, detail.endTime)
            if (time.isNotBlank()) append("时间：").append(time).append("\n")
            if (detail.place.isNotBlank()) append("地点：").append(detail.place).append("\n")
            if (detail.hours > 0) append("分：").append(format(detail.hours, 1)).append("\n")
            append("\n确定要报名吗？报名后无法取消。")
        }
        AlertDialog.Builder(this)
            .setTitle("确认报名")
            .setMessage(message)
            .setNegativeButton("再看看", null)
            .setPositiveButton("确认报名") { _, _ -> doJoin(detail) }
            .show()
    }

    private fun doJoin(detail: SecondClassApi.Detail) {
        fetchThen(
            label = "正在报名",
            failTitle = "报名失败",
            work = { session, token ->
                val message = SecondClassApi.join(session, token, detail.id)
                // 写操作后强制实时刷新，不吃缓存：名额与报名状态都变了
                val latest = runCatching {
                    SecondClassApi.list(
                        session,
                        token,
                        statusId = if (pastOnly) "5" else "1",
                        size = PAGE_SIZE,
                    )
                }.getOrNull()
                if (latest != null) {
                    SecondClassStore.saveActs(db, latest, past = pastOnly)
                    if (pastOnly) actsPast = latest else actsCurrent = latest
                }
                message
            },
        ) { message ->
            renderContent()
            toast(message)
        }
    }

    // ---------- 组织详情 / 加入 ----------

    private fun openOrg(org: SecondClassApi.Org) {
        fetchThen(
            label = "正在取组织信息",
            failTitle = "取组织信息失败",
            work = { session, token ->
                val detail = SecondClassApi.orgDetail(session, token, org.id, org.type)
                val activities = runCatching {
                    SecondClassApi.orgActivities(session, token, org.id, page = 1, size = 20)
                }.getOrDefault(emptyList())
                detail to activities
            },
        ) { (detail, activities) -> showOrg(detail, activities) }
    }

    private fun showOrg(detail: SecondClassApi.OrgDetail, activities: List<SecondClassApi.Act>) {
        val column = dialogCard(detail.name)
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }

        fun row(label: String, value: String) {
            if (value.isBlank()) return
            val line = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                setPadding(0, dpx(5), 0, dpx(5))
            }
            line.addView(TextView(this).apply {
                text = label
                textSize = 12f
                typeface = Ui.mono
                setTextColor(Ui.MUTED)
                width = dpx(64)
            })
            line.addView(TextView(this).apply {
                text = value
                textSize = 13.5f
                typeface = Ui.body
                setTextColor(Ui.INK_2)
            }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            box.addView(line)
        }

        row(
            "类型",
            when (detail.nature) {
                1 -> "学生会"
                2 -> "社团协会"
                3 -> "其他"
                else -> ""
            },
        )
        row("人数", if (detail.peopleCount > 0) "${detail.peopleCount} 人" else "")
        row("加入方式", if (detail.rule == 1) "提交申请，等审核" else "点一下就加入")
        if (detail.introduce.isNotBlank()) {
            box.addView(TextView(this).apply {
                text = detail.introduce
                textSize = 13f
                typeface = Ui.body
                setTextColor(Ui.INK_2)
                setPadding(0, dpx(10), 0, 0)
            })
        }
        if (activities.isNotEmpty()) {
            box.addView(TextView(this).apply {
                text = "办过的活动 ${activities.size} 场"
                textSize = 13f
                typeface = Ui.medium
                setTextColor(Ui.INK_2)
                setPadding(0, dpx(14), 0, dpx(4))
            })
            activities.take(20).forEach { act ->
                box.addView(TextView(this).apply {
                    text = act.name
                    textSize = 13f
                    typeface = Ui.body
                    setTextColor(Ui.INK)
                    setPadding(0, dpx(6), 0, 0)
                })
                box.addView(TextView(this).apply {
                    text = SecondClassApi.prettySpan(act.startTime, act.endTime)
                    textSize = 11f
                    typeface = Ui.mono
                    setTextColor(Ui.MUTED)
                })
            }
        }

        column.addView(
            ScrollView(this).apply { addView(box) },
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dpx(320)),
        )

        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        actions.addView(button("关闭") { }, gapParams(weight = 1f, end = 8))
        actions.addView(button(if (detail.rule == 1) "申请加入" else "加入", primary = true) { }, gapParams(weight = 1f))
        column.addView(actions, wide().apply { topMargin = dpx(16) })

        val dialog = showCard(column)
        (actions.getChildAt(0) as TextView).setOnClickListener { dialog.dismiss() }
        (actions.getChildAt(1) as TextView).setOnClickListener {
            dialog.dismiss()
            confirmJoinOrg(detail)
        }
    }

    private fun confirmJoinOrg(detail: SecondClassApi.OrgDetail) {
        val message = buildString {
            append(detail.name).append("\n\n")
            append(if (detail.rule == 1) "加入需要审核，提交后等结果。" else "点确认就直接加入。")
            if (detail.nature == 2) append("\n\n据文件要求，每位同学最多可加入 2 个社团。")
        }
        AlertDialog.Builder(this)
            .setTitle(if (detail.rule == 1) "提交加入申请" else "确认加入")
            .setMessage(message)
            .setNegativeButton("再看看", null)
            .setPositiveButton(if (detail.rule == 1) "提交申请" else "加入") { _, _ -> doJoinOrg(detail) }
            .show()
    }

    private fun doJoinOrg(detail: SecondClassApi.OrgDetail) {
        fetchThen(
            label = "正在提交",
            failTitle = "加入组织失败",
            work = { session, token ->
                val profile = SecondClassApi.myProfile(session, token)
                SecondClassApi.joinOrg(session, token, detail.id, profile.userId, profile.identity)
            },
        ) { message ->
            toast(message)
            // 组织人数会变，拉一次新的清单
            loadOrgs(silent = true)
        }
    }

    // ---------- 个人页三个子页 ----------

    // ---------- 我的报名 ----------

    private fun openJoins() {
        fetchThen(
            label = "正在取我的报名",
            failTitle = "取我的报名失败",
            work = { session, token -> SecondClassApi.myJoins(session, token) },
        ) { list -> showJoins(list) }
    }

    /**
     * 我的报名：我报过的活动。
     *
     * 这一屏与「活动」面看的不是一回事——那边是「还能不能报名」，这边是「我报了、审核过了没有」。
     * 所以状态取 audit_status（0 驳回 / 1 通过 / 2 待审核），不拿 apply_status 混着用。
     */
    private fun showJoins(list: List<SecondClassApi.MyJoin>) {
        val column = dialogCard("我的报名")
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }

        if (list.isEmpty()) {
            box.addView(
                stateBlock(
                    title = "还没有报名记录",
                    hint = "在「活动」里挑一个报名，报名成功后这里会多一条。",
                    primaryLabel = "去看活动",
                    primary = {
                        if (tab != TAB_ACTS) switchTab(TAB_ACTS)
                    },
                ),
            )
        } else {
            list.forEach { join ->
                val card = LinearLayout(this).apply {
                    orientation = LinearLayout.VERTICAL
                    setPadding(dpx(12), dpx(10), dpx(12), dpx(10))
                    background = GradientDrawable().apply {
                        cornerRadius = dpx(10).toFloat()
                        setColor(Ui.PAPER_2)
                        setStroke(dpx(1), Ui.RULE)
                    }
                }
                card.addView(TextView(this).apply {
                    text = join.name
                    textSize = 14f
                    typeface = Ui.medium
                    setTextColor(Ui.INK)
                    maxLines = 2
                    ellipsize = TextUtils.TruncateAt.END
                })
                val meta = buildList {
                    if (join.category.isNotBlank()) add(join.category)
                    if (join.org.isNotBlank()) add(join.org)
                    if (join.hours > 0) add("${format(join.hours, 1)} 分")
                }
                if (meta.isNotEmpty()) {
                    card.addView(TextView(this).apply {
                        text = meta.joinToString("　")
                        textSize = 12f
                        typeface = Ui.body
                        setTextColor(Ui.MUTED)
                        setPadding(0, dpx(5), 0, 0)
                    })
                }
                val foot = LinearLayout(this).apply {
                    orientation = LinearLayout.HORIZONTAL
                    gravity = Gravity.CENTER_VERTICAL
                    setPadding(0, dpx(6), 0, 0)
                }
                foot.addView(TextView(this).apply {
                    text = SecondClassApi.prettySpan(join.startTime, join.endTime)
                    textSize = 11f
                    typeface = Ui.mono
                    setTextColor(Ui.MUTED)
                }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
                foot.addView(tag(if (join.online) "线上" else "线下", accent = join.online), gapParams(start = 6))
                val audit = join.auditLine
                if (audit.isNotBlank()) {
                    foot.addView(TextView(this).apply {
                        text = audit
                        textSize = 11f
                        typeface = Ui.mono
                        setTextColor(
                            when (join.auditStatus) {
                                1 -> Ui.ACCENT_DEEP
                                0 -> Ui.WARN
                                else -> Ui.MUTED
                            },
                        )
                    }, gapParams(start = 8))
                }
                card.addView(foot)
                box.addView(card, topGap(8))
            }
        }

        column.addView(
            ScrollView(this).apply { addView(box) },
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dpx(380)),
        )
        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        actions.addView(button("关闭", primary = true) { }, gapParams(weight = 1f))
        column.addView(actions, wide().apply { topMargin = dpx(14) })
        val dialog = showCard(column)
        (actions.getChildAt(0) as TextView).setOnClickListener { dialog.dismiss() }
    }

    // ---------- 签到签退 ----------

    /**
     * 右上角「签到」：跟活动页筛选用的是同一套弹窗（同一张卡、同一种行），
     * 先问「用什么方式」，再把那一种的做法展开。
     */
    private fun pickSign() {
        val dialog = Dialog(this)
        val column = dialogCard("签到签退")
        column.addView(
            caption("现场签到有两种方式，选一个。", top = dpx(2)),
            wide(),
        )
        column.addView(
            optionRow("口令签到签退", checked = false, indent = false) {
                dialog.dismiss()
                askSignCode()
            },
            wide().apply { topMargin = dpx(10) },
        )
        column.addView(
            optionRow("扫码签到签退", checked = false, indent = false) {
                dialog.dismiss()
                scanSign()
            },
            wide().apply { topMargin = dpx(6) },
        )
        column.addView(button("关闭", primary = true) { dialog.dismiss() }, wide().apply { topMargin = dpx(14) })
        showCard(dialog, column)
    }

    /** 口令签到：四位数字码 → 查活动与动作 → 二次确认 → 提交。 */
    private fun askSignCode() {
        val column = dialogCard("口令签到签退")
        column.addView(
            caption("活动主持人会给出四位数字。", top = dpx(2)),
            wide(),
        )
        val field = input("四位数字", InputType.TYPE_CLASS_NUMBER).apply {
            setPadding(dpx(12), dpx(10), dpx(12), dpx(10))
            setBackgroundColor(Color.TRANSPARENT)
        }
        column.addView(
            field,
            wide().apply { topMargin = dpx(12) },
        )
        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        actions.addView(button("取消") { }, gapParams(weight = 1f))
        actions.addView(button("下一步", primary = true) { }, gapParams(weight = 1f, start = 8))
        column.addView(actions, wide().apply { topMargin = dpx(14) })
        val dialog = showCard(column)
        (actions.getChildAt(0) as TextView).setOnClickListener { dialog.dismiss() }
        (actions.getChildAt(1) as TextView).setOnClickListener {
            val code = field.text?.toString()?.trim().orEmpty()
            if (code.length != 4) {
                toast("口令是四位数字")
                return@setOnClickListener
            }
            dialog.dismiss()
            fetchThen(
                label = "正在查这个口令",
                failTitle = "查口令失败",
                work = { session, token -> SecondClassApi.signCode(session, token, code) },
            ) { sign -> confirmSignCode(sign) }
        }
    }

    /** 查到是哪个活动、签到还是签退之后，再问一次：这一步之后是真提交，不能撤销。 */
    private fun confirmSignCode(sign: SecondClassApi.SignCode) {
        AlertDialog.Builder(this)
            .setTitle("${sign.modeText}确认")
            .setMessage("活动：${sign.actName}\n\n确定要${sign.modeText}吗？提交后不能撤销。")
            .setNegativeButton("再看看", null)
            .setPositiveButton("确认${sign.modeText}") { _, _ ->
                fetchThen(
                    label = "正在${sign.modeText}",
                    failTitle = "${sign.modeText}失败",
                    work = { session, token -> SecondClassApi.signByCode(session, token, sign) },
                ) { message ->
                    toast(message)
                    // 签到会改变记录，把「签到签退记录」这一屏的旧数据标记为过期
                    lastSignAt = System.currentTimeMillis()
                }
            }
            .show()
    }

    /**
     * 扫码签到：启动 app 内置的扫码页（zxing-core 解相机帧），把拿到的字符串当 JSON 提交。
     *
     * 二维码里就是官方前端 `JSON.parse` 之后直接 POST 的那个对象，
     * 所以这里只负责解析，字段不猜。扫码页会自己申请相机权限。
     */
    private fun scanSign() {
        startActivityForResult(Intent(this, ScanActivity::class.java), REQ_SCAN)
    }

    private fun onScanResult(raw: String) {
        val payload = runCatching { JSONObject(raw) }.getOrNull()
        if (payload == null) {
            toast("这个二维码不是签到码")
            return
        }
        val name = payload.optString("name").ifBlank { payload.optString("actName") }
        val mode = payload.optString("mode")
        val action = if (mode == "out") "签退" else "签到"
        AlertDialog.Builder(this)
            .setTitle("$action 确认")
            .setMessage(
                (if (name.isBlank()) "扫描到一个签到码" else "活动：$name") +
                    "\n\n确定要$action 吗？提交后不能撤销。",
            )
            .setNegativeButton("再看看", null)
            .setPositiveButton("确认$action") { _, _ ->
                fetchThen(
                    label = "正在$action",
                    failTitle = "$action 失败",
                    work = { session, token -> SecondClassApi.scanSign(session, token, payload).first },
                ) { message ->
                    toast(message)
                    lastSignAt = System.currentTimeMillis()
                }
            }
            .show()
    }

    // ---------- 我的组织 ----------

    private fun openMyOrgs() {
        fetchThen(
            label = "正在取我的组织",
            failTitle = "取我的组织失败",
            work = { session, token -> SecondClassApi.myOrgs(session, token) },
        ) { list -> showMyOrgs(list) }
    }

    /** 我的组织：照官网「我的组织」页——名字 + 组织/班级类型 + 身份，审核中挂红标。 */
    private fun showMyOrgs(list: List<SecondClassApi.MyOrg>) {
        val column = dialogCard("我的组织")
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }

        if (list.isEmpty()) {
            box.addView(stateBlock("还没有加入组织", "在学校第二课堂官网加入组织或班级后，这里会显示。"))
        } else {
            list.forEach { org ->
                val row = LinearLayout(this).apply {
                    orientation = LinearLayout.HORIZONTAL
                    gravity = Gravity.CENTER_VERTICAL
                    setPadding(dpx(12), dpx(12), dpx(12), dpx(12))
                    background = GradientDrawable().apply {
                        cornerRadius = dpx(10).toFloat()
                        setColor(Ui.PAPER_2)
                        setStroke(dpx(1), Ui.RULE)
                    }
                }
                // 头像：有 logo 就不管它（零依赖不引图片加载），没 logo 取名字前两字
                val initial = TextView(this).apply {
                    text = org.name.take(2)
                    textSize = 14f
                    typeface = Ui.medium
                    setTextColor(Ui.ACCENT_DEEP)
                    gravity = Gravity.CENTER
                    setBackgroundResource(0)
                    background = GradientDrawable().apply {
                        shape = GradientDrawable.OVAL
                        setColor(Ui.PAPER_3)
                    }
                }
                row.addView(initial, LinearLayout.LayoutParams(dpx(40), dpx(40)))

                val info = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
                info.addView(TextView(this).apply {
                    text = org.name
                    textSize = 14f
                    typeface = Ui.medium
                    setTextColor(Ui.INK)
                    maxLines = 2
                    ellipsize = TextUtils.TruncateAt.END
                })
                val meta = buildList {
                    add(org.kindText)
                    if (org.roleText.isNotBlank()) add(org.roleText)
                    if (org.pending) add("审核中")
                }
                info.addView(TextView(this).apply {
                    text = meta.joinToString("　")
                    textSize = 12f
                    typeface = Ui.body
                    setTextColor(if (org.pending) Color.parseColor("#C0392B") else Ui.MUTED)
                    setPadding(0, dpx(4), 0, 0)
                })
                row.addView(
                    info,
                    LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply {
                        leftMargin = dpx(12)
                    },
                )
                box.addView(row, topGap(8))
            }
        }

        column.addView(
            ScrollView(this).apply { addView(box) },
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dpx(380)),
        )
        val close = button("关闭", primary = true) { }
        column.addView(close, wide().apply { topMargin = dpx(14) })
        val dialog = showCard(column)
        close.setOnClickListener { dialog.dismiss() }
    }

    private fun openApplies() {
        fetchThen(
            label = "正在取申报记录",
            failTitle = "取申报记录失败",
            work = { session, token -> SecondClassApi.myApplies(session, token) },
        ) { list -> showApplies(list) }
    }

    private fun showApplies(list: List<SecondClassApi.Apply>) {
        val column = dialogCard("我的申报")
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }

        if (list.isEmpty()) {
            box.addView(stateBlock("还没有申报记录", "申报走第二课堂官网，那边提交后会出现在这里。"))
        } else {
            list.forEach { apply ->
                val card = LinearLayout(this).apply {
                    orientation = LinearLayout.VERTICAL
                    setPadding(dpx(12), dpx(10), dpx(12), dpx(10))
                    background = GradientDrawable().apply {
                        cornerRadius = dpx(10).toFloat()
                        setColor(Ui.PAPER_2)
                        setStroke(dpx(1), Ui.RULE)
                    }
                }
                card.addView(TextView(this).apply {
                    text = apply.proName
                    textSize = 14f
                    typeface = Ui.medium
                    setTextColor(Ui.INK)
                    maxLines = 2
                    ellipsize = TextUtils.TruncateAt.END
                })
                val meta = buildList {
                    if (apply.optionName.isNotBlank()) add(apply.optionName)
                    if (apply.className.isNotBlank()) add(apply.className)
                    if (apply.hours > 0) add("${format(apply.hours, 1)} 分")
                }
                if (meta.isNotEmpty()) {
                    card.addView(TextView(this).apply {
                        text = meta.joinToString("　　")
                        textSize = 12f
                        typeface = Ui.body
                        setTextColor(Ui.MUTED)
                        setPadding(0, dpx(5), 0, 0)
                    })
                }
                val whenText = listOf(apply.startTime, apply.endTime)
                    .map { SecondClassApi.prettyTime(it) }
                    .filter { it.isNotBlank() }
                    .joinToString(" – ")
                val foot = LinearLayout(this).apply {
                    orientation = LinearLayout.HORIZONTAL
                    gravity = Gravity.CENTER_VERTICAL
                    setPadding(0, dpx(6), 0, 0)
                }
                foot.addView(TextView(this).apply {
                    text = whenText
                    textSize = 11f
                    typeface = Ui.mono
                    setTextColor(Ui.MUTED)
                }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
                foot.addView(TextView(this).apply {
                    text = apply.statusText
                    textSize = 11f
                    typeface = Ui.mono
                    setTextColor(
                        when (apply.status) {
                            1 -> Ui.ACCENT_DEEP
                            0 -> Ui.WARN
                            else -> Ui.MUTED
                        },
                    )
                }, gapParams(start = 8))
                card.addView(foot)
                box.addView(card, topGap(8))
            }
        }

        column.addView(
            ScrollView(this).apply { addView(box) },
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dpx(360)),
        )
        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        actions.addView(button("关闭", primary = true) { }, gapParams(weight = 1f))
        column.addView(actions, wide().apply { topMargin = dpx(14) })
        val dialog = showCard(column)
        (actions.getChildAt(0) as TextView).setOnClickListener { dialog.dismiss() }
    }

    private fun openProfile() {
        fetchThen(
            label = "正在取学籍信息",
            failTitle = "取学籍信息失败",
            work = { session, token -> SecondClassApi.myProfile(session, token) },
        ) { profile -> showProfile(profile) }
    }

    private fun showProfile(profile: SecondClassApi.Profile) {
        val column = dialogCard("学籍信息")
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }

        fun row(label: String, value: String) {
            if (value.isBlank()) return
            val line = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                setPadding(0, dpx(5), 0, dpx(5))
            }
            line.addView(TextView(this).apply {
                text = label
                textSize = 12f
                typeface = Ui.mono
                setTextColor(Ui.MUTED)
                width = dpx(72)
            })
            line.addView(TextView(this).apply {
                text = value
                textSize = 13.5f
                typeface = Ui.body
                setTextColor(Ui.INK_2)
            }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            box.addView(line)
        }

        // 字段与顺序照官网「学籍信息」页：姓名 学号 性别 民族 政治面貌 校区 院系 专业 年级 班级
        row("姓名", profile.name)
        row("学号", profile.code)
        row("性别", profile.gender)
        row("民族", profile.ethnicName)
        row("政治面貌", profile.politics)
        row("所属校区", profile.campusName)
        row("所属院系", profile.collegeName)
        row("所属专业", profile.majorName)
        row("所属年级", profile.gradeName)
        row("所属班级", profile.className)

        column.addView(
            ScrollView(this).apply { addView(box) },
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dpx(380)),
        )
        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        actions.addView(button("关闭", primary = true) { }, gapParams(weight = 1f))
        column.addView(actions, wide().apply { topMargin = dpx(14) })
        val dialog = showCard(column)
        (actions.getChildAt(0) as TextView).setOnClickListener { dialog.dismiss() }
    }

    private fun openSigns() {
        fetchThen(
            label = "正在取签到记录",
            failTitle = "取签到记录失败",
            work = { session, token -> SecondClassApi.signRecords(session, token) },
        ) { list -> showSigns(list) }
    }

    private fun showSigns(list: List<SecondClassApi.SignRecord>) {
        val column = dialogCard("签到签退记录")
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }

        if (list.isEmpty()) {
            box.addView(stateBlock("还没有签到记录", "报名并参加活动后，签到签退会出现在这里。记录稍有延迟。"))
        } else {
            list.forEach { record ->
                val card = LinearLayout(this).apply {
                    orientation = LinearLayout.VERTICAL
                    setPadding(dpx(12), dpx(10), dpx(12), dpx(10))
                    background = GradientDrawable().apply {
                        cornerRadius = dpx(10).toFloat()
                        setColor(Ui.PAPER_2)
                        setStroke(dpx(1), Ui.RULE)
                    }
                }
                card.addView(TextView(this).apply {
                    text = record.name
                    textSize = 14f
                    typeface = Ui.medium
                    setTextColor(Ui.INK)
                    maxLines = 2
                    ellipsize = TextUtils.TruncateAt.END
                })
                card.addView(TextView(this).apply {
                    text = SecondClassApi.prettySpan(record.startTime, record.endTime)
                    textSize = 11f
                    typeface = Ui.mono
                    setTextColor(Ui.MUTED)
                    setPadding(0, dpx(4), 0, 0)
                })

                val counts = buildList {
                    if (record.effective > 0) add("有效 ${record.effective}")
                    if (record.ongoing > 0) add("进行中 ${record.ongoing}")
                    if (record.invalid > 0) add("无效 ${record.invalid}")
                }
                if (counts.isNotEmpty()) {
                    card.addView(TextView(this).apply {
                        text = counts.joinToString("　　")
                        textSize = 11f
                        typeface = Ui.mono
                        setTextColor(Ui.INK_2)
                        setPadding(0, dpx(5), 0, 0)
                    })
                }

                record.segments.forEach { segment ->
                    val segLine = LinearLayout(this).apply {
                        orientation = LinearLayout.HORIZONTAL
                        gravity = Gravity.CENTER_VERTICAL
                        setPadding(0, dpx(5), 0, 0)
                    }
                    val span = buildString {
                        append("签到 ").append(segment.inTime.ifBlank { "—" })
                        append("　→　签退 ").append(segment.outTime.ifBlank { "—" })
                    }
                    segLine.addView(TextView(this).apply {
                        text = span
                        textSize = 11f
                        typeface = Ui.mono
                        setTextColor(Ui.MUTED)
                        maxLines = 1
                        ellipsize = TextUtils.TruncateAt.END
                    }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
                    val tagText = when (segment.status) {
                        "effective" -> if (segment.minutes > 0) "${duration(segment.minutes)}" else segment.statusText
                        else -> segment.statusText
                    }
                    segLine.addView(TextView(this).apply {
                        text = tagText
                        textSize = 11f
                        typeface = Ui.mono
                        setTextColor(if (segment.status == "effective") Ui.ACCENT_DEEP else Ui.WARN)
                    }, gapParams(start = 8))
                    card.addView(segLine)
                }
                box.addView(card, topGap(8))
            }
        }

        column.addView(
            ScrollView(this).apply { addView(box) },
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dpx(380)),
        )
        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        actions.addView(button("关闭", primary = true) { }, gapParams(weight = 1f))
        column.addView(actions, wide().apply { topMargin = dpx(14) })
        val dialog = showCard(column)
        (actions.getChildAt(0) as TextView).setOnClickListener { dialog.dismiss() }
    }

    /** 分钟数说成人话：90 → 1小时30分。 */
    private fun duration(minutes: Int): String {
        val h = minutes / 60
        val m = minutes % 60
        return if (h > 0) "${h}小时${m}分" else "${m}分"
    }

    // ---------- 抓取 ----------

    /** 所有「登录 → 干活」都走这里：统一遮罩、统一失败弹层。 */
    private fun <T> fetchThen(
        label: String,
        failTitle: String,
        work: (JwxtApi.Session, String) -> T,
        then: (T) -> Unit,
    ) {
        if (busy) {
            toast("正在抓取，稍等")
            return
        }
        val saved = SecondClassStore.loadLogin(db)
        if (saved == null) {
            askLogin()
            return
        }
        val startTab = tab
        setBusy(true, label)
        Thread {
            runCatching {
                val login = SecondClassApi.login(saved.user, saved.password)
                SecondClassStore.saveToken(db, login.token)
                work(login.session, login.token)
            }
                .onSuccess { value ->
                    runOnUiThread {
                        // 用户在这趟请求期间切了页：别把详情弹窗串到新页，也别误关新页的遮罩
                        if (tab != startTab) return@runOnUiThread
                        setBusy(false)
                        then(value)
                    }
                }
                .onFailure { error ->
                    runOnUiThread {
                        if (tab != startTab) return@runOnUiThread
                        setBusy(false)
                        fail(failTitle, error)
                    }
                }
        }.start()
    }

    /** 拉活动。有缓存时静默换新（不弹遮罩），没缓存才显示抓取层。 */
    private fun loadActs(silent: Boolean) {
        if (busy) return
        val bucketPast = pastOnly
        val saved = SecondClassStore.loadLogin(db) ?: run {
            renderContent()
            return
        }
        if (!silent) setBusy(true, if (bucketPast) "正在拉历史活动" else "正在拉报名中")
        Thread {
            runCatching {
                val login = SecondClassApi.login(saved.user, saved.password)
                SecondClassStore.saveToken(db, login.token)
                SecondClassApi.list(
                    login.session,
                    login.token,
                    statusId = if (bucketPast) "5" else "1",
                    size = PAGE_SIZE,
                )
            }
                .onSuccess { list ->
                    SecondClassStore.saveActs(db, list, past = bucketPast)
                    runOnUiThread {
                        if (bucketPast) actsPast = list else actsCurrent = list
                        actsError = null
                        if (!silent && tab == TAB_ACTS) setBusy(false)
                        if (tab == TAB_ACTS) renderContent()
                    }
                }
                .onFailure { error ->
                    runOnUiThread {
                        val message = error.message ?: error.javaClass.simpleName
                        // 已经有数据时不要把列表换成错误页——那正是「点刷新整页跳一下」的来源；
                        // 只在没有可看的数据时才进错误态。
                        val hadData = if (bucketPast) actsPast != null else actsCurrent != null
                        if (hadData) {
                            toast("刷新失败：$message")
                        } else {
                            actsError = message
                        }
                        if (!silent && tab == TAB_ACTS) setBusy(false)
                        if (tab == TAB_ACTS) renderContent()
                    }
                }
        }.start()
    }

    private fun loadOrgs(silent: Boolean = false) {
        if (busy) return
        val saved = SecondClassStore.loadLogin(db) ?: run {
            renderContent()
            return
        }
        orgsError = null
        if (!silent) setBusy(true, "正在拉组织清单")
        Thread {
            runCatching {
                val login = SecondClassApi.login(saved.user, saved.password)
                SecondClassStore.saveToken(db, login.token)
                SecondClassApi.orgList(login.session, login.token, page = 1, size = 100)
            }
                .onSuccess { list ->
                    runOnUiThread {
                        orgs = if (silent) (orgs ?: list) else list
                        if (!silent && tab == TAB_ORGS) setBusy(false)
                        if (tab == TAB_ORGS) renderContent()
                    }
                }
                .onFailure { error ->
                    runOnUiThread {
                        val message = error.message ?: error.javaClass.simpleName
                        // 同理：有旧清单就只提示，不把组织页变成错误页
                        if (orgs != null) {
                            toast("刷新失败：$message")
                        } else {
                            orgsError = message
                        }
                        if (!silent && tab == TAB_ORGS) setBusy(false)
                        if (tab == TAB_ORGS) renderContent()
                    }
                }
        }.start()
    }

    private fun loadCredit(silent: Boolean = false) {
        if (busy) return
        val saved = SecondClassStore.loadLogin(db)
        if (saved == null) {
            askLogin()
            return
        }
        if (!silent) setBusy(true, "正在拉学分进度")
        Thread {
            runCatching {
                EduSecondClass.fetch(EduSecondClass.DEFAULT_BASE, saved.user, saved.password)
            }
                .onSuccess { report ->
                    EduStore.saveSecond(db, report)
                    runOnUiThread {
                        credit = report
                        if (!silent && tab == TAB_ME) setBusy(false)
                        if (tab == TAB_ME) renderContent()
                    }
                }
                .onFailure { error ->
                    runOnUiThread {
                        if (!silent && tab == TAB_ME) setBusy(false)
                        // 静默刷新失败就悄悄算了：用户此刻可能在看活动，不该被打断
                        if (silent) return@runOnUiThread
                        fail("拉学分失败", error)
                    }
                }
        }.start()
    }

    /** 打开页面时悄悄换一份新数据，不打断正在看的内容。 */
    private fun autoRefresh() {
        if (SecondClassStore.loadLogin(db) == null) return
        loadActs(silent = true)
        // 顺带刷学分也走静默：进页面时用户多半在看活动，
        // 不该被「正在拉学分进度」的遮罩挡住。
        loadCredit(silent = true)
    }

    /** 下拉 / 点重试都走这里：按当前 tab 决定刷什么。 */
    private fun beginRefresh() {
        when (tab) {
            TAB_ACTS -> loadActs(silent = false)
            TAB_ORGS -> loadOrgs()
            else -> loadCredit()
        }
    }

    // ---------- 登录 ----------

    private fun askLogin() {
        if (busy) {
            toast("正在抓取，稍等")
            return
        }
        val column = dialogCard("登录第二课堂")
        val saved = SecondClassStore.loadLogin(db)
        val user = input("学号", InputType.TYPE_CLASS_NUMBER)
        user.setText(saved?.user ?: db.getMeta("2kt_xh").orEmpty())
        val pwd = input(
            "第二课堂密码（初始为学号@abc）",
            InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD,
        )
        if (saved != null) pwd.setText(saved.password)
        column.addView(user, wide())
        column.addView(pwd, wide().apply { topMargin = dpx(10) })
        column.addView(caption("学号与密码会记住；密码加密存在系统密钥库里。", top = dpx(8)))

        val actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        actions.addView(button("取消") { }, gapParams(weight = 1f, end = 8))
        actions.addView(button("登录", primary = true) { }, gapParams(weight = 1f))
        column.addView(actions, wide().apply { topMargin = dpx(16) })

        val dialog = showCard(column)
        (actions.getChildAt(0) as TextView).setOnClickListener { dialog.dismiss() }
        (actions.getChildAt(1) as TextView).setOnClickListener {
            val name = user.text.toString().trim()
            val pass = pwd.text.toString()
            if (name.isEmpty() || pass.isEmpty()) {
                toast("学号和密码都要填")
                return@setOnClickListener
            }
            dialog.dismiss()
            SecondClassStore.saveLogin(db, name, pass)
            db.setMeta("2kt_xh", name)
            loginAndFetch(name, pass)
        }
    }

    private fun loginAndFetch(user: String, pwd: String) {
        setBusy(true, "正在登录并抓取")
        val bucketPast = pastOnly
        Thread {
            runCatching {
                val login = SecondClassApi.login(user, pwd)
                SecondClassStore.saveToken(db, login.token)
                val list = SecondClassApi.list(
                    login.session,
                    login.token,
                    statusId = if (bucketPast) "5" else "1",
                    size = PAGE_SIZE,
                )
                SecondClassStore.saveActs(db, list, past = bucketPast)
                val report = runCatching { EduSecondClass.fetch(EduSecondClass.DEFAULT_BASE, user, pwd) }
                    .getOrNull()
                if (report != null) EduStore.saveSecond(db, report)
                Triple(list, report, bucketPast)
            }
                .onSuccess { (list, report, wasPast) ->
                    runOnUiThread {
                        if (wasPast) actsPast = list else actsCurrent = list
                        if (report != null) credit = report
                        actsError = null
                        setBusy(false)
                        renderContent()
                        toast("拿到 ${list.size} 个活动")
                    }
                }
                .onFailure { error ->
                    runOnUiThread {
                        setBusy(false)
                        fail("抓第二课堂失败", error)
                    }
                }
        }.start()
    }

    // ---------- 小控件 ----------

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
            maxLines = 2
            ellipsize = TextUtils.TruncateAt.END
            setPadding(0, 0, 0, dpx(14))
        })
        return column
    }

    private fun showCard(content: LinearLayout): Dialog {
        val dialog = Dialog(this)
        showCard(dialog, content)
        return dialog
    }

    /** 复用同一张卡：调用方自己建 Dialog（要在内部回调里 dismiss），这里只负责摆放与调暗。 */
    private fun showCard(dialog: Dialog, content: LinearLayout) {
        dialog.requestWindowFeature(android.view.Window.FEATURE_NO_TITLE)
        dialog.setContentView(
            content,
            ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
            ),
        )
        dialog.window?.apply {
            setBackgroundDrawable(ColorDrawable(Color.TRANSPARENT))
            setLayout(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
            )
            setGravity(Gravity.CENTER)
            addFlags(android.view.WindowManager.LayoutParams.FLAG_DIM_BEHIND)
            setDimAmount(0.32f)
            setSoftInputMode(android.view.WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE)
        }
        dialog.show()
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
        runCatching { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(SecondClassApi.BASE))) }
            .onFailure { toast("这台设备没有能打开的浏览器") }
    }

    private fun button(
        text: String,
        primary: Boolean = false,
        onClick: () -> Unit,
    ): TextView = pill(text, primary = primary, small = true).also { view ->
        view.setOnClickListener { onClick() }
    }

    private fun statRow(vararg cells: Pair<String, String>): LinearLayout {
        val row = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        row.setPadding(0, dpx(6), 0, dpx(4))
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

    /** 统计行第三格：一个可点的「签到」按钮，跟前两格同结构，凑成三等分对称。 */
    private fun signStatCell(): LinearLayout {
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        box.addView(TextView(this).apply {
            text = "签到"
            textSize = 15f
            typeface = Ui.medium
            setTextColor(Ui.ACCENT_INK)
            gravity = Gravity.CENTER
            setPadding(dpx(12), dpx(4), dpx(12), dpx(4))
            background = GradientDrawable().apply {
                cornerRadius = dpx(10).toFloat()
                setColor(Ui.ACCENT_DEEP)
            }
            isClickable = true
            setOnClickListener { pickSign() }
        }, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.WRAP_CONTENT,
            ViewGroup.LayoutParams.WRAP_CONTENT,
        ).apply { gravity = Gravity.CENTER_HORIZONTAL })
        box.addView(TextView(this).apply {
            text = "现场签到"
            textSize = 11f
            typeface = Ui.mono
            setTextColor(Ui.MUTED)
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(0, dpx(2), 0, 0)
        }, LinearLayout.LayoutParams(
            LinearLayout.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT,
        ))
        return box
    }

    private fun caption(text: String, color: Int = Ui.MUTED, top: Int = 0): TextView =
        TextView(this).apply {
            this.text = text
            textSize = 11f
            typeface = Ui.mono
            setTextColor(color)
            setPadding(0, top, 0, 0)
        }

    private fun line(name: String, right: String, color: Int, big: Boolean = false, flushRight: Boolean = false): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        row.setPadding(0, dpx(if (big) 14 else 7), 0, dpx(if (big) 14 else 7))
        if (flushRight) {
            // 负 rightMargin 抵消 body 的 20dp 右留白，让右侧分数贴到屏幕最右（负 padding 会被系统忽略）
            row.layoutParams = LinearLayout.LayoutParams(MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
                rightMargin = -dpx(20)
            }
        }
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

    private fun topGap(px: Int) = LinearLayout.LayoutParams(
        ViewGroup.LayoutParams.MATCH_PARENT,
        ViewGroup.LayoutParams.WRAP_CONTENT,
    ).apply { topMargin = dpx(px) }

    private fun wide(): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)

    private fun toast(message: String) = Toast.makeText(this, message, Toast.LENGTH_SHORT).show()

    private fun statusBarHeight(): Int {
        val id = resources.getIdentifier("status_bar_height", "dimen", "android")
        return if (id > 0) resources.getDimensionPixelSize(id) else dpx(24)
    }

    private fun format(value: Double, digits: Int): String =
        String.format(Locale.CHINA, "%.${digits}f", value)

    private fun stamp(at: Long): String = noteStamp(at)

    // ---------- 下拉刷新 ----------

    /**
     * 到顶了再往下拖才算下拉刷新：起手时 canScrollVertically(-1) 为 false 才布防，
     * 拖过 64dp 的 0.8 倍触发。不引第三方库（项目零依赖）。
     *
     * 两个状态必须分清，否则会把正常滚动吃掉：
     * - armed：起手在顶部，有资格演变成下拉；
     * - pulling：这次手势真的往下了，已经接管成下拉，后续事件不再交给滚动。
     */
    private inner class PullScroll(context: Context) : ScrollView(context) {

        var onPull: ((Float) -> Unit)? = null
        var onRelease: ((Boolean) -> Unit)? = null

        private val slop = dpx(PULL_DEAD_ZONE_DP).toFloat()
        private val threshold = dpx(PULL_THRESHOLD_DP).toFloat()
        private var downY = 0f
        private var pulled = 0f
        private var armed = false
        private var pulling = false

        init {
            // 自己画位移，不要再叠系统的边缘光晕，否则两者一起动看起来像卡住
            overScrollMode = OVER_SCROLL_NEVER
            // 滚动条钉死在屏幕最右。之前它跟着 ScrollView 的右边缘走，而那个边缘
            // 正好压在卡片右边框上，看起来就是一条线挡着内容；现在滚动区通栏、
            // 内容自己留 20dp，条子落在右边那片空白里，字和框都不挨着。
            isVerticalScrollBarEnabled = true
            isHorizontalScrollBarEnabled = false
            scrollBarStyle = SCROLLBARS_INSIDE_INSET
            setVerticalScrollbarPosition(SCROLLBAR_POSITION_RIGHT)
        }

        /** 手指按在卡片上时卡片会吃掉 DOWN，所以判定必须在拦截阶段做。 */
        override fun onInterceptTouchEvent(event: MotionEvent): Boolean {
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> {
                    downY = event.y
                    pulled = 0f
                    pulling = false
                    armed = !canScrollVertically(-1)
                    return super.onInterceptTouchEvent(event)
                }
                MotionEvent.ACTION_MOVE -> {
                    if (pulling) return true
                    val dy = event.y - downY
                    // 只有「已在顶部 + 这次是往下拖」才演变成本页的下拉刷新。
                    if (dy > slop && armed) {
                        pulling = true
                        pulled = (dy / threshold).coerceIn(0f, 1.2f)
                        onPull?.invoke(pulled)
                        return true
                    }
                    // 其余全部交还 ScrollView：向上拖它自己会按 abs(位移) > touchSlop 拦截，
                    // 顺手把「这次手势变味了」记下来，免得回拖时又被当下拉。
                    if (dy < -slop) armed = false
                    return super.onInterceptTouchEvent(event)
                }
                else -> {
                    armed = false
                    pulling = false
                    pulled = 0f
                    onPull?.invoke(0f)
                    return super.onInterceptTouchEvent(event)
                }
            }
        }

        override fun onTouchEvent(event: MotionEvent): Boolean {
            // 只有真的被接管成下拉时才吞事件，其余一律交给 ScrollView 正常滚动
            if (!pulling) return super.onTouchEvent(event)
            when (event.actionMasked) {
                MotionEvent.ACTION_MOVE -> {
                    val dy = event.y - downY
                    // 手指按住时先往下蹭了一点点、接着又往上推：这是想翻列表，不是想刷新。
                    // 这里必须把手势还给 ScrollView，否则整次滑动都被当成下拉吃掉，
                    // 手感就是「页面在初始高度总是划不动」。
                    if (dy < -slop) {
                        pulling = false
                        armed = false
                        pulled = 0f
                        onPull?.invoke(0f)
                        return super.onTouchEvent(event)
                    }
                    pulled = (dy.coerceAtLeast(0f) / threshold).coerceIn(0f, 1.2f)
                    onPull?.invoke(pulled)
                    return true
                }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    val triggered = event.actionMasked == MotionEvent.ACTION_UP && pulled >= PULL_TRIGGER
                    pulling = false
                    armed = false
                    pulled = 0f
                    onPull?.invoke(0f)
                    if (triggered) {
                        onRelease?.invoke(true)
                    }
                    return true
                }
            }
            return super.onTouchEvent(event)
        }
    }
}

private const val MATCH_PARENT = ViewGroup.LayoutParams.MATCH_PARENT
