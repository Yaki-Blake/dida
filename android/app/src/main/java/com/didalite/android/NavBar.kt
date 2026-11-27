package com.didalite.android

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.LinearLayout

/**
 * 底部导航：课表 / 学业 / 专注 / 第二课堂 / 设置。
 *
 * 「时间」与「便签」合并成「专注」一个一级入口（页内再分闹钟 / 便签两个子 tab），
 * 腾出的位置给「第二课堂」。
 * 图标是手画的简笔画，不引图片资源：未选中只描边，选中填主题色。
 */
object NavBar {
    const val SCHEDULE = 0
    const val STUDY = 1
    const val FOCUS = 2
    const val SECOND = 3
    const val SETTINGS = 4

    /** 旧入口的别名：闹钟 / 便签页现在都归到「专注」下，外部还按这两个名字引用。 */
    const val TIME = FOCUS
    const val NOTES = FOCUS

    /** 底栏总高度（dp）。页面算可用高度时要扣掉它，避免内容被压在导航下面。 */
    const val BAR_HEIGHT_DP = 65

    /** 底栏整体上提的距离（dp）：不再贴屏幕底，底部留出一条同色空白；图标大小不变。 */
    const val BAR_LIFT_DP = 14

    private val TARGETS = arrayOf(
        ScheduleActivity::class.java,
        StudyActivity::class.java,
        FocusActivity::class.java,
        SecondClassActivity::class.java,
        SettingsActivity::class.java,
    )
    private val LABELS = arrayOf("课表", "学业", "专注", "第二课堂", "设置")

    /** 把页面内容包成「内容 + 底部导航」。导航浮在内容上方，内容底部留出同样高度。 */
    fun wrap(activity: Activity, content: View, current: Int): View {
        val barH = dp(activity, BAR_HEIGHT_DP)
        val root = FrameLayout(activity).apply { setBackgroundColor(Ui.PAPER) }
        val lift = dp(activity, BAR_LIFT_DP)
        // 底部内边距 = 底栏高度 + 上提距离：底栏抬起来后，最后一行内容也不会被它盖住
        content.setPadding(
            content.paddingLeft,
            content.paddingTop,
            content.paddingRight,
            content.paddingBottom + barH + lift
        )
        root.addView(content, FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT
        ))
        // bottomMargin = 上提距离：整条底栏离开屏幕底，下面露出一条纸色空白
        root.addView(bar(activity, current), FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, barH, Gravity.BOTTOM
        ).apply { bottomMargin = lift })
        return root
    }

    fun bar(activity: Activity, current: Int): View {
        val root = LinearLayout(activity).apply { orientation = LinearLayout.VERTICAL }
        root.addView(View(activity).apply { setBackgroundColor(Ui.RULE) },
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(activity, 1)))

        val row = LinearLayout(activity).apply {
            orientation = LinearLayout.HORIZONTAL
            setBackgroundColor(Ui.PAPER)
        }
        LABELS.forEachIndexed { index, label ->
            val item = NavItem(activity, index, label, index == current).apply {
                isClickable = true
                setOnClickListener {
                    if (index == current) return@setOnClickListener
                    activity.startActivity(
                        Intent(activity, TARGETS[index]).addFlags(
                            Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP
                        )
                    )
                    activity.overridePendingTransition(0, 0)
                }
            }
            row.addView(item, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MATCH_PARENT, 1f))
        }
        // 行高固定为 BAR_HEIGHT_DP − 1（减去顶部 1dp 分隔线）：无论调用方把底栏放在
        // 固定高度容器还是 WRAP_CONTENT 容器里，底栏总高都恒定，切换页面时上界不跳。
        root.addView(row, LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, dp(activity, BAR_HEIGHT_DP - 1)
        ))
        return root
    }

    private fun dp(context: Context, value: Int): Int =
        (value * context.resources.displayMetrics.density).toInt()
}

