package com.didalite.android

/**
 * 学分绩点：走强智教务网页端的成绩查询页。
 *
 * 复用课表那趟表单登录拿到的 Cookie，之后：
 *   GET  /jsxsd/kscj/cjcx_query          进查询页，让服务端记住「这次要查全部」
 *   POST /jsxsd/kscj/cjcx_list           空条件 = 全部学期的成绩，返回 #dataList 表格
 *
 * 表格列在不同学校的强智版本里顺序不完全一样，所以列位置一律**按表头文字定位**，
 * 认不出来才退回广财这套固定顺序（序号 / 学期 / 编号 / 名称 / 成绩 / 标识 / 学分 /
 * 学时 / 绩点 / 补重学期 / 考核方式 / 考试性质 / 课程属性 / 课程性质 / 通选课类别）。
 *
 * 绩点口径按「学分加权」：Σ(学分 × 绩点) / Σ学分，只统计绩点大于 0 的课
 * （通过/免修这类不给绩点的课不进分母）。
 */
object EduGrades {

    /** 一条成绩。成绩、绩点都保留原始字符串：等级制（优秀/通过）不硬转数字。 */
    data class Course(
        val term: String,
        val code: String,
        val name: String,
        val score: String,
        val flag: String,
        val credit: Double,
        val point: Double,
        val kind: String,
        val nature: String,
    )

    /**
     * 一份成绩单。[gpa] 与 [average] 都是学分加权；
     * [earned] 是已通过的学分，[failed] 是挂掉的学分。
     */
    data class Report(
        val items: List<Course>,
        val gpa: Double,
        val average: Double,
        val earned: Double,
        val failed: Double,
        val fetchedAt: Long,
    ) {
        /** 按学期分组，新学期在前；同组保持教务给的原顺序。 */
        val byTerm: List<Pair<String, List<Course>>>
            get() = items.groupBy { it.term.ifBlank { "未标学期" } }
                .entries
                .sortedByDescending { it.key }
                .map { it.key to it.value }
    }

    private val LIST_PATHS = listOf("/jsxsd/kscj/cjcx_list", "/jsxsd/kscj/cjcx_list.do")
    private const val QUERY_PATH = "/jsxsd/kscj/cjcx_query"
    private const val LIST_BODY = "kksj=&kcxz=&kcmc=&xsfs=all"

    /** 等级制成绩换算成分数，只用于「加权平均分」，不影响绩点。 */
    private val LEVEL_SCORE = linkedMapOf(
        "优秀" to 95.0,
        "良好" to 85.0,
        "中等" to 75.0,
        "及格" to 65.0,
        "合格" to 65.0,
        "通过" to 65.0,
    )

    // ---------- 抓取 ----------

    /** 拉一学期 / 全部学期的成绩。[base] 形如 http://jwxt.gdufe.edu.cn（不带 /jsxsd）。 */
    fun fetch(base: String, session: JwxtApi.Session): Report {
        val root = base.trimEnd('/')
        // 先踩一下查询页，把「查全部」的会话上下文建立起来；失败不影响后面（有的版本没这一步）
        runCatching { EduHttp.get(session, root + QUERY_PATH) }

        var lastError = "教务没返回成绩页"
        var gotPage = false
        for (path in LIST_PATHS) {
            val url = root + path
            // 先按表单 POST，再不济用 GET 同参数：不同强智版本对方法的要求不一样
            for (usePost in listOf(true, false)) {
                val reply = eduAttempt(url, usePost, session)
                if (reply == null) {
                    lastError = "连不上成绩接口"
                    continue
                }
                if (reply.code == 404) {
                    lastError = "成绩接口不存在（HTTP 404）"
                    continue
                }
                if (!reply.ok) {
                    lastError = "成绩接口返回 HTTP ${reply.code}"
                    continue
                }
                if (reply.text.contains("verifycode.servlet") || reply.text.contains("用户登录")) {
                    throw EduException("登录态已失效，请重新登录教务")
                }
                // 走到这里说明登录态没问题，页面也拿到了
                gotPage = true
                val items = parseItems(reply.text)
                if (items.isEmpty()) {
                    lastError = "成绩表里没有数据"
                    continue
                }
                return compute(items, System.currentTimeMillis())
            }
        }
        // 登录成功但确实一条成绩都没有：返回空表，页面显示「暂无成绩」，别打回未登录
        if (gotPage) return compute(emptyList(), System.currentTimeMillis())
        throw EduException("没抓到成绩：$lastError")
    }

