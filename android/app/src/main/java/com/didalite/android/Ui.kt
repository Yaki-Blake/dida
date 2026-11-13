package com.didalite.android

import android.app.Activity
import android.content.res.ColorStateList
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.RippleDrawable
import android.graphics.drawable.StateListDrawable
import android.os.Build
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsetsController
import android.widget.LinearLayout
import android.widget.TextView

/**
 * 界面令牌与控件工厂。色值与字号取自 docs/design/tokens.css（Hallmark · dose）。
 * 原生 View 实现，不引第三方库；字体用系统 sans / monospace 对应草图里的
 * Schibsted Grotesk / Instrument Sans / DM Mono。
 */
object Ui {

    val PAPER = Color.parseColor("#F7F5EF")
    val PAPER_2 = Color.parseColor("#EFF6F0")
    val PAPER_3 = Color.parseColor("#E2EFE6")
    val INK = Color.parseColor("#12231E")
    val INK_2 = Color.parseColor("#2C3B36")
    val MUTED = Color.parseColor("#5C6F68")
    val RULE = Color.parseColor("#D2DED2")
    val ACCENT = Color.parseColor("#1F8A63")
    val ACCENT_DEEP = Color.parseColor("#0F6E4F")
    val ACCENT_INK = Color.parseColor("#FFFFFF")
    val WARN = Color.parseColor("#A97515")

    val display: Typeface = Typeface.create("sans-serif", Typeface.BOLD)
    val body: Typeface = Typeface.create("sans-serif", Typeface.NORMAL)
    val medium: Typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
    val mono: Typeface = Typeface.create("monospace", Typeface.NORMAL)
    val monoBold: Typeface = Typeface.create("monospace", Typeface.BOLD)
}

fun Activity.dpx(value: Int): Int = (value * resources.displayMetrics.density).toInt()

/** 纸色底 + 深色状态栏图标，三页统一。放在 setContentView 之后调用。 */
fun Activity.applyDidaTheme() {
    window.statusBarColor = Ui.PAPER
    window.navigationBarColor = Ui.PAPER
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
        val light = WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS or
            WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS
        runCatching { window.insetsController?.setSystemBarsAppearance(light, light) }
    } else {
        @Suppress("DEPRECATION")
        window.decorView.systemUiVisibility =
            View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR or View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR
    }
}

/**
 * 圆角按钮：常规 46dp 高、小号 34dp 高。
 * primary 用深绿底白字，其余浅底 + 1dp 描边；禁用时灰底灰字。
 */
fun Activity.pill(text: String, primary: Boolean = false, small: Boolean = false): TextView {
    val height = if (small) 34 else 46
    val radius = dpx(10).toFloat()

    fun shape(fill: Int, stroke: Int) = GradientDrawable().apply {
        cornerRadius = radius
        setColor(fill)
        setStroke(dpx(1), stroke)
    }

    val fill = if (primary) Ui.ACCENT_DEEP else Ui.PAPER_2
    val stroke = if (primary) Ui.ACCENT_DEEP else Ui.RULE
    val fg = if (primary) Ui.ACCENT_INK else Ui.INK

    val background = StateListDrawable().apply {
        addState(intArrayOf(-android.R.attr.state_enabled), shape(Ui.PAPER_2, Ui.RULE))
        addState(
            intArrayOf(),
            RippleDrawable(
                ColorStateList.valueOf(Color.parseColor("#1F000000")),
                shape(fill, stroke),
                shape(Color.WHITE, Color.WHITE),
            ),
        )
    }

    return TextView(this).apply {
        this.text = text
        textSize = if (small) 12f else 14f
        typeface = Ui.medium
        gravity = Gravity.CENTER
        this.background = background
        setTextColor(
            ColorStateList(
                arrayOf(intArrayOf(-android.R.attr.state_enabled), intArrayOf()),
                intArrayOf(Ui.MUTED, fg),
            )
        )
        isClickable = true
        isFocusable = true
        minimumHeight = dpx(height)
        minWidth = 0
        minimumWidth = 0
        setPadding(if (small) dpx(12) else dpx(8), 0, if (small) dpx(12) else dpx(8), 0)
    }
}

/** 1dp 分隔线，宽度撑满。 */
fun Activity.pageRule(topMargin: Int = 0): View = View(this).apply {
    setBackgroundColor(Ui.RULE)
    layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dpx(1))
        .apply { this.topMargin = dpx(topMargin) }
}

/** 等宽、等间距的一行三按钮里用的 LayoutParams。 */
fun Activity.gapParams(weight: Float = 0f, start: Int = 0, end: Int = 0) =
    LinearLayout.LayoutParams(
        if (weight > 0f) 0 else ViewGroup.LayoutParams.WRAP_CONTENT,
        ViewGroup.LayoutParams.WRAP_CONTENT,
        weight,
    ).apply {
        marginStart = dpx(start)
        marginEnd = dpx(end)
    }
