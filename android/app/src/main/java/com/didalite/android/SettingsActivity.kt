package com.didalite.android

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.pm.PackageManager
import android.content.res.ColorStateList
import android.graphics.drawable.GradientDrawable
import android.os.Build
import android.os.Bundle
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast

/**
 * 设置页：服务器地址、账号、提醒权限、读系统日历。
 * 地址与 token 存本地 meta；密码不落盘。
 */
class SettingsActivity : Activity() {

    private lateinit var db: Db
    private lateinit var urlInput: EditText
    private lateinit var nameInput: EditText
    private lateinit var pwdInput: EditText
    private lateinit var loginBtn: TextView
    private lateinit var loginSlot: LinearLayout
    private lateinit var stateView: TextView
    private lateinit var notifState: TextView
    private lateinit var exactState: TextView
    private lateinit var batteryState: TextView
    private lateinit var fullScreenState: TextView
    private lateinit var calendarState: TextView
    private val permButtons = HashMap<TextView, TextView>()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        db = Db(this)
        setContentView(buildUi())
        applyDidaTheme()

        urlInput.setText(db.getMeta("server_url") ?: "")
        nameInput.setText(db.getMeta("user") ?: "")
        refreshState()
        refreshPermissions()
    }

    override fun onResume() {
        super.onResume()
        refreshState()
        refreshPermissions()
    }

    // ---------- 界面 ----------

    private fun buildUi(): LinearLayout {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), statusBarHeight() + dp(10), dp(20), dp(28))
        }

        val titleRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        titleRow.addView(TextView(this).apply {
            text = "设置"
            textSize = 22f
            typeface = Ui.display
            setTextColor(Ui.INK)
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        titleRow.addView(pill("返回", small = true).apply { setOnClickListener { finish() } })
        root.addView(titleRow)

        stateView = TextView(this).apply {
            textSize = 13f
            typeface = Ui.body
            setTextColor(Ui.MUTED)
            setPadding(0, dp(8), 0, dp(2))
        }
        root.addView(stateView)

        root.addView(label("服务器地址"))
        urlInput = input("如 192.168.1.5:5080", InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI)
        root.addView(urlInput)

        root.addView(label("用户名"))
        nameInput = input("用户名", InputType.TYPE_CLASS_TEXT)
        root.addView(nameInput)

        root.addView(label("密码"))
        pwdInput = input("密码", InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD)
        root.addView(pwdInput)

        loginSlot = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        root.addView(loginSlot, fullWidth(topMargin = 16))

        root.addView(pageRule(topMargin = 26))

        root.addView(label("提醒权限"))
        notifState = permissionRow(root, "通知") {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
                checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
            ) {
                requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), REQ_NOTIFICATION)
            } else {
                Permissions.openNotificationSettings(this)
            }
        }
        exactState = permissionRow(root, "精确闹钟") { Permissions.requestExactAlarm(this) }
        batteryState = permissionRow(root, "电池白名单") { Permissions.requestIgnoreBattery(this) }
        fullScreenState = permissionRow(root, "锁屏全屏提醒") { Permissions.requestFullScreenIntent(this) }

        root.addView(pageRule(topMargin = 20))

        root.addView(label("系统日历"))
        calendarState = permissionRow(root, "写入日历", "选择") { pickCalendar() }
        root.addView(pill("从系统日历读入任务").apply {
            setOnClickListener { readCalendar() }
        }, fullWidth(topMargin = 6))

        root.addView(TextView(this).apply {
            text = "地址与登录凭证只存在这台手机上，不会上传。任务数据在本机有一份完整副本，断网也能用。"
            textSize = 12f
            typeface = Ui.body
            setTextColor(Ui.MUTED)
            setLineSpacing(dp(3).toFloat(), 1f)
            setPadding(0, dp(24), 0, 0)
        })

        return LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Ui.PAPER)
            addView(ScrollView(this@SettingsActivity).apply {
                isFillViewport = true
                setBackgroundColor(Ui.PAPER)
                addView(root)
            })
        }
    }

    private fun label(text: String) = TextView(this).apply {
        this.text = text
        textSize = 12f
        typeface = Ui.medium
        setTextColor(Ui.MUTED)
        setPadding(0, dp(14), 0, dp(6))
    }

    private fun input(hint: String, inputType: Int) = EditText(this).apply {
        this.hint = hint
        textSize = 14f
        typeface = Ui.body
        setInputType(inputType)
        setSingleLine(true)
        setPadding(dp(14), dp(11), dp(14), dp(11))
        minimumHeight = dp(44)
        background = GradientDrawable().apply {
            cornerRadius = dp(10).toFloat()
            setColor(Ui.PAPER_2)
            setStroke(dp(1), Ui.RULE)
        }
        setTextColor(
            ColorStateList(
                arrayOf(intArrayOf(-android.R.attr.state_enabled), intArrayOf()),
                intArrayOf(Ui.MUTED, Ui.INK),
            )
        )
        setHintTextColor(Ui.MUTED)
    }

    private fun permissionRow(
        parent: LinearLayout,
        name: String,
        buttonText: String = "去开启",
        onOpen: () -> Unit,
    ): TextView {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, dp(6), 0, dp(6))
        }
        row.addView(TextView(this).apply {
            text = name
            textSize = 14f
            typeface = Ui.body
            setTextColor(Ui.INK_2)
        }, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        val state = TextView(this).apply {
            text = "检查中"
            textSize = 12f
            typeface = Ui.body
            setTextColor(Ui.MUTED)
            setPadding(0, 0, dp(12), 0)
        }
        row.addView(state)
        val button = pill(buttonText, small = true).apply { setOnClickListener { onOpen() } }
        row.addView(button)
        permButtons[state] = button
        parent.addView(row)
        return state
    }

    private fun fullWidth(topMargin: Int = 0) = LinearLayout.LayoutParams(
        ViewGroup.LayoutParams.MATCH_PARENT,
        ViewGroup.LayoutParams.WRAP_CONTENT
    ).apply { this.topMargin = dp(topMargin) }

    // ---------- 状态 ----------

    private fun refreshState() {
        val logged = db.getMeta("token") != null
        nameInput.isEnabled = !logged
        pwdInput.isEnabled = !logged
        urlInput.isEnabled = !logged
        stateView.text = if (logged) {
            "已登入：" + (db.getMeta("user") ?: "")
        } else {
            "未登入 · 填好地址和账号后点「登入」"
        }

        loginSlot.removeAllViews()
        loginBtn = pill(if (logged) "登出" else "登入", primary = !logged).apply {
            setOnClickListener { if (db.getMeta("token") == null) doLogin() else doLogout() }
        }
        loginSlot.addView(
            loginBtn,
            LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            )
        )
    }

    private fun refreshPermissions() {
        setPerm(notifState, Permissions.notificationsEnabled(this))
        setPerm(exactState, Permissions.canExactAlarm(this))
        setPerm(batteryState, Permissions.ignoringBatteryOptimizations(this))
        setPerm(fullScreenState, Permissions.canFullScreenIntent(this))
        val calendarAllowed = CalendarWriter.hasPermission(this)
        val calendarName = db.getMeta("calendar_id")?.let { id ->
            CalendarWriter.writableCalendars(this).firstOrNull { it.id.toString() == id }?.name
        }?.let { name ->
            // 小米日历把名字存成资源键（calendar_displayname_xiaomi），直出很难看
            if (name.startsWith("calendar_displayname")) "系统日历" else name
        }
        calendarState.text = when {
            !calendarAllowed -> "未开启"
            calendarName != null -> "已开启 · $calendarName"
            else -> "已开启"
        }
        calendarState.setTextColor(if (calendarAllowed) Ui.INK_2 else Ui.MUTED)
    }

    /** 权限状态与右侧按钮文案联动：已开就是「查看」，没开才是「去开启」。 */
    private fun setPerm(state: TextView, granted: Boolean) {
        state.text = if (granted) "已开启" else "未开启"
        state.setTextColor(if (granted) Ui.INK_2 else Ui.MUTED)
        permButtons[state]?.text = if (granted) "查看" else "去开启"
    }

    // ---------- 动作 ----------

    private fun doLogin() {
        val base = Api.normalize(urlInput.text.toString())
        val name = nameInput.text.toString().trim()
        val password = pwdInput.text.toString()
        if (base.isEmpty() || name.isEmpty() || password.isEmpty()) {
            toast("地址、用户名、密码都要填")
            return
        }
        stateView.text = "登录中…"
        Thread {
            val message = try {
                val token = Api.login(base, name, password)
                db.setMeta("server_url", base)
                db.setMeta("user", name)
                db.setMeta("token", token)
                "登入成功，回到首页就能同步"
            } catch (e: Exception) {
                "失败：" + (e.message ?: e.javaClass.simpleName)
            }
            runOnUiThread {
                pwdInput.setText("")
                stateView.text = message
                refreshState()
                toast(message)
            }
        }.start()
    }

    private fun doLogout() {
        db.setMeta("token", null)
        db.setMeta("user", null)
        pwdInput.setText("")
        refreshState()
        toast("已登出，本地数据保留")
    }

    private fun readCalendar() {
        if (checkSelfPermission(Manifest.permission.READ_CALENDAR) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.READ_CALENDAR), REQ_CALENDAR)
            return
        }
        stateView.text = "读取日历…"
        Thread {
            val message = try {
                val events = SystemCalendar.read(this)
                val changed = SystemCalendar.importInto(db, events)
                ReminderScheduler.rescheduleAll(applicationContext)
                "读到 ${events.size} 个日程，其中 $changed 条有变化"
            } catch (e: Exception) {
                "失败：" + (e.message ?: e.javaClass.simpleName)
            }
            runOnUiThread {
                stateView.text = message
                toast(message)
            }
        }.start()
    }

    /** 选一个可写日历并立即把已同步的任务写进去。 */
    private fun pickCalendar() {
        if (checkSelfPermission(Manifest.permission.WRITE_CALENDAR) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.WRITE_CALENDAR), REQ_CALENDAR_WRITE)
            return
        }
        val list = CalendarWriter.writableCalendars(this)
        if (list.isEmpty()) {
            toast("手机里没有可写的日历")
            return
        }
        AlertDialog.Builder(this)
            .setTitle("写入哪个日历")
            .setItems(list.map { it.name }.toTypedArray()) { _, which ->
                db.setMeta("calendar_id", list[which].id.toString())
                writeCalendar()
            }
            .show()
    }

    private fun writeCalendar() {
        stateView.text = "写入日历…"
        Thread {
            val message = try {
                CalendarWriter.sync(this, db).describe()
            } catch (e: Exception) {
                "失败：" + (e.message ?: e.javaClass.simpleName)
            }
            runOnUiThread {
                stateView.text = message
                toast(message)
                refreshPermissions()
            }
        }.start()
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray,
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        val granted = grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED
        when (requestCode) {
            REQ_NOTIFICATION -> {
                refreshPermissions()
                if (!granted) toast("没有通知权限，提醒不会弹")
            }
            REQ_CALENDAR -> {
                if (granted) readCalendar() else toast("没有日历权限")
            }
            REQ_CALENDAR_WRITE -> {
                if (granted) pickCalendar() else toast("没有日历写入权限")
            }
        }
    }

    private fun toast(text: String) = Toast.makeText(this, text, Toast.LENGTH_SHORT).show()

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    @Suppress("DEPRECATION")
    private fun statusBarHeight(): Int {
        val id = resources.getIdentifier("status_bar_height", "dimen", "android")
        return if (id > 0) resources.getDimensionPixelSize(id) else dp(24)
    }

    private companion object {
        const val REQ_NOTIFICATION = 1
        const val REQ_CALENDAR = 2
        const val REQ_CALENDAR_WRITE = 3
    }
}
