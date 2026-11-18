package com.didalite.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 日历删除回流的判断逻辑。
 *
 * 场景就是用户反馈的那条：手机日历里把日程删掉，电脑端也要跟着删。
 * 这几组数据都按真机上的实际情况造：老任务用的是旧编码，事件 3 和 30 会撞成同一个 id。
 */
class CalendarSyncTest {

    private fun event(
        id: Long,
        title: String = "日程$id",
        startAt: Long = 1_700_000_000_000L,
        deleted: Boolean = false,
        own: Boolean = false,
    ) = SystemCalendar.EventRow(id, title, startAt, deleted, own)

    private fun task(id: String, title: String = "任务", startAt: Long = 1_700_000_000_000L) =
        Task(id = id, title = title, startAt = startAt)

    @Test
    fun `新编码不会撞车`() {
        assertEquals("C" + "0".repeat(24) + "3", SystemCalendar.encode(3))
        assertEquals("C" + "0".repeat(23) + "30", SystemCalendar.encode(30))
        assertEquals(26, SystemCalendar.encode(3).length)
        assertFalse(SystemCalendar.encode(3) == SystemCalendar.encode(30))
    }

    @Test
    fun `旧编码会撞车但能认出格式`() {
        assertEquals("C" + "3" + "0".repeat(24), SystemCalendar.legacyEncode(3))
        assertEquals(SystemCalendar.legacyEncode(3), SystemCalendar.legacyEncode(30))
        assertTrue(SystemCalendar.isLegacyId(SystemCalendar.legacyEncode(3)))
        assertFalse(SystemCalendar.isLegacyId(SystemCalendar.encode(3)))
        assertEquals(3L, SystemCalendar.decodeEventId(SystemCalendar.encode(3)))
    }

    @Test
    fun `老任务对应的事件没了就删`() {
        // 事件 12 被用户在日历里删了，任务还是活的
        val task = task(SystemCalendar.legacyEncode(12), "励学楼208班歌初赛")
        val plan = CalendarSync.plan(
            inventory = listOf(event(13), event(14)),
            tasks = listOf(task),
            links = emptyMap(),
        )
        assertEquals(listOf(task.id), plan.tasksToRemove)
    }

    @Test
    fun `老任务对应的事件还在就登记链接`() {
        val task = task(SystemCalendar.legacyEncode(12), "励学楼208班歌初赛")
        val plan = CalendarSync.plan(
            inventory = listOf(event(12, "励学楼208班歌初赛")),
            tasks = listOf(task),
            links = emptyMap(),
        )
        assertTrue(plan.tasksToRemove.isEmpty())
        assertEquals(mapOf(task.id to 12L), plan.linksToAdd)
    }

    @Test
    fun `事件号撞车时靠标题和时间认领`() {
        // 3 和 30 撞成同一个旧 id，这条任务其实是 30 号事件的
        val task = task(SystemCalendar.legacyEncode(30), "开会", startAt = 555L)
        val plan = CalendarSync.plan(
            inventory = listOf(event(3, "别的日程", 111L), event(30, "开会", 555L)),
            tasks = listOf(task),
            links = emptyMap(),
        )
        assertTrue(plan.tasksToRemove.isEmpty())
        assertEquals(mapOf(task.id to 30L), plan.linksToAdd)
    }

    @Test
    fun `撞车又认不出来就不动它`() {
        val task = task(SystemCalendar.legacyEncode(30), "开会", startAt = 555L)
        val plan = CalendarSync.plan(
            inventory = listOf(event(3, "别的日程", 111L), event(30, "另一件事", 999L)),
            tasks = listOf(task),
            links = emptyMap(),
        )
        assertTrue(plan.tasksToRemove.isEmpty())
        assertTrue(plan.linksToAdd.isEmpty())
        assertEquals(1, plan.ambiguous)
    }

    @Test
    fun `电脑端下来的任务被删掉事件也要删`() {
        // 电脑端同步下来的任务，链接指向我们写进日历的事件
        val task = task("01M7PP7NSCN3R1HW947TQSZVRP", "电脑端的日程")
        val plan = CalendarSync.plan(
            inventory = emptyList(),
            tasks = listOf(task),
            links = mapOf(task.id to 162L),
        )
        assertEquals(listOf(task.id), plan.tasksToRemove)
    }

    @Test
    fun `电脑端下来的任务事件还在就不动`() {
        val task = task("01M7PP7NSCN3R1HW947TQSZVRP", "电脑端的日程")
        val plan = CalendarSync.plan(
            inventory = listOf(event(162, own = true)),
            tasks = listOf(task),
            links = mapOf(task.id to 162L),
        )
        assertTrue(plan.tasksToRemove.isEmpty())
    }

    @Test
    fun `事件被标记删除也算没了`() {
        val task = task("01M7PP7NSCN3R1HW947TQSZVRP", "电脑端的日程")
        val plan = CalendarSync.plan(
            inventory = listOf(event(162, deleted = true, own = true)),
            tasks = listOf(task),
            links = mapOf(task.id to 162L),
        )
        assertEquals(listOf(task.id), plan.tasksToRemove)
    }

    @Test
    fun `新编码任务丢了链接也能反推出来`() {
        val task = task(SystemCalendar.encode(42), "新任务")
        val plan = CalendarSync.plan(
            inventory = listOf(event(42, "新任务")),
            tasks = listOf(task),
            links = emptyMap(),
        )
        assertTrue(plan.tasksToRemove.isEmpty())
        assertEquals(mapOf(task.id to 42L), plan.linksToAdd)
    }

    @Test
    fun `已经是墓碑的任务不会再删一遍`() {
        val task = task(SystemCalendar.legacyEncode(12), "已经删过的").copy(deleted = true)
        val plan = CalendarSync.plan(
            inventory = listOf(event(13)),
            tasks = listOf(task),
            links = emptyMap(),
        )
        assertTrue(plan.tasksToRemove.isEmpty())
    }
}
