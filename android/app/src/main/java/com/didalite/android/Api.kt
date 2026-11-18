package com.didalite.android

import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

/** 服务端返回非 2xx 时抛出，带 HTTP 状态码。 */
class ApiException(val code: Int, message: String) : Exception(message)

/** 一轮同步的服务端返回。null 字段会被服务端省略，这里统一按可选处理。 */
class SyncPayload(
    val maxRev: Long,
    val accepted: List<Pair<String, Long>>,
    val rejected: List<Task>,
    val tasks: List<Task>,
    /** 电脑端「闹钟」页的独立闹钟，整批下发，手机端只读。 */
    val alarms: List<Alarm>,
    /** 便签增量：note_since 之后被改过的条目，两边都能改。 */
    val notes: List<Note> = emptyList(),
    val noteMaxRev: Long = 0,
    /** 本轮被服务端接受的便签（含被高版本压过后重新编号的）。 */
    val noteAccepted: List<Pair<String, Long>> = emptyList(),
)

/**
 * 同步 API 客户端，契约完全对齐 PC 端 SyncServer：
 * POST /login {name,password} → {token,name}；GET /sync?since= ；POST /sync {since,changes}。
 * 鉴权走 X-Token 头。
 */
object Api {

    /**
     * 出厂预填的同步服务器。用户首次打开设置就已经填好，直接填账号密码登入即可；
     * 想换成自己电脑起的本地服务，把地址改成「192.168.x.x:5080」就行。
     */
    const val DefaultServerUrl = "https://huangyiwei.online/dida"

    /**
     * 用户可能填「192.168.1.5:5080」或带 /sync 的完整地址，统一收敛成基地址。
     * 中文输入法容易打出全角冒号和斜杠，这里一并换成半角，否则地址解析会失败。
     */
    fun normalize(input: String): String {
        var s = input.trim().replace('：', ':').replace('／', '/').trimEnd('/')
        if (s.isEmpty()) return s
        if (!s.startsWith("http://") && !s.startsWith("https://")) s = "http://$s"
        for (suffix in listOf("/sync", "/login")) if (s.endsWith(suffix)) s = s.removeSuffix(suffix)
        return s.trimEnd('/')
    }

    fun login(base: String, name: String, password: String): String {
        val body = JSONObject().put("name", name).put("password", password)
        return JSONObject(request("$base/login", "POST", body, null)).getString("token")
    }

    /** 只拉不推。 */
    fun pull(base: String, token: String?, since: Long, noteSince: Long = 0): SyncPayload =
        parse(request("$base/sync?since=$since&note_since=$noteSince", "GET", null, token))

    /** 推本地改动，服务端同时带回 since 之后的变更。闹钟改动单独放在 alarm_changes 里。 */
    fun push(
        base: String,
        token: String?,
        since: Long,
        changes: List<Task>,
        alarmOps: List<AlarmOp> = emptyList(),
        notes: List<Note> = emptyList(),
        noteSince: Long = 0,
    ): SyncPayload {
        val array = JSONArray()
        changes.forEach { array.put(it.toJson()) }
        val body = JSONObject().put("since", since).put("changes", array)
        if (alarmOps.isNotEmpty()) {
            val ops = JSONArray()
            alarmOps.forEach { ops.put(it.toJson()) }
            body.put("alarm_changes", ops)
        }
        // 便签独立 rev 空间：只推脏行，连同 note_since 一起上报
        if (notes.isNotEmpty()) {
            val items = JSONArray()
            notes.forEach { items.put(it.toJson()) }
            body.put("notes", items)
        }
        body.put("note_since", noteSince)
        return parse(request("$base/sync", "POST", body, token))
    }

    /**
     * 连接自检的结果。三种情况在界面上长得都像「同步失败」，但处理方式完全不同，
     * 所以必须一次就分清楚，别让用户自己猜是网络问题还是登录问题。
     */
    sealed class CheckResult {
        /** 通了，而且这枚 token 仍然有效。 */
        class Ok(val taskCount: Int) : CheckResult()

        /** 请求到了服务器，但服务器不认这枚凭据 —— 需要重新登入。 */
        class Unauthorized(val message: String) : CheckResult()

        /** 根本没连上：断网、地址写错、端口被运营商/校园网挡住。 */
        class Unreachable(val message: String) : CheckResult()

        /** 连上了，但服务端返回了别的错误。 */
        class ServerError(val code: Int, val message: String) : CheckResult()
    }

