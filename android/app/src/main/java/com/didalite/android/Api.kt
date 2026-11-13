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
)

/**
 * 同步 API 客户端，契约完全对齐 PC 端 SyncServer：
 * POST /login {name,password} → {token,name}；GET /sync?since= ；POST /sync {since,changes}。
 * 鉴权走 X-Token 头。
 */
object Api {

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
    fun pull(base: String, token: String?, since: Long): SyncPayload =
        parse(request("$base/sync?since=$since", "GET", null, token))

    /** 推本地改动，服务端同时带回 since 之后的变更。闹钟改动单独放在 alarm_changes 里。 */
    fun push(
        base: String,
        token: String?,
        since: Long,
        changes: List<Task>,
        alarmOps: List<AlarmOp> = emptyList(),
    ): SyncPayload {
        val array = JSONArray()
        changes.forEach { array.put(it.toJson()) }
        val body = JSONObject().put("since", since).put("changes", array)
        if (alarmOps.isNotEmpty()) {
            val ops = JSONArray()
            alarmOps.forEach { ops.put(it.toJson()) }
            body.put("alarm_changes", ops)
        }
        return parse(request("$base/sync", "POST", body, token))
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

        return SyncPayload(o.optLong("max_rev"), accepted, rejected, tasks, alarms)
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