    private fun eduAttempt(url: String, usePost: Boolean, session: JwxtApi.Session): EduHttp.Reply? =
        runCatching {
            if (usePost) EduHttp.post(session, url, LIST_BODY)
            else EduHttp.get(session, "$url?$LIST_BODY")
        }.getOrNull()

    // ---------- 解析 ----------

    /** 纯解析：喂一段成绩页 HTML，出成绩单。找不到表格会抛 [EduException]。 */
    fun parse(html: String, now: Long = System.currentTimeMillis()): Report {
        val items = parseItems(html)
        if (items.isEmpty()) throw EduException("成绩页里没有课程记录")
        return compute(items, now)
    }

    /** 从成绩页 HTML 里拆出课程列表；拆不出来就返回空表。 */
    fun parseItems(html: String): List<Course> {
        val table = scoreTable(html) ?: return emptyList()
        val rows = rowsOf(table)
        if (rows.size < 2) return emptyList()

        val headCells = cellsOf(rows.first())
        val columns = Columns.of(headCells)

        return rows.drop(1).mapNotNull { row -> courseOf(cellsOf(row), columns) }
    }

    private fun courseOf(cells: List<String>, columns: Columns): Course? {
        fun at(index: Int): String = cells.getOrNull(index)?.trim().orEmpty()

        val name = at(columns.name)
        if (name.isEmpty()) return null
        // 表尾的合计 / 平均行不是课
        if (name.contains("合计") || name.contains("平均") || name.contains("小计")) return null

        return Course(
            term = at(columns.term),
            code = at(columns.code),
            name = name,
            score = at(columns.score),
            flag = at(columns.flag),
            credit = number(at(columns.credit)) ?: 0.0,
            point = number(at(columns.point)) ?: 0.0,
            kind = at(columns.kind),
            nature = at(columns.nature),
        )
    }

    /** 优先认 id="dataList" 那张表，认不出来就挑「表头里有课程名称」且行数最多的那张。 */
    private fun scoreTable(html: String): String? {
        val tables = outermostTables(html)
        if (tables.isEmpty()) return null

        val byId = tables.firstOrNull {
            Regex("id\\s*=\\s*[\"']?dataList", RegexOption.IGNORE_CASE).containsMatchIn(it)
        }
        if (byId != null) return byId

        return tables
            .filter { it.contains("课程名称") || it.contains("课程名") }
            .maxByOrNull { rowsOf(it).size }
    }

    /**
     * 只取最外层 table：强智页面偶尔把表格套在表格里，
     * 直接 `.*?</table>` 会在内层提前收口，拆出来的行不完整。
     */
    private fun outermostTables(html: String): List<String> {
        val open = Regex("<table\\b", RegexOption.IGNORE_CASE)
        val close = Regex("</table\\s*>", RegexOption.IGNORE_CASE)
        val out = mutableListOf<String>()
        var cursor = 0
        while (cursor < html.length) {
            val start = open.find(html, cursor) ?: break
            var depth = 1
            var pos = start.range.last + 1
            var end = -1
            while (depth > 0) {
                val nextOpen = open.find(html, pos)
                val nextClose = close.find(html, pos) ?: break
                if (nextOpen != null && nextOpen.range.first < nextClose.range.first) {
                    depth++
                    pos = nextOpen.range.last + 1
                } else {
                    depth--
                    pos = nextClose.range.last + 1
                    if (depth == 0) end = nextClose.range.last + 1
                }
            }
            if (end < 0) break
            out += html.substring(start.range.first, end)
            cursor = maxOf(end, start.range.last + 1)
        }
        return out
    }

    private fun rowsOf(table: String): List<String> =
        Regex("<tr\\b[^>]*>([\\s\\S]*?)</tr\\s*>", RegexOption.IGNORE_CASE)
            .findAll(table)
            .map { it.groupValues[1] }
            .toList()

    private fun cellsOf(row: String): List<String> =
        Regex("<t[dh]\\b[^>]*>([\\s\\S]*?)</t[dh]\\s*>", RegexOption.IGNORE_CASE)
            .findAll(row)
            .map { clean(it.groupValues[1]) }
            .toList()

