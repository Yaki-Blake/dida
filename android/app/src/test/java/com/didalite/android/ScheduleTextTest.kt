package com.didalite.android

import org.junit.Assert.assertEquals
import org.junit.Test

/** 用真机抓下来的课表原文做样本，锁住整理规则。 */
class ScheduleTextTest {

    @Test
    fun `中文节次转数字`() {
        assertEquals("1-2节", ScheduleText.headToJie("第一二节"))
        assertEquals("9-10节", ScheduleText.headToJie("第九十节"))
        assertEquals("3-4节", ScheduleText.headToJie("3-4节"))
        assertEquals("5节", ScheduleText.headToJie("第五节"))
    }

    @Test
    fun `去掉重复条目与 节次标记`() {
        val raw = "周一 第一二节  大学英语 II · 2-16(周) · 笃行楼(SJ2)411 · [01-02]节 ·  " +
            "大学英语 II · 杨璇瑜讲师 · 2-16(周) · 笃行楼(SJ2)411 · [01-02]节"
        assertEquals(
            "周一 1-2节  大学英语 II · 2-16(周) · 笃行楼(SJ2)411 · 杨璇瑜讲师",
            ScheduleText.polish(raw),
        )
    }

    @Test
    fun `横线分隔的两门课拆成两行`() {
        val raw = "周二 第三四节  国家安全教育 · 18-19(周) · 笃行楼(SJ2)205 · [03-04]节 · " +
            "---------------------- · 羽毛球 · 2-16(周) · 风雨球馆 · [03-04]节"
        assertEquals(
            "周二 3-4节  国家安全教育 · 18-19(周) · 笃行楼(SJ2)205\n" +
                "周二 3-4节  羽毛球 · 2-16(周) · 风雨球馆",
            ScheduleText.polish(raw),
        )
    }

    @Test
    fun `备注行丢掉`() {
        assertEquals("", ScheduleText.polish("周一 备注:  未安排时间课程："))
    }

    @Test
    fun `已经干净的文本不会被改坏`() {
        val clean = "周三 1-2节  岭南文化地理 · 2-16(周) · 励学楼(SJ1)303"
        assertEquals(clean, ScheduleText.polish(clean))
    }

    @Test
    fun `切出结构化的课`() {
        val raw = "周一 第一二节  大学英语 II · 2-16(周) · 笃行楼(SJ2)411 · [01-02]节 ·  " +
            "大学英语 II · 杨璇瑜讲师 · 2-16(周) · 笃行楼(SJ2)411 · [01-02]节"
        val e = ScheduleText.entries(raw).single()
        assertEquals("周一", e.day)
        assertEquals("1-2节", e.jie)
        assertEquals("大学英语 II", e.course)
        assertEquals("笃行楼(SJ2)411", e.room)
        assertEquals("杨璇瑜讲师", e.teacher)
        assertEquals("2-16(周)", e.weeks)
    }

    @Test
    fun `一格两门课切成两条`() {
        val raw = "周二 第三四节  国家安全教育 · 18-19(周) · 笃行楼(SJ2)205 · [03-04]节 · " +
            "---------------------- · 羽毛球 · 2-16(周) · 风雨球馆 · [03-04]节"
        val list = ScheduleText.entries(raw)
        assertEquals(2, list.size)
        assertEquals("国家安全教育", list[0].course)
        assertEquals("羽毛球", list[1].course)
        assertEquals("风雨球馆", list[1].room)
    }

    @Test
    fun `周次展开`() {
        assertEquals((2..16).toSet(), ScheduleText.weekSet("2-16(周)"))
        assertEquals(setOf(2, 4, 6, 8, 10, 12, 14, 16), ScheduleText.weekSet("2-16(双周)"))
        assertEquals(setOf(3, 5, 7, 9, 11, 13, 15), ScheduleText.weekSet("2-16(单周)"))
        assertEquals(
            setOf(2, 4, 5, 7, 9, 12, 14, 16),
            ScheduleText.weekSet("2,4-5,7,9,12,14,16(周)"),
        )
    }

    @Test
    fun `节次区间`() {
        assertEquals(1..2, ScheduleText.jieRange("1-2节"))
        assertEquals(9..10, ScheduleText.jieRange("9-10节"))
        assertEquals(5..5, ScheduleText.jieRange("5节"))
    }
}