/** 五个简笔画图标 + 文字：未选中描边，选中填色。自己画文字，避免嵌套 TextView 被压扁。 */
private class NavItem(
    context: Context,
    private val kind: Int,
    private val label: String,
    private val selected: Boolean,
) : View(context) {

    private val density = context.resources.displayMetrics.density
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        strokeCap = Paint.Cap.ROUND
        strokeJoin = Paint.Join.ROUND
    }
    private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        textSize = 11f * density
        textAlign = Paint.Align.CENTER
        typeface = if (selected) Ui.medium else Ui.body
        color = if (selected) Ui.ACCENT else Ui.MUTED
    }
    private val ink = Ui.ACCENT
    private val paper = Ui.PAPER

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        setMeasuredDimension(
            MeasureSpec.getSize(widthMeasureSpec),
            MeasureSpec.getSize(heightMeasureSpec)
        )
    }

    override fun onDraw(canvas: Canvas) {
        val iconSize = 24f * density
        val gap = 3f * density
        val fm = textPaint.fontMetrics
        val textH = fm.descent - fm.ascent
        val top = (height - (iconSize + gap + textH)) / 2f
        val cx = width / 2f
        val iconCy = top + iconSize / 2f
        val r = iconSize / 2f
        paint.color = ink
        when (kind) {
            NavBar.SCHEDULE -> calendar(canvas, cx, iconCy, r)
            NavBar.STUDY -> cap(canvas, cx, iconCy, r)
            NavBar.FOCUS -> target(canvas, cx, iconCy, r)
            NavBar.SECOND -> medal(canvas, cx, iconCy, r)
            else -> sliders(canvas, cx, iconCy, r)
        }
        canvas.drawText(label, cx, top + iconSize + gap - fm.ascent, textPaint)
    }

    /** 日历：圆角方框 + 顶部一条横线 + 两个挂环 */
    private fun calendar(canvas: Canvas, cx: Float, cy: Float, r: Float) {
        val body = RectF(cx - r * 0.74f, cy - r * 0.60f, cx + r * 0.74f, cy + r * 0.76f)
        val radius = r * 0.20f
        if (selected) {
            paint.style = Paint.Style.FILL
            canvas.drawRoundRect(body, radius, radius, paint)
            paint.color = paper
            paint.style = Paint.Style.STROKE
            paint.strokeWidth = r * 0.12f
            val lineY = body.top + r * 0.30f
            canvas.drawLine(body.left + r * 0.10f, lineY, body.right - r * 0.10f, lineY, paint)
            paint.strokeWidth = r * 0.14f
            canvas.drawLine(cx - r * 0.34f, body.top - r * 0.10f, cx - r * 0.34f, body.top + r * 0.20f, paint)
            canvas.drawLine(cx + r * 0.34f, body.top - r * 0.10f, cx + r * 0.34f, body.top + r * 0.20f, paint)
        } else {
            paint.style = Paint.Style.STROKE
            paint.strokeWidth = r * 0.12f
            canvas.drawRoundRect(body, radius, radius, paint)
            val lineY = body.top + r * 0.30f
            canvas.drawLine(body.left + r * 0.10f, lineY, body.right - r * 0.10f, lineY, paint)
            paint.strokeWidth = r * 0.15f
            canvas.drawLine(cx - r * 0.34f, body.top - r * 0.12f, cx - r * 0.34f, body.top + r * 0.18f, paint)
            canvas.drawLine(cx + r * 0.34f, body.top - r * 0.12f, cx + r * 0.34f, body.top + r * 0.18f, paint)
        }
    }

    /** 学业：学士帽（菱形帽顶 + 帽身 + 流苏） */
    private fun cap(canvas: Canvas, cx: Float, cy: Float, r: Float) {
        val board = Path().apply {
            moveTo(cx, cy - r * 0.62f)
            lineTo(cx + r * 0.88f, cy - r * 0.16f)
            lineTo(cx, cy + r * 0.30f)
            lineTo(cx - r * 0.88f, cy - r * 0.16f)
            close()
        }
        paint.color = ink
        paint.style = if (selected) Paint.Style.FILL else Paint.Style.STROKE
        paint.strokeWidth = r * 0.13f
        canvas.drawPath(board, paint)

        paint.style = Paint.Style.STROKE
        paint.strokeWidth = r * 0.13f
        canvas.drawLine(cx - r * 0.34f, cy + r * 0.34f, cx - r * 0.34f, cy + r * 0.78f, paint)
        canvas.drawLine(cx + r * 0.34f, cy + r * 0.34f, cx + r * 0.34f, cy + r * 0.78f, paint)
        canvas.drawLine(cx + r * 0.88f, cy - r * 0.16f, cx + r * 0.88f, cy + r * 0.40f, paint)

        paint.style = Paint.Style.FILL
        canvas.drawCircle(cx + r * 0.88f, cy + r * 0.52f, r * 0.11f, paint)
    }

    /** 专注：同心圆靶心（专注 / 计时） */
    private fun target(canvas: Canvas, cx: Float, cy: Float, r: Float) {
        val outer = r * 0.78f
        paint.style = if (selected) Paint.Style.FILL else Paint.Style.STROKE
        paint.strokeWidth = r * 0.12f
        canvas.drawCircle(cx, cy, outer, paint)
        paint.color = if (selected) paper else ink
        paint.style = Paint.Style.STROKE
        paint.strokeWidth = r * 0.12f
        canvas.drawCircle(cx, cy, outer * 0.56f, paint)
        paint.style = Paint.Style.FILL
        canvas.drawCircle(cx, cy, outer * 0.20f, paint)
    }

    /** 第二课堂：奖章（圆牌 + 下方两条绶带） */
    private fun medal(canvas: Canvas, cx: Float, cy: Float, r: Float) {
        val medalCy = cy - r * 0.22f
        val radius = r * 0.56f
        // 绶带先画，压在圆牌下面
        paint.color = ink
        paint.style = Paint.Style.STROKE
        paint.strokeWidth = r * 0.20f
        canvas.drawLine(cx - radius * 0.42f, medalCy + radius * 0.55f, cx - radius * 0.72f, cy + r * 0.88f, paint)
        canvas.drawLine(cx + radius * 0.42f, medalCy + radius * 0.55f, cx + radius * 0.72f, cy + r * 0.88f, paint)

        paint.style = if (selected) Paint.Style.FILL else Paint.Style.STROKE
        paint.strokeWidth = r * 0.12f
        canvas.drawCircle(cx, medalCy, radius, paint)
        if (selected) {
            // 选中填色时，中间压一颗纸色五角星，避免一团实心看不出是奖章
            paint.color = paper
            paint.style = Paint.Style.FILL
            canvas.drawPath(star(cx, medalCy, radius * 0.52f), paint)
        } else {
            paint.color = ink
            paint.style = Paint.Style.STROKE
            paint.strokeWidth = r * 0.10f
            canvas.drawPath(star(cx, medalCy, radius * 0.52f), paint)
        }
    }

    /** 以 (cx,cy) 为中心、外接半径 rad 的五角星路径。 */
    private fun star(cx: Float, cy: Float, rad: Float): Path {
        val path = Path()
        for (i in 0 until 10) {
            val angle = Math.toRadians((i * 36 - 90).toDouble())
            val len = if (i % 2 == 0) rad else rad * 0.42f
            val x = cx + (len * Math.cos(angle)).toFloat()
            val y = cy + (len * Math.sin(angle)).toFloat()
            if (i == 0) path.moveTo(x, y) else path.lineTo(x, y)
        }
        path.close()
        return path
    }

    /** 设置：三条滑杆 */
    private fun sliders(canvas: Canvas, cx: Float, cy: Float, r: Float) {
        paint.style = Paint.Style.STROKE
        paint.strokeWidth = r * 0.13f
        paint.color = ink
        val left = cx - r * 0.78f
        val right = cx + r * 0.78f
        val ys = floatArrayOf(cy - r * 0.52f, cy, cy + r * 0.52f)
        val knobX = floatArrayOf(cx - r * 0.22f, cx + r * 0.30f, cx - r * 0.40f)
        ys.forEachIndexed { i, y ->
            canvas.drawLine(left, y, right, y, paint)
            paint.style = if (selected) Paint.Style.FILL else Paint.Style.STROKE
            paint.strokeWidth = r * 0.13f
            canvas.drawCircle(knobX[i], y, r * 0.15f, paint)
            paint.style = Paint.Style.STROKE
            paint.strokeWidth = r * 0.13f
        }
    }
}
