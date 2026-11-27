package com.didalite.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 日历导入的裁决规则：核心是「事件号被复用」这件事。
 *
 * Android 的 Events._ID 是 INTEGER PRIMARY KEY（没有 AUTOINCREMENT），删掉末尾事件后
 * 新建事件会拿回同一个号。用户报的「写进日历的日程同步不上去」就是这条：
 * 同号的老任务还躺着墓碑，导入时直接 continue，新日程永远变不成任务。
 */
class CalendarImportTest {

    private fun event(
        id: Long,
        title: String = "日程$id",
        startAt: Long = 1_700_000_000_000L,
        dueAt: Long? = null,
        remindAt: Long? = null,
        location: String? = null,
        allDay: Boolean = false,
    ) = SystemCalendar.Event(
        eventId = id,
        title = title,
        note = null,
        location = location,
        startAt = startAt,
        endAt = dueAt,
        allDay = allDay,
        remindAt = remindAt,
    )

    private fun task(
        id: String,
        title: String = "任务",
        startAt: Long = 1_700_000_000_000L,
        deleted: Boolean = false,
    ) = Task(id = id, title = title, startAt = startAt, deleted = deleted)

    @Test
    fun `没有老任务就新建`() {
        val e = event(7, "新日程")
        assertEquals(
            SystemCalendar.ImportAction.INSERT,
            SystemCalendar.decide(null, SystemCalendar.toTask(e)),
        )
    }

    @Test
    fun `活着且内容一致就跳过`() {
        val e = event(7, "巡河")
        val old = SystemCalendar.toTask(e).copy(id = SystemCalendar.encode(7))
        assertEquals(
            SystemCalendar.ImportAction.SKIP,
            SystemCalendar.decide(old, SystemCalendar.toTask(e)),
        )
    }

    @Test
    fun `活着但内容变了就更新`() {
        val e = event(7, "巡河（改时间）")
        val old = SystemCalendar.toTask(e).copy(
            id = SystemCalendar.encode(7),
            title = "巡河",
            startAt = 1_600_000_000_000L,
        )
        assertEquals(
            SystemCalendar.ImportAction.UPDATE,
            SystemCalendar.decide(old, SystemCalendar.toTask(e)),
        )
    }

    @Test
    fun `墓碑但内容相同不复活`() {
        // 用户在云端删掉了这条，手机日历里事件还在：不能又拽回来
        val e = event(7, "巡河")
        val old = SystemCalendar.toTask(e).copy(id = SystemCalendar.encode(7), deleted = true)
        assertEquals(
            SystemCalendar.ImportAction.SKIP,
            SystemCalendar.decide(old, SystemCalendar.toTask(e)),
        )
    }

    @Test
    fun `墓碑但内容不同说明事件号被复用必须复活`() {
        // 这是本次报障的根因场景：老任务 197 删了留墓碑，日历新建日程又拿到 197
        val old = task(SystemCalendar.encode(197), "旧日程", 1_700_000_000_000L, deleted = true)
        val fresh = SystemCalendar.toTask(event(197, "新写的日程", 1_800_000_000_000L))
        assertEquals(
            SystemCalendar.ImportAction.REVIVE,
            SystemCalendar.decide(old, fresh),
        )
    }

    @Test
    fun `复活后的任务 id 不变但内容取日历侧且标脏`() {
        val old = task(SystemCalendar.encode(197), "旧日程", 1_700_000_000_000L, deleted = true)
            .copy(rev = 788)
        val fresh = SystemCalendar.toTask(event(197, "新写的日程", 1_800_000_000_000L))
        val revived = fresh.copy(
            id = old.id,
            rev = old.rev,
            deleted = false,
            updatedAt = 1_900_000_000_000L,
        )
        assertEquals(SystemCalendar.encode(197), revived.id)
        assertEquals(788L, revived.rev)
        assertFalse(revived.deleted)
        assertEquals("新写的日程", revived.title)
        assertEquals(1_800_000_000_000L, revived.startAt)
        assertTrue(revived.dirty)
    }

    @Test
    fun `只有标题相同但时间不同也算新事件`() {
        val old = task(SystemCalendar.encode(30), "acm", 1_700_000_000_000L, deleted = true)
        val fresh = SystemCalendar.toTask(event(30, "acm", 1_800_000_000_000L))
        assertEquals(
            SystemCalendar.ImportAction.REVIVE,
            SystemCalendar.decide(old, fresh),
        )
    }

    @Test
    fun `六项内容全等才算没变`() {
        val base = event(7, "巡河", 1_700_000_000_000L, 1_700_000_900_000L, 1_699_999_000_000L, "沁湖", false)
        val a = SystemCalendar.toTask(base)
        assertEquals(true, SystemCalendar.sameContent(a, SystemCalendar.toTask(base)))
        assertEquals(
            false,
            SystemCalendar.sameContent(a, SystemCalendar.toTask(base.copy(title = "巡河2"))),
        )
        assertEquals(
            false,
            SystemCalendar.sameContent(a, SystemCalendar.toTask(base.copy(startAt = 1))),
        )
        assertEquals(
            false,
            SystemCalendar.sameContent(a, SystemCalendar.toTask(base.copy(endAt = null))),
        )
        assertEquals(
            false,
            SystemCalendar.sameContent(a, SystemCalendar.toTask(base.copy(remindAt = null))),
        )
        assertEquals(
            false,
            SystemCalendar.sameContent(a, SystemCalendar.toTask(base.copy(location = "别处"))),
        )
        assertEquals(
            false,
            SystemCalendar.sameContent(a, SystemCalendar.toTask(base.copy(allDay = true))),
        )
    }
}
