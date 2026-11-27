package com.didalite.android

import org.json.JSONArray
import org.json.JSONObject

/**
 * 第二课堂成绩单（校团委 2ketang）。
 *
 * 前端是 uni-app 的单页应用，页面壳里没有数据，全是 JS 起来后打 /apps 下的接口：
 *   GET  /apps/school?para={"url":"<域名>"}        → 学校 id
 *   POST /apps/common/login  body: para=<json>      → 响应头 x-token 是登录态
 *   GET  /apps/credit/plan/progress                 → 每类已获学分 / 要求 / 百分比
 *
 * 域名解析在校园网内网段，**校外必须先连学校 VPN**。账号是学号，密码初始为「学号@abc」。
 */
object EduSecondClass {

    const val DEFAULT_BASE = "http://2ketang.gdufe.edu.cn"
    private const val HOST = "2ketang.gdufe.edu.cn"

    /** 一类第二课堂学分。[need] 是毕业要求，[level] 是完成百分比。 */
    data class Item(
        val name: String,
        val category: String,
        val credit: Double,
        val need: Double,
        val level: String,
    )

    data class Report(
        val items: List<Item>,
        val fetchedAt: Long,
        val percent: Double,
    ) {
        val total: Double get() = items.sumOf { it.credit }
    }

    /**
     * 拉第二课堂学分。[base] 形如 http://2ketang.gdufe.edu.cn。
     * 密码只在内存里用一次，不落盘。
     */
    fun fetch(base: String, user: String, pwd: String): Report {
        val root = base.trim().trimEnd('/').ifBlank { DEFAULT_BASE }
        val api = "$root/apps"
        val session = EduHttp.session()

        val schoolId = try {
            val reply = EduHttp.get(session, "$api/school?para=${EduHttp.enc("""{"url":"$HOST"}""")}")
            JSONObject(reply.text).getJSONArray("data").getJSONObject(0).optString("id")
        } catch (e: Exception) {
            throw EduException("连不上第二课堂：校外请先连学校 VPN（${e.message ?: e.javaClass.simpleName}）")
        }
        if (schoolId.isBlank()) throw EduException("没认出这所学校，第二课堂接口可能改版了")

        val para = JSONObject()
            .put("school", schoolId)
            .put("account", user)
            .put("password", pwd)
            .toString()
        val login = EduHttp.post(session, "$api/common/login", EduHttp.form(listOf("para" to para)))
        val body = runCatching { JSONObject(login.text) }.getOrNull()
            ?: throw EduException("第二课堂返回的不是 JSON，接口可能改版了")
        val data = body.optJSONObject("data")
        if (data == null || data.has("detail") || !data.has("id")) {
            throw EduException(body.optString("msg").ifBlank { "登录失败：账号或密码不对" })
        }
        val token = login.token
        if (token.isNullOrBlank()) throw EduException("登录没有返回凭证，接口可能改版了")

        val progress = EduHttp.get(session, "$api/credit/plan/progress", token)
        val plan = runCatching { JSONObject(progress.text).getJSONObject("data") }.getOrNull()
        val details = plan?.optJSONArray("details")
        val items = when {
            details != null && details.length() > 0 -> itemsOf(details)
            else -> itemsOf(eduHttpClassify(api, session, token, data.optString("id")))
        }
        if (items.isEmpty()) throw EduException("登录成功，但没拿到学分数据")
        return Report(items, System.currentTimeMillis(), plan?.optDouble("overallPercent", 0.0) ?: 0.0)
    }

    /** 学分计划没启用时退回按类别汇总的接口。 */
    private fun eduHttpClassify(api: String, session: JwxtApi.Session, token: String, userId: String): JSONArray {
        val para = EduHttp.enc(JSONObject().put("userId", userId).toString())
        val reply = EduHttp.get(session, "$api/user/achievement/by-classify-list?para=$para", token)
        return JSONObject(reply.text).optJSONArray("data") ?: JSONArray()
    }

    private fun itemsOf(details: JSONArray): List<Item> = (0 until details.length()).mapNotNull { i ->
        val row = details.optJSONObject(i) ?: return@mapNotNull null
        val name = row.optString("classifyName").ifBlank { return@mapNotNull null }
        val current = row.optDouble("current", row.optDouble("classifyHours", 0.0))
        val need = row.optDouble("totalExpected", row.optDouble("classifySchoolMinHours", 0.0))
        val percent = row.optDouble("percent", if (need > 0) current / need * 100 else 0.0)
        Item(name, "", current, need, "${percent.toInt()}%")
    }

}
