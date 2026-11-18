using DidaLite.Core.Models;

namespace DidaLite.Data.Sync;

/// <summary>
/// 同步协议的三份请求体。电脑端、桌面内嵌服务、独立服务器共用这一份定义，
/// 免得三处各写一份、字段悄悄走样（之前 SyncClient 挪到数据层才发现重复了两份）。
/// </summary>
public sealed class SyncRequest
{
    public long Since { get; set; }

    public List<TaskItem> Changes { get; set; } = [];

    /// <summary>手机端对闹钟的开关 / 删除，可为空。</summary>
    public List<AlarmChange> AlarmChanges { get; set; } = [];

    /// <summary>
    /// 电脑端上传的闹钟全量快照（未删除项）。服务器据此整表替换该账号的闹钟；
    /// 手机端永远是空数组——它只改开关和删除，不会新建闹钟。
    /// </summary>
    public List<AlarmItem> Alarms { get; set; } = [];

    /// <summary>
    /// 本轮要推的便签改动（电脑端与手机端都会推，两边都能改）。
    /// 便签有独立的 rev 空间，游标放在 <see cref="NoteSince"/>，不复用任务的 Since。
    /// </summary>
    public List<NoteItem> Notes { get; set; } = [];

    /// <summary>便签游标：上一轮拿到的最大便签 rev。老客户端不带这个字段，按 0 处理。</summary>
    public long NoteSince { get; set; }
}

/// <summary>手机端上报的一条闹钟改动。</summary>
public sealed class AlarmChange
{
    public string Id { get; set; } = string.Empty;

    public bool? Enabled { get; set; }

    public bool? Deleted { get; set; }
}

/// <summary>POST /login 的请求体。</summary>
public sealed class LoginRequest
{
    public string Name { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}
