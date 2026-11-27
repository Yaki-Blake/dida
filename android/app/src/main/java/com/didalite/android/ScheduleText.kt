package com.didalite.android

/**
 * 教务课表文本的收尾整理。
 *
 * 强智课表单元格把同一门课的信息重复吐两遍（可见文本 + title 展开），
 * 节次是「第一二节 / 第九十节」这种中文写法，多个课程块之间用一串横线分隔。
 * 抓下来先过这里，页面才像人看的课表。
 */
object ScheduleText {

    private val CN_DIGIT = mapOf(
        '一' to 1, '二' to 2, '三' to 3, '四' to 4, '五' to 5,
        '六' to 6, '七' to 7, '八' to 8, '九' to 9, '十' to 10,
    )

    fun polish(raw: String): String {
        val out = mutableListOf<String>()
        raw.lineSequence().forEach { line ->
            val text = line.trim()
            if (text.isEmpty()) return@forEach
            if (text.contains("未安排时间课程") || text.contains("备注")) return@forEach

            val m = Regex("""^(周[一二三四五六日])\s+(.*)$""").find(text)
            if (m == null) {
                out += text
                return@forEach
            }
            val day = m.groupValues[1]
            val rest = m.groupValues[2]
            val parts = rest.split(Regex("""\s{2,}"""), limit = 2)
            val jie = headToJie(parts[0])
            val body = if (parts.size > 1) parts[1] else ""

            val blocks = body.split(Regex("""[-–—－_~～]{3,}""")).map { refineBlock(it) }.filter { it.isNotEmpty() }
            if (blocks.isEmpty()) out += "$day $jie" else blocks.forEach { out += "$day $jie  $it" }
        }
        return out.joinToString("\n")
    }

    /** 课表里的一门课。 */
    data class Entry(
        val day: String,
        val jie: String,
        val course: String,
        val room: String,
        val teacher: String,
        val weeks: String,
    )

    /** 把整理后的文本切成结构化的课，供页面按天分组显示。 */
    fun entries(raw: String): List<Entry> {
        val list = mutableListOf<Entry>()
        polish(raw).lineSequence().forEach { line ->
            val m = Regex("""^(周[一二三四五六日])\s+(\S*节)\s+(.*)$""").find(line.trim())
                ?: return@forEach
            val day = m.groupValues[1]
            val jie = m.groupValues[2]
            var course = ""
            var room = ""
            var teacher = ""
            var weeks = ""

            fun flush() {
                if (course.isNotBlank()) list += Entry(day, jie, course, cleanRoom(room), teacher, weeks)
                course = ""; room = ""; teacher = ""; weeks = ""
            }

            m.groupValues[3].split(" · ").map { it.trim() }.filter { it.isNotEmpty() }.forEach { t ->
                when {
                    isWeeks(t) -> if (weeks.isEmpty()) weeks = t else { flush(); weeks = t }
                    isRoom(t) -> if (room.isEmpty()) room = t
                    isTeacher(t, course.isNotEmpty()) -> if (teacher.isEmpty()) teacher = t
                    course.isEmpty() -> course = t
                    weeks.isNotEmpty() -> { flush(); course = t }
                }
            }
            flush()
        }
        // 教务单元格在可见内容后面又贴了一遍 title 里的「课程·老师·周次·教室」，
        // 同一门课会被切出两条：按 天+节次+课程+周次 合并，留信息全的那条。
        return list
            .groupBy { "${it.day}|${it.jie}|${it.course}|${it.weeks}" }
            .map { (_, group) ->
                group.maxByOrNull { (if (it.teacher.isNotBlank()) 1 else 0) + (if (it.room.isNotBlank()) 1 else 0) }!!
            }
    }

    private fun isWeeks(t: String) = t.contains("周") && Regex("""^[\d,\-]+\(.*周\)$""").matches(t)

    /** 「1-2节」→ 1..2，「5节」→ 5..5。 */
    fun jieRange(jie: String): IntRange {
        val nums = Regex("""\d+""").findAll(jie).map { it.value.toInt() }.toList()
        return when {
            nums.size >= 2 -> nums[0]..nums[1]
            nums.size == 1 -> nums[0]..nums[0]
            else -> 0..0
        }
    }

    /**
     * 把「2-16(周)」「2,4-5,7,9,12,14,16(周)」「2-16(双周)」「2-16(单周)」
     * 展开成具体周次集合。
     */
    fun weekSet(spec: String): Set<Int> {
        if (spec.isBlank()) return emptySet()
        val odd = spec.contains("单周")
        val even = spec.contains("双周")
        val body = spec.substringBefore("(")
        val weeks = sortedSetOf<Int>()
        body.split(",").forEach { part ->
            val t = part.trim()
            val dash = t.indexOf('-')
            if (dash > 0) {
                val a = t.substring(0, dash).trim().toIntOrNull()
                val b = t.substring(dash + 1).trim().toIntOrNull()
                if (a != null && b != null) for (w in a..b) weeks += w
            } else {
                t.toIntOrNull()?.let { weeks += it }
            }
        }
        return when {
            odd -> weeks.filter { it % 2 == 1 }.toSet()
            even -> weeks.filter { it % 2 == 0 }.toSet()
            else -> weeks
        }
    }

    /** 带职称后缀的一律视为教师，不参与教室判定（如「刘楼副教授」含「楼」但不是教室）。 */
    private fun isTeacherTitle(t: String) =
        t.endsWith("讲师") || t.endsWith("教授") || t.endsWith("工程师") ||
            t.endsWith("助教") || t.endsWith("老师")

    private fun isRoom(t: String) =
        !isTeacherTitle(t) &&
            (t.contains("楼") || t.contains("馆") || t.contains("场") || t.contains("实验室") ||
                t.contains("中心") || t.contains("校区"))

    /**
     * 去掉地点里所有圆括号及其内容：楼栋代码与补充说明都不要。
     * 例：「笃行楼(SJ2)419」→「笃行楼419」、
     *     「拓新楼205（计算机基础实验室）」→「拓新楼205」。
     */
    private fun cleanRoom(room: String): String =
        room.replace(Regex("""[（(][^）)]*[）)]"""), "").trim()

    private fun isTeacher(t: String, courseReady: Boolean): Boolean {
        if (!courseReady) return false
        if (isTeacherTitle(t)) return true
        return Regex("""^[\u4e00-\u9fa5]{2,4}$""").matches(t) && !t.contains("课")
    }

    /** 「第一二节」→「1-2节」，「1-2节」原样归一。 */
    fun headToJie(head: String): String {
        val digits = Regex("""\d{1,2}""").findAll(head).map { it.value.toInt() }.toList()
        if (digits.size >= 2) return "${digits[0]}-${digits[1]}节"
        val cn = head.toList().mapNotNull { CN_DIGIT[it] }
        return when {
            cn.size >= 2 -> "${cn[0]}-${cn[1]}节"
            cn.size == 1 -> "${cn[0]}节"
            digits.size == 1 -> "${digits[0]}节"
            else -> head
        }
    }

    /** 一块里去掉重复条目和没用的 [01-02]节 标记。 */
    private fun refineBlock(block: String): String {
        val tokens = block.split(Regex("""\s*·\s*|\n"""))
            .map { it.trim() }
            .filter { it.isNotEmpty() }
            .filterNot { Regex("""^\[\d+-\d+\]节$""").matches(it) }
        return tokens.distinct().joinToString(" · ")
    }
}
