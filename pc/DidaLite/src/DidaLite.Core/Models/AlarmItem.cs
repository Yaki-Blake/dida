namespace DidaLite.Core.Models;

/// <summary>
/// 独立闹钟。与任务提醒分开：只按「时:分」在本机响，不依赖任务。
/// 字段与 task 对齐（rev / dirty / updated_at），便于后续并入同步协议。
/// </summary>
public sealed class AlarmItem
{
    /// <summary>单次闹钟，响一次后自动停用。</summary>
    public const string Once = "once";

    /// <summary>每天。</summary>
    public const string Daily = "daily";

    /// <summary>周一到周五。</summary>
    public const string Weekday = "weekday";

    public string Id { get; set; } = Ulid.NewId();
    public string Label { get; set; } = string.Empty;

    /// <summary>小时，0–23，本地时间。</summary>
    public int Hour { get; set; } = 7;

    /// <summary>分钟，0–59，本地时间。</summary>
    public int Minute { get; set; }

    /// <summary>重复规则：once / daily / weekday。</summary>
    public string RepeatRule { get; set; } = Daily;

    /// <summary>单次闹钟的目标时刻（UTC 毫秒）；其他规则为空。</summary>
    public long? OnceAt { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>最近一次已响的时刻（UTC 毫秒），防止同一次重复弹出。</summary>
    public long? LastFiredAt { get; set; }

    /// <summary>墓碑标记。</summary>
    public bool Deleted { get; set; }

    public long UpdatedAt { get; set; }

    /// <summary>服务端权威版本号，客户端只读。</summary>
    public long Rev { get; set; }

    /// <summary>仅客户端：待上传标记。</summary>
    public bool Dirty { get; set; }

    public string TimeText => $"{Hour:00}:{Minute:00}";

    public AlarmItem Clone() => (AlarmItem)MemberwiseClone();

    public void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Dirty = true;
    }
}
