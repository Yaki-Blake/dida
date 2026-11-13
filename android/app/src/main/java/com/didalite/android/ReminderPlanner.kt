package com.didalite.android

/**
 * 提醒排程的纯逻辑，对齐 PC 端 ReminderPlanner：
 * 提醒时间取 remind_at，为空依次回退 start_at → due_at。
 */
object ReminderPlanner {

    fun effectiveRemindAt(task: Task): Long? = task.remindAt ?: task.startAt ?: task.dueAt

    /**
     * 重复任务触发后推进到下一次，保持 remind_at 与 due_at 的原有偏移量。
     * 非重复任务返回 null，调用方保持原样。
     */
    fun advance(task: Task, nowUtcMs: Long): Task? {
        if (!RepeatRules.isRepeating(task.repeatRule)) return null
        val base = task.startAt ?: task.dueAt ?: return null
        val dueOffset = task.dueAt?.minus(base) ?: 0L
        val remindOffset = task.remindAt?.minus(base) ?: 0L
        val next = RepeatRules.nextAfter(base, task.repeatRule, nowUtcMs) ?: return null
        return task.copy(
            startAt = task.startAt?.let { next },
            dueAt = task.dueAt?.let { next + dueOffset },
            remindAt = task.remindAt?.let { next + remindOffset },
            done = false,
            updatedAt = System.currentTimeMillis(),
        )
    }
}
