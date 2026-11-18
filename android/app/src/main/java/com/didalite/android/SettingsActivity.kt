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
    private lateinit var diagView: TextView
    private lateinit var notifState: TextView
    private lateinit var exactState: TextView
    private lateinit var batteryState: TextView
    private lateinit var fullScreenState: TextView
    private lateinit var calendarState: TextView
    private val permButtons = HashMap<TextView, TextView>()

    /** 从首页因「登录失效」跳进来：账号已保留，只需要补密码。 */
    private var reauth = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        db = Db(this)
        reauth = intent?.getBooleanExtra("reauth", false) == true
        setContentView(buildUi())
        applyDidaTheme()

        urlInput.setText(db.getMeta("server_url") ?: Api.DefaultServerUrl)
        nameInput.setText(db.getMeta("user") ?: "")
        refreshState()
        refreshPermissions()

        // 失效补密码：光标直接落到密码框，省一次点击
        if (reauth) {
            pwdInput.requestFocus()
            pwdInput.post {
                val imm = getSystemService(INPUT_METHOD_SERVICE) as android.view.inputmethod.InputMethodManager
                imm.showSoftInput(pwdInput, android.view.inputmethod.InputMethodManager.SHOW_IMPLICIT)
            }
        }
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
        urlInput = input("已预填服务器，也可改成 192.168.1.5:5080", InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI)
        root.addView(urlInput)

        root.addView(label("用户名"))
        nameInput = input("用户名", InputType.TYPE_CLASS_TEXT)
        root.addView(nameInput)

        root.addView(label("密码"))
        pwdInput = input("密码", InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD)
        root.addView(pwdInput)

        loginSlot = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        root.addView(loginSlot, fullWidth(topMargin = 16))

        root.addView(pill("检查连接", small = true).apply {
            setOnClickListener { runCheck() }
        }, fullWidth(topMargin = 10))

        diagView = TextView(this).apply {
            textSize = 12f
            typeface = Ui.body
            setTextColor(Ui.MUTED)
            setLineSpacing(dp(3).toFloat(), 1f)
            setPadding(0, dp(10), 0, 0)
        }
        root.addView(diagView)

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
        val known = db.getMeta("user") ?: ""
        nameInput.isEnabled = !logged
        pwdInput.isEnabled = !logged
        urlInput.isEnabled = !logged
        stateView.text = when {
            logged -> "已登入：" + known
            known.isNotEmpty() -> "未登入：账号 $known 已保留，输入密码就能重新登入"
            else -> "未登入 · 填好地址和账号后点「登入」"
        }
        stateView.setTextColor(if (logged) Ui.MUTED else if (known.isNotEmpty()) Ui.WARN else Ui.MUTED)

        loginSlot.removeAllViews()
        val label = when {
            logged -> "登出"
            known.isNotEmpty() -> "恢复登入"
            else -> "登入"
        }
        loginBtn = pill(label, primary = !logged).apply {
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

        // 换账号 = 换一份数据：旧账号的任务会原样推给新账号，必须先清干净。
        // 清空不可撤销，所以先问过用户再动手。
        val previousUser = db.getMeta("user") ?: ""
        val switching = previousUser.isNotEmpty() && !previousUser.equals(name, ignoreCase = true)
        val localCount = db.all().size + db.notes().size
        if (switching && localCount > 0) {
            AlertDialog.Builder(this)
                .setTitle("换成另一个账号？")
                .setMessage(
                    "账号从「$previousUser」换成「$name」。\n\n" +
                        "这台手机上的 $localCount 条任务和便签会先清空，再从新账号导入云端数据。\n" +
                        "本地闹钟、课表和提醒不受影响。此操作不可撤销。"
                )
                .setNegativeButton("取消", null)
                .setPositiveButton("清空并换") { _, _ -> doLoginNow(base, name, password, true) }
                .show()
            return
        }

        doLoginNow(base, name, password, switching)
    }

    private fun doLoginNow(base: String, name: String, password: String, clearLocal: Boolean) {
        stateView.text = "登录中…"
        Thread {
            val message = try {
                val token = Api.login(base, name, password)
                // 先登录成功再清本地：登录失败时本地一个字节都不动
                if (clearLocal) db.clearSyncedData()
                db.setMeta("server_url", base)
                db.setMeta("user", name)
                db.setMeta("token", token)
                reauth = false
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
        // 只作废 token：账号和地址留着，下次登入不用重填用户名
        db.setMeta("token", null)
        pwdInput.setText("")
        refreshState()
        toast("已登出，账号已保留，下次输密码即可")
    }

    /**
     * 连接自检：一次请求把「网络不通」「登录失效」「一切正常」分开。
     * 这三种在同步结果里长得一模一样，混在一起就会一直误以为是掉线——
     * 其实校园网挡 443 端口和服务器不认凭据完全是两回事。
     */
    private fun runCheck() {
        val base = Api.normalize(urlInput.text.toString())
        if (base.isEmpty()) {
            showDiag("地址是空的，先填服务器地址", Ui.WARN)
            return
        }
        val token = db.getMeta("token")
        diagView.text = "正在检查…"
        Thread {
            val result = Api.checkConnection(base, token)
            runOnUiThread {
                when (result) {
                    is Api.CheckResult.Ok -> showDiag(
                        "一切正常：连上了，登录有效，云端有 ${result.taskCount} 条任务\n$base",
                        Ui.INK_2
                    )
                    is Api.CheckResult.Unauthorized -> showDiag(result.message, Ui.WARN)
                    is Api.CheckResult.Unreachable -> showDiag(result.message, Ui.WARN)
                    is Api.CheckResult.ServerError -> showDiag("服务端报错：${result.message}", Ui.WARN)
                }
            }
        }.start()
    }

    private fun showDiag(text: String, color: Int) {
        diagView.text = text
        diagView.setTextColor(color)
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
                // 顺带核对日历里被删掉的事件，把对应任务标成删除，等同步推给电脑端
                val cal = CalendarSync.scan(applicationContext, db)
                ReminderScheduler.rescheduleAll(applicationContext)
                val tail = when {
                    cal.removed > 0 -> "，日历里删掉的 ${cal.removed} 条已标记删除"
                    cal.note != null -> "（${cal.note}）"
                    else -> ""
                }
                "读到 ${events.size} 个日程，其中 $changed 条有变化$tail"
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
        // MIUI/HyperOS 的授权落库是异步的：授权弹窗刚确认完，provider 可能还没把权限算数，
        // 立刻查询会撞 SecurityException 闪退。捕获后提示再点一次，不崩。
        val list = try {
            CalendarWriter.writableCalendars(this)
        } catch (e: SecurityException) {
            toast("日历权限刚授权还没生效，请稍等一下再点一次「写入日历」")
            return
        }
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
