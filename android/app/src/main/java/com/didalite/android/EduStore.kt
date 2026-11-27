package com.didalite.android

/**
 * 学业数据的本地缓存：两种成绩单各存一段纯文本（字段用 \u0001 分隔），
 * 落在 Db 的 meta 表里，与课表 schedule_text 同一套做法。
 * 只存明细，绩点/总学分读回来时重算——算法只有一份，不会出现缓存和代码对不上。
 */
internal object EduStore {
    private const val SEP = '\u0001'
    private const val GRADES = "edu_grades"
    private const val GRADES_AT = "edu_grades_at"
    private const val SECOND = "edu_second"
    private const val SECOND_AT = "edu_second_at"
    private const val SECOND_PCT = "edu_second_pct"

    fun saveGrades(db: Db, report: EduGrades.Report) {
        val text = report.items.joinToString("\n") { course ->
            listOf(
                course.term, course.code, course.name, course.score, course.flag,
                course.credit.toString(), course.point.toString(), course.kind, course.nature,
            ).joinToString(SEP.toString())
        }
        db.setMeta(GRADES, text)
        db.setMeta(GRADES_AT, report.fetchedAt.toString())
    }

    fun loadGrades(db: Db): EduGrades.Report? {
        val text = db.getMeta(GRADES) ?: return null
        val items = text.lineSequence().filter { it.isNotBlank() }.mapNotNull { line ->
            // limit 保住尾部空字段，不让一个空「课程性质」把整行挤歪
            val cells = line.split(SEP, limit = 9)
            if (cells.size < 9) return@mapNotNull null
            EduGrades.Course(
                term = cells[0],
                code = cells[1],
                name = cells[2],
                score = cells[3],
                flag = cells[4],
                credit = cells[5].toDoubleOrNull() ?: 0.0,
                point = cells[6].toDoubleOrNull() ?: 0.0,
                kind = cells[7],
                nature = cells[8],
            )
        }.toList()
        // 空表也照样返回：登录成功但没成绩时页面显示「暂无成绩」，不能掉回未登录态
        return EduGrades.compute(items, db.getMeta(GRADES_AT)?.toLongOrNull() ?: 0L)
    }

    fun saveSecond(db: Db, report: EduSecondClass.Report) {
        val text = report.items.joinToString("\n") { item ->
            listOf(item.name, item.category, item.credit.toString(), item.need.toString(), item.level)
                .joinToString(SEP.toString())
        }
        db.setMeta(SECOND, text)
        db.setMeta(SECOND_AT, report.fetchedAt.toString())
        db.setMeta(SECOND_PCT, report.percent.toString())
    }

    fun loadSecond(db: Db): EduSecondClass.Report? {
        val text = db.getMeta(SECOND) ?: return null
        val items = text.lineSequence().filter { it.isNotBlank() }.mapNotNull { line ->
            val cells = line.split(SEP, limit = 5)
            if (cells.size < 5) return@mapNotNull null
            EduSecondClass.Item(
                name = cells[0],
                category = cells[1],
                credit = cells[2].toDoubleOrNull() ?: 0.0,
                need = cells[3].toDoubleOrNull() ?: 0.0,
                level = cells[4],
            )
        }.toList()
        if (items.isEmpty()) return null
        return EduSecondClass.Report(
            items = items,
            fetchedAt = db.getMeta(SECOND_AT)?.toLongOrNull() ?: 0L,
            percent = db.getMeta(SECOND_PCT)?.toDoubleOrNull() ?: 0.0,
        )
    }
}