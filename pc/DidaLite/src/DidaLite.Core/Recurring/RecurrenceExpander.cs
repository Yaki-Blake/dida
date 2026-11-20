using DidaLite.Core.Models;

namespace DidaLite.Core.Recurring;

/// <summary>
/// 把一条带重复规则的日程展开成多条独立日程。
///
/// 设计取舍：不引入 series 字段、不改数据库结构——展开出的每一条都是普通日程
/// （repeat_rule = none、各自独立 id），因此日历上会真的出现多条、能分别勾选完成、
/// 也能原样走现有同步链路。用户选了"每天"就是想要日历上看到一排日程，
/// 而不是原来那种"只有一条、勾选后原地跳到下一次"的摆设行为。
/// </summary>
public static class RecurrenceExpander
{
    /// <summary>默认向前展开 6 个月，足够覆盖一个学期的日程；上限 400 条防止失控。</summary>
    public const int DefaultMonths = 6;
    public const int MaxOccurrences = 400;

    /// <summary>
    /// 展开 [起始时间, 起始时间 + months 个月) 内的所有实例，含第一条（即母任务当天）。
    /// 非重复任务返回原样单条。每条的标题/内容/地点/全天/提醒偏移都沿用母任务，
    /// repeat_rule 统一清零、done 清零，确保是彼此独立的一次性日程。
    /// </summary>
    public static List<TaskItem> Expand(TaskItem master, int months = DefaultMonths, long? nowUtcMs = null)
    {
        var result = new List<TaskItem> { master };
        if (!RepeatRules.IsRepeating(master.RepeatRule)) return result;

        var anchor = master.StartAt ?? master.DueAt;
        if (anchor is not long baseAt) return result;

        var dueOffset = master.DueAt is long due ? due - baseAt : 0;
        var remindOffset = master.RemindAt is long remind ? remind - baseAt : 0;

        var horizon = AddMonths(baseAt, Math.Clamp(months, 1, 24));
        var cursor = baseAt;
        var count = 1;

        while (count < MaxOccurrences)
        {
            var next = RepeatRules.Next(cursor, master.RepeatRule);
            if (next is null || next.Value > horizon) break;

            // 用"下界推进"防止 every:N 等规则在夏令时切换时卡住
            if (next.Value <= cursor) break;
            cursor = next.Value;

            var instance = new TaskItem
            {
                Id = Ulid.NewId(),
                Title = master.Title,
                Note = master.Note,
                Location = master.Location,
                AllDay = master.AllDay,
                RepeatRule = RepeatRules.None, // 展开后每条都是独立的一次性日程
                Done = false,
                Deleted = false,
                StartAt = master.StartAt is not null ? cursor : null,
                DueAt = master.DueAt is not null ? cursor + dueOffset : null,
                RemindAt = master.RemindAt is not null ? cursor + remindOffset : null,
                UpdatedAt = master.UpdatedAt,
            };
            result.Add(instance);
            count++;
        }

        // 母任务自身也变成一次性日程，避免"勾选后原地推进"的摆设行为
        master.RepeatRule = RepeatRules.None;
        return result;
    }

    private static long AddMonths(long utcMs, int months)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToLocalTime();
        var shifted = local.DateTime.AddMonths(months);
        var result = new DateTimeOffset(DateTime.SpecifyKind(shifted, DateTimeKind.Unspecified), local.Offset);
        return result.ToUniversalTime().ToUnixTimeMilliseconds();
    }
}
