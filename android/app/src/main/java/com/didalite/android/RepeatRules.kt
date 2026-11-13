package com.didalite.android

import java.time.Instant
import java.time.LocalDateTime
import java.time.ZoneId

/**
 * 重复规则编码，与 PC 端 RepeatRules.cs 一一对应，同样只支持计划书 3.4 的受控字符串。
 * 推导基准是「本次触发时间」，按设备本地时区做日期运算。
 */
object RepeatRules {
    const val NONE = "none"
    const val DAILY = "daily"
    const val WEEKLY = "weekly"
    const val MONTHLY = "monthly"
    const val YEARLY = "yearly"
    const val WEEKDAY = "weekday"
    const val EVERY_PREFIX = "every:"

    fun isRepeating(rule: String?): Boolean {
        if (rule.isNullOrBlank() || rule == NONE) return false
        return when (rule) {
            DAILY, WEEKLY, MONTHLY, YEARLY, WEEKDAY -> true
            else -> if (rule.startsWith(EVERY_PREFIX)) parseEvery(rule) != null else false
        }
    }

    fun displayName(rule: String?): String = when {
        rule == null || rule == NONE -> "不重复"
        rule == DAILY -> "每天"
        rule == WEEKLY -> "每周"
        rule == MONTHLY -> "每月"
        rule == YEARLY -> "每年"
        rule == WEEKDAY -> "每个工作日"
        rule == "every" -> "每 N 天"
        rule.startsWith(EVERY_PREFIX) -> "每 ${rule.substringAfter(EVERY_PREFIX)} 天"
        else -> "不重复"
    }

    /** 从 currentUtcMs 推出下一次发生时间；规则无效或无重复时返回 null。 */
    fun next(currentUtcMs: Long, rule: String?): Long? {
        if (!isRepeating(rule)) return null
        val zone = ZoneId.systemDefault()
        val local = Instant.ofEpochMilli(currentUtcMs).atZone(zone).toLocalDateTime()

        val next: LocalDateTime = when (rule) {
            DAILY -> local.plusDays(1)
            WEEKLY -> local.plusWeeks(1)
            MONTHLY -> local.plusMonths(1) // 1/31 → 2/28，java.time 自动夹到月末
            YEARLY -> local.plusYears(1) // 2/29 → 2/28
            WEEKDAY -> nextWorkday(local)
            else -> local.plusDays(parseEvery(rule) ?: 1L)
        }
        return next.atZone(zone).toInstant().toEpochMilli()
    }

    /** 反复推导直到结果晚于 nowUtcMs，用于设备休眠数天后的追平。 */
    fun nextAfter(currentUtcMs: Long, rule: String?, nowUtcMs: Long): Long? {
        var cursor = currentUtcMs
        repeat(10_000) {
            val next = next(cursor, rule) ?: return null
            cursor = next
            if (cursor > nowUtcMs) return cursor
        }
        return cursor
    }

    private fun parseEvery(rule: String?): Long? {
        if (rule == null || !rule.startsWith(EVERY_PREFIX)) return null
        val n = rule.substringAfter(EVERY_PREFIX).toLongOrNull() ?: return null
        return if (n in 2..365) n else null
    }

    private fun nextWorkday(value: LocalDateTime): LocalDateTime {
        var next = value.plusDays(1)
        while (next.dayOfWeek.value > 5) next = next.plusDays(1)
        return next
    }
}
