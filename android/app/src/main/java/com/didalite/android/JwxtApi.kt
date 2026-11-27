package com.didalite.android

import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

/**
 * 强智教务直连（网页端 /jsxsd/ 那条路）。
 *
 * 为什么不用 app.do：广财的教务没开放强智移动端 app.do 接口，请求会直接返回「出错页面」。
 * 实测可用的是网页端登录表单：
 *   GET  /jsxsd/                    拿初始 Cookie
 *   GET  /jsxsd/verifycode.servlet  验证码图片（必须人工看图输入）
 *   POST /jsxsd/xk/LoginToXkLdap    USERNAME / PASSWORD / RANDOMCODE
 *   GET  /jsxsd/framework/xsMain.jsp 校验登录态
 *   POST /jsxsd/xskb/xskb_list.do   xnxq01id=学期  → 含 #kbtable 的课表 HTML
 *
 * 密码只在内存里用一次，不落盘。
 */
object JwxtApi {

    const val DEFAULT_BASE = "http://jwxt.gdufe.edu.cn"

    class JwxtException(message: String) : Exception(message)

    /** 一次教务会话：手动管 Cookie，不依赖全局 CookieHandler。 */
    class Session {
        internal val cookies = LinkedHashMap<String, String>()

        internal fun header(): String =
            cookies.entries.joinToString("; ") { "${it.key}=${it.value}" }

        internal fun absorb(conn: HttpURLConnection) {
            conn.headerFields.forEach { (key, values) ->
                if (key == null || !key.equals("Set-Cookie", ignoreCase = true)) return@forEach
                values.forEach { raw ->
                    val pair = raw.substringBefore(';')
                    val idx = pair.indexOf('=')
                    if (idx > 0) cookies[pair.substring(0, idx).trim()] = pair.substring(idx + 1).trim()
                }
            }
        }
    }

    /** 抓验证码图片。调用方必须把图显示给用户看，让用户填。 */
    fun captcha(base: String, session: Session): ByteArray {
        val conn = open("$base/jsxsd/verifycode.servlet?t=${System.currentTimeMillis()}", session)
        return try {
            val code = conn.responseCode
            // 验证码与当前会话绑定：servlet 会回 Set-Cookie，必须收进 session，
            // 否则登录时缺这个 cookie，服务端永远判「验证码错误」，一直循环。
            session.absorb(conn)
            val bytes = (if (code in 200..299) conn.inputStream else conn.errorStream)
                ?.use { it.readBytes() } ?: ByteArray(0)
            if (code !in 200..299 || bytes.isEmpty()) {
                throw JwxtException("取验证码失败：HTTP $code")
            }
            bytes
        } catch (e: JwxtException) {
            throw e
        } catch (e: IOException) {
            throw JwxtException("取验证码失败：${netMessage(e)}")
        } finally {
            conn.disconnect()
        }
    }

    /** 表单登录。成功后 session 里已带上登录 Cookie。 */
    fun login(base: String, session: Session, user: String, pwd: String, code: String) {
        val body = listOf(
            "USERNAME" to user,
            "PASSWORD" to pwd,
            "RANDOMCODE" to code,
        ).joinToString("&") { "${enc(it.first)}=${enc(it.second)}" }

        val text = postForm("$base/jsxsd/xk/LoginToXkLdap", body, session)
        for (bad in listOf("验证码错误", "验证码不正确", "密码错误", "用户名或密码", "用户名不能为空")) {
            if (text.contains(bad)) throw JwxtException(bad)
        }

        // 用主页校验登录态：登录失败会退回登录页（含「用户登录」和验证码框）
        val main = getText("$base/jsxsd/framework/xsMain.jsp", session)
        if (main.contains("verifycode.servlet") || main.contains("用户登录")) {
            throw JwxtException("登录没成功：验证码或密码不对，重试一次")
        }
    }

    /** 拉课表并整理成可读文本。term 形如 2026-2027-1。 */
    fun schedule(base: String, session: Session, term: String): String {
        val body = "xnxq01id=${enc(term)}&zc="
        val html = postForm("$base/jsxsd/xskb/xskb_list.do", body, session)
        if (html.contains("verifycode.servlet")) {
            throw JwxtException("登录态已失效，请重新导入")
        }
        val table = extractTable(html)
            ?: throw JwxtException("教务没返回课表，检查学期是否填对（如 2026-2027-1）")
        val text = ScheduleText.polish(renderTable(table))
        if (text.isBlank()) throw JwxtException("这一学期还没有排课")
        return text
    }

    // ---------- HTML 处理 ----------

    /** 截出 id="kbtable" 的那张表。 */
    private fun extractTable(html: String): String? {
        val key = Regex("id\\s*=\\s*[\"']?kbtable", RegexOption.IGNORE_CASE).find(html) ?: return null
        val start = html.lastIndexOf("<table", key.range.first, ignoreCase = true)
        if (start < 0) return null
        val end = html.indexOf("</table>", key.range.first)
        if (end < 0) return null
        return html.substring(start, end)
    }

