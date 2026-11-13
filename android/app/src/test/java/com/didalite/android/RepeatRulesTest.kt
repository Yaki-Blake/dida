package com.didalite.android

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test
import java.time.Instant
import java.time.LocalDateTime
import java.time.ZoneId
import java.util.TimeZone

/**
 * 重复规则推导的边界验证（计划书 T4.5）：1/31→2/28、2/29→2/28、工作日跨周末、每 N 天、追平。
 * 纯 JVM 测试，不依赖设备。
 */
class RepeatRulesTest {

    private val zone: ZoneId = ZoneId.of("Asia/Shanghai")

    @Before
    fun setUp() {
        TimeZone.setDefault(TimeZone.getTimeZone("Asia/Shanghai"))
    }

    @After
    fun tearDown() {
        TimeZone.setDefault(TimeZone.getTimeZone("Asia/Shanghai"))
    }

    private fun at(year: Int, month: Int, day: Int, hour: Int = 9, minute: Int = 0): Long =
        LocalDateTime.of(year, month, day, hour, minute).atZone(zone).toInstant().toEpochMilli()

    private fun local(utcMs: Long): LocalDateTime =
        Instant.ofEpochMilli(utcMs).atZone(zone).toLocalDateTime()

    @Test
    fun dailyKeepsWallClock() {
        val next = RepeatRules.next(at(2026, 11, 12, 23, 30), "daily")!!
        assertEquals(LocalDateTime.of(2026, 11, 13, 23, 30), local(next))
    }

    @Test
    fun weeklyAddsSevenDays() {
        val next = RepeatRules.next(at(2026, 11, 12, 8, 0), "weekly")!!
        assertEquals(LocalDateTime.of(2026, 11, 19, 8, 0), local(next))
    }

    @Test
    fun monthlyClampsJan31ToFeb28() {
        val next = RepeatRules.next(at(2026, 1, 31, 9, 0), "monthly")!!
        assertEquals(LocalDateTime.of(2026, 2, 28, 9, 0), local(next))
    }

    @Test
    fun monthlyKeepsDayWhenItExists() {
        val next = RepeatRules.next(at(2026, 3, 15, 9, 0), "monthly")!!
        assertEquals(LocalDateTime.of(2026, 4, 15, 9, 0), local(next))
    }

    @Test
    fun yearlyFeb29FallsBackToFeb28() {
        val next = RepeatRules.next(at(2024, 2, 29, 9, 0), "yearly")!!
        assertEquals(LocalDateTime.of(2025, 2, 28, 9, 0), local(next))
    }

    @Test
    fun weekdaySkipsWeekend() {
        // 2026-11-13 是周五
        val next = RepeatRules.next(at(2026, 11, 13, 9, 0), "weekday")!!
        assertEquals(LocalDateTime.of(2026, 11, 16, 9, 0), local(next))
    }

    @Test
    fun everyThreeDays() {
        val next = RepeatRules.next(at(2026, 11, 12, 9, 0), "every:3")!!
        assertEquals(LocalDateTime.of(2026, 11, 15, 9, 0), local(next))
    }

    @Test
    fun nextAfterJumpsPastNow() {
        val base = at(2026, 1, 1, 9, 0)
        val now = at(2026, 1, 10, 10, 0)
        val next = RepeatRules.nextAfter(base, "daily", now)!!
        assertEquals(LocalDateTime.of(2026, 1, 11, 9, 0), local(next))
    }

    @Test
    fun noneAndInvalidReturnNull() {
        assertNull(RepeatRules.next(at(2026, 11, 12), "none"))
        assertNull(RepeatRules.next(at(2026, 11, 12), "every:1"))
        assertNull(RepeatRules.next(at(2026, 11, 12), "every:abc"))
    }

    @Test
    fun advanceKeepsOffsets() {
        val task = Task(
            id = "T1",
            title = "交房租",
            startAt = at(2026, 1, 31, 9, 0),
            dueAt = at(2026, 1, 31, 10, 0),
            remindAt = at(2026, 1, 31, 8, 30),
            repeatRule = "monthly",
        )
        val next = ReminderPlanner.advance(task, at(2026, 1, 31, 9, 1))!!
        assertEquals(LocalDateTime.of(2026, 2, 28, 9, 0), local(next.startAt!!))
        assertEquals(LocalDateTime.of(2026, 2, 28, 10, 0), local(next.dueAt!!))
        assertEquals(LocalDateTime.of(2026, 2, 28, 8, 30), local(next.remindAt!!))
    }
}
