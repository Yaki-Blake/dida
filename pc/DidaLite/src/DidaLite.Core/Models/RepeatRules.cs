namespace DidaLite.Core.Models;

/// <summary>
/// 重复规则编码。只支持计划书 3.4 定义的受控字符串，不解析标准 RRULE。
/// 推导基准是「本次触发时间」，按设备本地时区做日期运算。
/// </summary>
public static class RepeatRules
{
    public const string None = "none";
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
    public const string Yearly = "yearly";
    public const string Weekday = "weekday";
    public const string EveryPrefix = "every:";

    public static readonly string[] All =
    [
        None, Daily, Weekly, Monthly, Yearly, Weekday, "every:2"
    ];

    public static string Every(int days) => $"{EveryPrefix}{Math.Clamp(days, 2, 365)}";

    public static bool IsValid(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return false;
        if (rule is Daily or Weekly or Monthly or Yearly or Weekday) return true;
        if (rule.StartsWith(EveryPrefix, StringComparison.Ordinal))
        {
            return int.TryParse(rule.AsSpan(EveryPrefix.Length), out var n) && n is >= 2 and <= 365;
        }
        return false;
    }

    public static bool IsRepeating(string? rule) =>
        !string.IsNullOrWhiteSpace(rule) && rule != None && IsValid(rule);

    /// <summary>给界面用的中文名。</summary>
    public static string DisplayName(string? rule) => rule switch
    {
        null or None => "不重复",
        Daily => "每天",
        Weekly => "每周",
        Monthly => "每月",
        Yearly => "每年",
        Weekday => "每个工作日",
        _ when rule.StartsWith(EveryPrefix, StringComparison.Ordinal)
            => $"每 {rule[EveryPrefix.Length..]} 天",
        _ => "不重复"
    };

    /// <summary>
    /// 从 currentUtcMs 推出下一次发生时间（UTC 毫秒）。规则无效或无重复时返回 null。
    /// 返回值严格大于 currentUtcMs。
    /// </summary>
    public static long? Next(long currentUtcMs, string? rule)
    {
        if (!IsRepeating(rule)) return null;

        var local = DateTimeOffset.FromUnixTimeMilliseconds(currentUtcMs).ToLocalTime();

        DateTime next = rule switch
        {
            Daily => local.DateTime.AddDays(1),
            Weekly => local.DateTime.AddDays(7),
            Monthly => AddMonthsClamped(local.DateTime, 1),
            Yearly => AddYearsClamped(local.DateTime, 1),
            Weekday => NextWorkday(local.DateTime),
            _ => local.DateTime.AddDays(ParseEvery(rule))
        };

        // 保留原时区偏移，再换回 UTC
        var offset = local.Offset;
        var result = new DateTimeOffset(DateTime.SpecifyKind(next, DateTimeKind.Unspecified), offset);
        return result.ToUniversalTime().ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// 反复推导直到结果晚于 nowUtcMs，用于设备休眠数天后的追平。
    /// </summary>
    public static long? NextAfter(long currentUtcMs, string? rule, long nowUtcMs)
    {
        var cursor = currentUtcMs;
        // 上限保护：最多推 10000 次（约 27 年每日重复）
        for (var i = 0; i < 10_000; i++)
        {
            var next = Next(cursor, rule);
            if (next is null) return null;
            cursor = next.Value;
            if (cursor > nowUtcMs) return cursor;
        }
        return cursor;
    }

    private static int ParseEvery(string? rule)
    {
        if (rule is not null && rule.StartsWith(EveryPrefix, StringComparison.Ordinal)
            && int.TryParse(rule.AsSpan(EveryPrefix.Length), out var n)
            && n is >= 2 and <= 365)
        {
            return n;
        }
        // 解析失败按不重复处理，不抛异常
        return 1;
    }

    private static DateTime AddMonthsClamped(DateTime value, int months)
    {
        var target = value.AddMonths(months);
        // AddMonths 已自动夹到月末，这里仅是语义说明：1/31 → 2/28
        return target;
    }

    private static DateTime AddYearsClamped(DateTime value, int years)
    {
        // 2/29 遇平年：AddYears 会落到 2/28
        return value.AddYears(years);
    }

    private static DateTime NextWorkday(DateTime value)
    {
        var next = value.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            next = next.AddDays(1);
        }
        return next;
    }
}
