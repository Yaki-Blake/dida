package com.didalite.android

import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

/** 学业接口（学分绩点 / 第二课堂）抛出来的异常：message 直接给用户看。 */
class EduException(message: String) : Exception(message)

/**
 * 学业接口共用的 HTTP 小件。
 *
 * Cookie 直接复用 [JwxtApi.Session]——它就是一张「名字 → 值」的表：
 * 学分绩点与教务同域，可以用同一个实例带上登录态；第二课堂是另一套系统，
 * 各 new 一个实例即可，互不串。
 *
 * 依然不引第三方网络库：两个接口加起来只有 GET/POST 表单，HttpURLConnection 够用。
 */
internal object EduHttp {

    const val UA = "Mozilla/5.0 (Linux; Android 14) DidaLite/0.2"
    const val FORM = "application/x-www-form-urlencoded"

    private const val CONNECT_TIMEOUT = 8000
    private const val READ_TIMEOUT = 15000

    /** 一次响应：状态码 + 已解码正文。第二课堂把登录态放在响应头 x-token 里。 */
    class Reply(val code: Int, val text: String, val token: String? = null) {
        val ok: Boolean get() = code in 200..299
    }

    fun session(): JwxtApi.Session = JwxtApi.Session()

    fun get(session: JwxtApi.Session, url: String, token: String? = null): Reply =
        request(session, url, "GET", null, null, token)

    fun post(
        session: JwxtApi.Session,
        url: String,
        body: String,
        contentType: String = FORM,
        token: String? = null,
    ): Reply = request(session, url, "POST", body, contentType, token)

    /**
     * 发一次请求。非 2xx 不抛异常，交给调用方按状态码决定（第二课堂要探 404）；
     * 只有网络层失败（超时、DNS、连不上）才抛 [EduException]。
     */
    fun request(
        session: JwxtApi.Session,
        url: String,
        method: String,
        body: String?,
        contentType: String?,
        token: String? = null,
    ): Reply {
        val conn = (URL(url).openConnection() as HttpURLConnection).apply {
            requestMethod = method
            connectTimeout = CONNECT_TIMEOUT
            readTimeout = READ_TIMEOUT
            instanceFollowRedirects = true
            setRequestProperty("User-Agent", UA)
            setRequestProperty("Accept-Language", "zh-CN,zh;q=0.9")
            setRequestProperty("Accept", "text/html,application/json,*/*")
            if (token != null) setRequestProperty("x-token", token)
            val cookie = session.header()
            if (cookie.isNotEmpty()) setRequestProperty("Cookie", cookie)
        }
        return try {
            if (body != null) {
                conn.doOutput = true
                conn.setRequestProperty("Content-Type", contentType ?: FORM)
                conn.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
            }
            val code = conn.responseCode
            session.absorb(conn)
            val token = conn.getHeaderField("x-token")
            val bytes = (if (code in 200..299) conn.inputStream else conn.errorStream)
                ?.use { it.readBytes() } ?: ByteArray(0)
            Reply(code, decode(bytes), token)
        } catch (e: IOException) {
            throw EduException(netMessage(e))
        } finally {
            conn.disconnect()
        }
    }

    fun enc(value: String): String = URLEncoder.encode(value, "UTF-8")

    fun form(pairs: List<Pair<String, String>>): String =
        pairs.joinToString("&") { "${enc(it.first)}=${enc(it.second)}" }

    /** 教务与第二课堂都有 UTF-8 页面，个别老页面是 GB18030：先试 UTF-8，见乱码再退。 */
    fun decode(bytes: ByteArray): String {
        val utf8 = String(bytes, Charsets.UTF_8)
        if (utf8.contains('\uFFFD')) {
            return runCatching { String(bytes, charset("GB18030")) }.getOrDefault(utf8)
        }
        return utf8
    }

    private fun netMessage(e: IOException): String = when (e) {
        is java.net.SocketTimeoutException -> "连接超时：校外请先连学校 VPN，或换个网络再试"
        is java.net.UnknownHostException -> "找不到服务器：地址不对或 DNS 不通"
        else -> "连不上服务器：${e.message ?: e.javaClass.simpleName}"
    }
}