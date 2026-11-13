using DidaLite.Core.Models;

namespace DidaLite.Core.Reminders;

/// <summary>
/// 提醒排程的纯逻辑，不依赖界面与数据库，便于单测。
/// 提醒永远由客户端本地计算，不依赖网络。
/// </summary>
public static class ReminderPlanner
{
    /// <summary>轮询窗口：提前 2 分钟进入待触发集合。</summary>
    public const long LookAheadMs = 2 * 60 * 1000;

    public static bool IsActive(TaskItem task) => !task.Done && !task.Deleted;

    /// <summary>
    /// 该任务此刻是否应当弹提醒。
    /// 只补发「今天」漏掉的提醒：昨天及更早到点的（比如刚补录的历史日程）不再弹。
    /// </summary>
    public static bool ShouldFire(TaskItem task, long nowUtcMs)
    {
        if (!IsActive(task)) return false;
        if (task.EffectiveRemindAt is not long remindAt) return false;
        if (remindAt > nowUtcMs) return false;

        // 跨天不补发，否则新加入的历史日程一同步进来就会连环弹窗
        if (LocalDateOf(remindAt) < LocalDateOf(nowUtcMs)) return false;

        return task.RemindedAt is not long fired || fired < remindAt;
    }

    private static DateTime LocalDateOf(long utcMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToLocalTime().Date;

    /// <summary>
    /// 重复任务触发后排下一次：保持 remind_at 与 due_at 的原有偏移量。
    /// 非重复任务返回 false，调用方应保持原样。
    /// </summary>
    public static bool AdvanceToNextOccurrence(TaskItem task, long nowUtcMs)
    {
        if (!RepeatRules.IsRepeating(task.RepeatRule)) return false;

        // 以起始时间为基准推进；没有起始时间则退回截止时间
        var anchor = task.StartAt ?? task.DueAt;
        if (anchor is not long baseAt) return false;

        var dueOffset = task.DueAt is long due ? due - baseAt : 0;
        var remindOffset = task.RemindAt is long remind ? remind - baseAt : 0;

        var next = RepeatRules.NextAfter(baseAt, task.RepeatRule, nowUtcMs);
        if (next is null) return false;

        if (task.StartAt is not null) task.StartAt = next;
        if (task.DueAt is not null) task.DueAt = next + dueOffset;
        if (task.RemindAt is not null) task.RemindAt = next + remindOffset;
        task.RemindedAt = null;
        task.Done = false;
        task.Touch();
        return true;
    }

    /// <summary>新建任务时的默认提醒：有截止时间则提前 0 分钟（同一时刻）。</summary>
    public static long? DefaultRemindAt(long? dueAt) => dueAt;
}