    /**
     * 把课表表格转成按行可读的文本。
     * 第一列是节次（如「1-2节」），后面各列对应星期几，表头里认「星期一…星期日」。
     */
    private fun renderTable(table: String): String {
        val rows = Regex("<tr[^>]*>(.*?)</tr>", setOf(RegexOption.DOT_MATCHES_ALL, RegexOption.IGNORE_CASE))
            .findAll(table).map { it.groupValues[1] }.toList()
        if (rows.isEmpty()) return ""

        val cellsOf = { row: String ->
            Regex("<t[dh][^>]*>(.*?)</t[dh]>", setOf(RegexOption.DOT_MATCHES_ALL, RegexOption.IGNORE_CASE))
                .findAll(row).map { clean(it.groupValues[1]) }.toList()
        }

        // 表头 → 列号到星期几的映射
        val weekChars = charArrayOf('一', '二', '三', '四', '五', '六', '日')
        val colToDay = HashMap<Int, Int>()
        for (r in rows.take(3)) {
            val cells = cellsOf(r)
            cells.forEachIndexed { idx, c ->
                for (n in 1..7) {
                    if (c.contains("星期${weekChars[n - 1]}") || c.contains("周${weekChars[n - 1]}")) {
                        colToDay[idx] = n
                        break
                    }
                }
            }
            if (colToDay.size >= 5) break
        }

        val out = mutableListOf<String>()
        rows.drop(1).forEach { row ->
            val cells = cellsOf(row)
            if (cells.isEmpty()) return@forEach
            val head = cells.first().replace("\n", " ").trim()
            val jie = Regex("(\\d{1,2})\\s*[-~]\\s*(\\d{1,2})").find(head)?.let {
                "${it.groupValues[1]}-${it.groupValues[2]} 节"
            } ?: head.take(8)
            cells.forEachIndexed { idx, cell ->
                if (idx == 0 || cell.isBlank() || cell == "无" || cell == "-") return@forEachIndexed
                val day = colToDay[idx] ?: idx
                if (day !in 1..7) return@forEachIndexed
                out += "周${weekChars[day - 1]} $jie  ${cell.replace("\n", " · ")}"
            }
        }
        return out.joinToString("\n")
    }

    /** 去标签、解实体、压空白。 */
    private fun clean(raw: String): String = raw
        .replace(Regex("<br\\s*/?>", RegexOption.IGNORE_CASE), "\n")
        .replace(Regex("<[^>]+>"), "")
        .replace("&nbsp;", " ")
        .replace("&amp;", "&")
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&quot;", "\"")
        .replace(Regex("[ \\t\\u00a0]+"), " ")
        .replace(Regex("\\n\\s*\\n+"), "\n")
        .trim()

    // ---------- HTTP ----------

    private fun open(url: String, session: Session, method: String = "GET"): HttpURLConnection =
        (URL(url).openConnection() as HttpURLConnection).apply {
            requestMethod = method
            connectTimeout = 8000
            readTimeout = 15000
            instanceFollowRedirects = true
            setRequestProperty("User-Agent", "Mozilla/5.0 (Linux; Android 14) DidaLite/0.2")
            setRequestProperty("Accept-Language", "zh-CN,zh;q=0.9")
            val cookie = session.header()
            if (cookie.isNotEmpty()) setRequestProperty("Cookie", cookie)
        }

    private fun getText(url: String, session: Session): String {
        val conn = open(url, session)
        return try {
            val code = conn.responseCode
            session.absorb(conn)
            val bytes = (if (code in 200..299) conn.inputStream else conn.errorStream)
                ?.use { it.readBytes() } ?: ByteArray(0)
            if (code !in 200..299) throw JwxtException("教务返回 HTTP $code")
            decode(bytes)
        } catch (e: JwxtException) {
            throw e
        } catch (e: IOException) {
            throw JwxtException(netMessage(e))
        } finally {
            conn.disconnect()
        }
    }

    private fun postForm(url: String, body: String, session: Session): String {
        val conn = open(url, session, "POST")
        return try {
            conn.doOutput = true
            conn.setRequestProperty("Content-Type", "application/x-www-form-urlencoded")
            conn.setRequestProperty("Referer", url.substringBeforeLast('/'))
            conn.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
            val code = conn.responseCode
            session.absorb(conn)
            val bytes = (if (code in 200..299) conn.inputStream else conn.errorStream)
                ?.use { it.readBytes() } ?: ByteArray(0)
            if (code !in 200..299) throw JwxtException("教务返回 HTTP $code")
            decode(bytes)
        } catch (e: JwxtException) {
            throw e
        } catch (e: IOException) {
            throw JwxtException(netMessage(e))
        } finally {
            conn.disconnect()
        }
    }

    /** 教务页面多数是 UTF-8，个别老页面是 GB18030，先试 UTF-8 看有没有乱码。 */
    private fun decode(bytes: ByteArray): String {
        val utf8 = String(bytes, Charsets.UTF_8)
        if (utf8.contains('\uFFFD')) {
            return runCatching { String(bytes, charset("GB18030")) }.getOrDefault(utf8)
        }
        return utf8
    }

    private fun netMessage(e: IOException): String = when (e) {
        is java.net.SocketTimeoutException -> "连接教务系统超时：校外请先连学校 VPN"
        is java.net.UnknownHostException -> "找不到教务服务器：地址不对或 DNS 不通"
        else -> "连不上教务系统：${e.message ?: e.javaClass.simpleName}"
    }

    private fun enc(s: String): String = URLEncoder.encode(s, "UTF-8")
}
