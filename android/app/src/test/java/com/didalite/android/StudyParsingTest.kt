package com.didalite.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 学业接口的解析规则：成绩表列定位、加权绩点、第二课堂选最缺的类别。
 * 样本按强智 dataList 手写，不依赖网络。第二课堂走 JSON（org.json 在 JVM 单测里是 Stub），
 * 所以只测不碰 JSON 的那部分。
 */
class StudyParsingTest {

    private val gradeHtml = """
        <html><body>
        <table id="dataList">
        <tr><th>序号</th><th>开课学期</th><th>课程编号</th><th>课程名称</th><th>成绩</th><th>成绩标识</th>
        <th>学分</th><th>总学时</th><th>绩点</th><th>补重学期</th><th>考核方式</th><th>考试性质</th>
        <th>课程属性</th><th>课程性质</th><th>通选课类别</th></tr>
        <tr><td>1</td><td>2025-2026-1</td><td>B01001</td><td>大学英语 I</td><td>88</td><td></td>
        <td>3.0</td><td>48</td><td>3.8</td><td></td><td>考试</td><td>初修</td>
        <td>学科基础课</td><td>必修</td><td></td></tr>
        <tr><td>2</td><td>2025-2026-1</td><td>B01002</td><td>高等数学</td><td>优秀</td><td></td>
        <td>4.0</td><td>64</td><td>4.0</td><td></td><td>考查</td><td>初修</td>
        <td>学科基础课</td><td>必修</td><td></td></tr>
        <tr><td>3</td><td>2025-2026-1</td><td>B01003</td><td>体育 I</td><td>52</td><td></td>
        <td>1.0</td><td>32</td><td>0.0</td><td></td><td>考查</td><td>初修</td>
        <td>通识课</td><td>必修</td><td></td></tr>
        </table>
        </body></html>
    """.trimIndent()

    @Test
    fun `成绩表按表头定位列`() {
        val report = EduGrades.parse(gradeHtml)
        assertEquals(3, report.items.size)

        val first = report.items.first()
        assertEquals("2025-2026-1", first.term)
        assertEquals("大学英语 I", first.name)
        assertEquals(3.0, first.credit, 0.001)
        assertEquals(3.8, first.point, 0.001)
        assertEquals("考试", first.kind)

        val terms = report.byTerm
        assertEquals(1, terms.size)
        assertEquals("2025-2026-1", terms.first().first)
    }

    @Test
    fun `绩点与均分按学分加权 挂科不算已修`() {
        val report = EduGrades.parse(gradeHtml)
        // (3.8x3 + 4.0x4) / 7
        assertEquals(3.914, report.gpa, 0.01)
        // (88x3 + 95x4 + 52x1) / 8：加权均分算全部有数字的课，挂科也算在内
        assertEquals(87.0, report.average, 0.01)
        assertEquals(7.0, report.earned, 0.001)
        assertEquals(1.0, report.failed, 0.001)
    }

    @Test
    fun `等级制与数字成绩都按 60 分线判通过`() {
        val report = EduGrades.parse(gradeHtml)
        assertTrue(EduGrades.passed(report.items[0]))
        assertTrue(EduGrades.passed(report.items[1]))
        assertEquals(false, EduGrades.passed(report.items[2]))
    }

    @Test
    fun `数字解析认 分数与小数`() {
        assertEquals(90.0, EduGrades.number("90分")!!, 0.001)
        assertEquals(3.5, EduGrades.number("3.5")!!, 0.001)
        assertEquals(-1.0, EduGrades.number("-1")!!, 0.001)
        assertNull(EduGrades.number("优秀"))
        assertNull(EduGrades.number("通过"))
    }

    @Test
    fun `成绩页不认识表头时整表放弃`() {
        val html = "<table><tr><th>甲</th><th>乙</th></tr><tr><td>1</td><td>2</td></tr></table>"
        assertTrue(EduGrades.parseItems(html).isEmpty())
    }

    @Test
    fun `第二课堂总学分等于各类之和`() {
        val report = EduSecondClass.Report(
            items = listOf(
                EduSecondClass.Item("思想引领", "", 1.0, 1.5, "100%"),
                EduSecondClass.Item("实践实习", "", 0.0, 1.5, "0%"),
                EduSecondClass.Item("文体艺术", "", 2.0, 1.0, "100%"),
            ),
            fetchedAt = 0L,
            percent = 42.0,
        )
        assertEquals(3.0, report.total, 0.001)
    }
}