    /** 去标签、解常见实体、压空白。 */
    private fun clean(raw: String): String = raw
        .replace(Regex("<br\\s*/?>", RegexOption.IGNORE_CASE), " ")
        .replace(Regex("<[^>]+>"), "")
        .replace("&nbsp;", " ")
        .replace("&amp;", "&")
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&quot;", "\"")
        .replace("&#39;", "'")
        .replace(Regex("[ \\t\\u00a0]+"), " ")
        .replace(Regex("\\s*\\n\\s*"), " ")
        .trim()

    // ---------- 列定位 ----------

    private class Columns(
        val term: Int,
        val code: Int,
        val name: Int,
        val score: Int,
        val flag: Int,
        val credit: Int,
        val point: Int,
        val kind: Int,
        val nature: Int,
    ) {
        companion object {
            /** 广财这套固定顺序，表头认不出来时的兜底。 */
            val FALLBACK = Columns(1, 2, 3, 4, 5, 6, 8, 10, 13)

            fun of(header: List<String>): Columns {
                // 名称是最关键的列，它都定位不到就整行按兜底来
                val name = locate(header, listOf("课程名称", "课程名", "名称"))
                if (name < 0) return FALLBACK

                return Columns(
                    term = or(locate(header, listOf("开课学期", "学年学期", "学期")), FALLBACK.term),
                    code = or(locate(header, listOf("课程编号", "课程代码", "课程号")), FALLBACK.code),
                    name = name,
                    score = or(
                        locate(header, listOf("成绩"), not = listOf("标识", "性质", "类型", "方式")),
                        FALLBACK.score,
                    ),
                    flag = or(locate(header, listOf("成绩标识", "标识")), FALLBACK.flag),
                    credit = or(locate(header, listOf("学分")), FALLBACK.credit),
                    point = or(locate(header, listOf("绩点", "平均绩点")), FALLBACK.point),
                    kind = or(locate(header, listOf("考核方式", "考核")), FALLBACK.kind),
                    nature = or(locate(header, listOf("课程性质", "性质")), FALLBACK.nature),
                )
            }

            private fun or(value: Int, fallback: Int): Int = if (value >= 0) value else fallback

            private fun locate(
                header: List<String>,
                names: List<String>,
                not: List<String> = emptyList(),
            ): Int {
                val cells = header.map { it.replace(Regex("\\s+"), "") }
                // 先要完全等于，再要包含（"成绩"不能命中"成绩标识"）
                names.forEach { name ->
                    val hit = cells.indexOf(name)
                    if (hit >= 0) return hit
                }
                names.forEach { name ->
                    val hit = cells.indexOfFirst { cell ->
                        cell.contains(name) && not.none { cell.contains(it) }
                    }
                    if (hit >= 0) return hit
                }
                return -1
            }
        }
    }

    // ---------- 计算 ----------

    /** 数字：认 "90" / "90.5" / "90分"，认不出（优秀/通过/缓考）返回 null。 */
    fun number(raw: String): Double? {
        val text = raw.trim().replace('．', '.').replace('。', '.')
        val match = Regex("-?\\d+(?:\\.\\d+)?").find(text) ?: return null
        return match.value.toDoubleOrNull()
    }

    /** 是否通过：数值成绩按 60 分线，等级制看字面，都没有就看绩点。 */
    fun passed(course: Course): Boolean {
        val numeric = number(course.score)
        if (numeric != null) return numeric >= 60.0

        val text = course.score.trim()
        if (text.isEmpty()) return course.point > 0
        if (text.contains("不及格") || text.contains("不合格") || text.contains("未通过")) return false
        if (text.equals("F", ignoreCase = true) || text.contains("缺考") || text.contains("舞弊")) return false
        if (text.contains("及格") || text.contains("合格") || text.contains("通过")) return true
        if (LEVEL_SCORE.keys.any { text.contains(it) }) return true

        return course.point > 0
    }

    /** 把课程列表折算成成绩单：学分加权绩点、加权平均分、已修/挂科学分。 */
    fun compute(items: List<Course>, now: Long = System.currentTimeMillis()): Report {
        var pointCredit = 0.0
        var pointWeighted = 0.0
        var scoreCredit = 0.0
        var scoreWeighted = 0.0
        var earned = 0.0
        var failed = 0.0

        items.forEach { course ->
            val credit = course.credit
            if (credit > 0 && course.point > 0) {
                pointCredit += credit
                pointWeighted += credit * course.point
            }
            if (credit > 0) {
                val score = number(course.score) ?: LEVEL_SCORE.entries
                    .firstOrNull { course.score.contains(it.key) }?.value
                if (score != null) {
                    scoreCredit += credit
                    scoreWeighted += credit * score
                }
            }
            when {
                passed(course) -> earned += credit
                credit > 0 -> failed += credit
            }
        }

        return Report(
            items = items,
            gpa = if (pointCredit > 0) pointWeighted / pointCredit else 0.0,
            average = if (scoreCredit > 0) scoreWeighted / scoreCredit else 0.0,
            earned = earned,
            failed = failed,
            fetchedAt = now,
        )
    }
}