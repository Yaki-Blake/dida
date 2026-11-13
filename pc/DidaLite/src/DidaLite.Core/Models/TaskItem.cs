using System.Text.Json.Serialization;

namespace DidaLite.Core.Models;

/// <summary>
/// 待办任务。字段与开发计划书 3.2 客户端表结构一一对应。
/// 所有时间均为 UTC 毫秒。
/// </summary>
public sealed class TaskItem
{
    public string Id { get; set; } = Ulid.NewId();
    public string Title { get; set; } = string.Empty;
    public string? Note { get; set; }

    /// <summary>地点，可为空。</summary>
    public string? Location { get; set; }

    /// <summary>起始时间，UTC 毫秒，可为空。</summary>
    public long? StartAt { get; set; }

    /// <summary>截止时间，UTC 毫秒，可为空（无日期任务）。</summary>
    public long? DueAt { get; set; }

    /// <summary>全天事件：只有日期没有具体时刻。</summary>
    public bool AllDay { get; set; }

    /// <summary>提醒时间，UTC 毫秒；为空时使用 DueAt。</summary>
    public long? RemindAt { get; set; }

    /// <summary>重复规则编码，见 RepeatRules。</summary>
    public string RepeatRule { get; set; } = RepeatRules.None;

    public bool Done { get; set; }

    /// <summary>墓碑标记，删除不做物理删除。</summary>
    public bool Deleted { get; set; }

    /// <summary>最后修改时间，冲突裁决依据。</summary>
    public long UpdatedAt { get; set; }

    /// <summary>服务端权威版本号，客户端只读。</summary>
    public long Rev { get; set; }

    /// <summary>仅客户端：待上传标记。</summary>
    [JsonIgnore]
    public bool Dirty { get; set; }

    /// <summary>仅客户端：最近一次已弹提醒的时间，防止重复弹窗。</summary>
    [JsonIgnore]
    public long? RemindedAt { get; set; }

    /// <summary>日历定位用的时间：起始时间优先，其次截止时间。</summary>
    [JsonIgnore]
    public long? AnchorAt => StartAt ?? DueAt;

    /// <summary>实际生效的提醒时刻。</summary>
    [JsonIgnore]
    public long? EffectiveRemindAt => RemindAt ?? StartAt ?? DueAt;

    public TaskItem Clone() => (TaskItem)MemberwiseClone();

    public void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Dirty = true;
    }
}