    /**
     * 连接自检：一次 GET 就能分清「网络不通」「登录失效」「一切正常」。
     * 典型误判场景——PC 端登录后手机点同步失败，其实是校园网挡了 443 端口，
     * 跟登录状态毫无关系；不区分清楚就会一直以为是「掉线」。
     */
    fun checkConnection(base: String, token: String?): CheckResult {
        val conn = (URL("$base/sync?since=0&note_since=0").openConnection() as HttpURLConnection).apply {
            requestMethod = "GET"
            connectTimeout = 8000
            readTimeout = 10000
            setRequestProperty("Accept", "application/json")
            if (!token.isNullOrEmpty()) setRequestProperty("X-Token", token)
        }
        return try {
            val code = conn.responseCode
            val text = (if (code in 200..299) conn.inputStream else conn.errorStream)
                ?.bufferedReader(Charsets.UTF_8)?.use { it.readText() } ?: ""
            when {
                code in 200..299 -> CheckResult.Ok(countTasks(text))
                code == 401 -> CheckResult.Unauthorized("登录已失效：服务器不认这枚凭据，点「恢复登入」换一枚")
                else -> CheckResult.ServerError(code, describeError(code, text))
            }
        } catch (e: java.net.UnknownHostException) {
            CheckResult.Unreachable("找不到服务器：地址写错了，或 DNS 不通")
        } catch (e: java.net.SocketTimeoutException) {
            CheckResult.Unreachable("连不上服务器：超时了。网络不通，或者运营商/校园网把这个端口挡了")
        } catch (e: java.net.ConnectException) {
            CheckResult.Unreachable("连不上服务器：连接被拒绝，地址或端口不对")
        } catch (e: Exception) {
            CheckResult.Unreachable("连不上服务器：${e.message ?: e.javaClass.simpleName}")
        } finally {
            conn.disconnect()
        }
    }

    /** 自检结果里顺带报一下云端有多少条任务，用来确认「连的是自己的服务器」。 */
    private fun countTasks(text: String): Int = try {
        JSONObject(text).optJSONArray("tasks")?.length() ?: 0
    } catch (_: Exception) {
        0
    }

    private fun parse(text: String): SyncPayload {
        val o = JSONObject(text)

        val accepted = mutableListOf<Pair<String, Long>>()
        o.optJSONArray("accepted")?.let { array ->
            for (i in 0 until array.length()) {
                val item = array.getJSONObject(i)
                accepted.add(item.getString("id") to item.optLong("rev"))
            }
        }

        // rejected 里的 server 就是服务端当前版本，直接拿来覆盖本地
        val rejected = mutableListOf<Task>()
        o.optJSONArray("rejected")?.let { array ->
            for (i in 0 until array.length()) {
                array.getJSONObject(i).optJSONObject("server")?.let { rejected.add(Task.fromJson(it)) }
            }
        }

        val tasks = mutableListOf<Task>()
        o.optJSONArray("tasks")?.let { array ->
            for (i in 0 until array.length()) tasks.add(Task.fromJson(array.getJSONObject(i)))
        }

        val alarms = mutableListOf<Alarm>()
        o.optJSONArray("alarms")?.let { array ->
            for (i in 0 until array.length()) alarms.add(Alarm.fromJson(array.getJSONObject(i)))
        }

        val notes = mutableListOf<Note>()
        o.optJSONArray("notes")?.let { array ->
            for (i in 0 until array.length()) notes.add(Note.fromJson(array.getJSONObject(i)))
        }

        val noteAccepted = mutableListOf<Pair<String, Long>>()
        o.optJSONArray("note_accepted")?.let { array ->
            for (i in 0 until array.length()) {
                val item = array.getJSONObject(i)
                noteAccepted.add(item.getString("id") to item.optLong("rev"))
            }
        }

        return SyncPayload(
            maxRev = o.optLong("max_rev"),
            accepted = accepted,
            rejected = rejected,
            tasks = tasks,
            alarms = alarms,
            notes = notes,
            noteMaxRev = o.optLong("note_max_rev"),
            noteAccepted = noteAccepted,
        )
    }

    private fun request(url: String, method: String, body: JSONObject?, token: String?): String {
        val conn = (URL(url).openConnection() as HttpURLConnection).apply {
            requestMethod = method
            connectTimeout = 8000
            readTimeout = 10000
            setRequestProperty("Accept", "application/json")
            if (!token.isNullOrEmpty()) setRequestProperty("X-Token", token)
        }
        try {
            if (body != null) {
                conn.doOutput = true
                conn.setRequestProperty("Content-Type", "application/json; charset=utf-8")
                conn.outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
            }
            val code = conn.responseCode
            val text = (if (code in 200..299) conn.inputStream else conn.errorStream)
                ?.bufferedReader(Charsets.UTF_8)?.use { it.readText() } ?: ""
            if (code !in 200..299) throw ApiException(code, describeError(code, text))
            return text
        } finally {
            conn.disconnect()
        }
    }

    private fun describeError(code: Int, body: String): String {
        val detail = try {
            JSONObject(body).optString("error")
        } catch (_: Exception) {
            ""
        }
        return when {
            code == 401 && detail == "用户名或密码错误" -> "用户名或密码错误"
            code == 401 -> "登录已失效，请重新登入"
            detail.isNotEmpty() -> "HTTP $code：$detail"
            else -> "HTTP $code"
        }
    }
}
