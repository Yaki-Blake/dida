using System.Text.Json.Serialization;

namespace DidaLite.Core.Models;

/// <summary>
/// 便签。与任务完全分开：不带日期、不带提醒，只有标题和正文，
/// 用来记事、贴片段、临时存一段文字。字段与 task 一样以 updated_at 做冲突裁决。
/// </summary>
public sealed class NoteItem
{
    public string Id { get; set; } = Ulid.NewId();

    /// <summary>标题，可为空——只写正文也是一种便签。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>正文，换行原样保留。</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>置顶：列表里始终排在最前。</summary>
    public bool Pinned { get; set; }

    /// <summary>墓碑标记，删除不做物理删除。</summary>
    public bool Deleted { get; set; }

    /// <summary>最后修改时间，UTC 毫秒；冲突裁决依据。</summary>
    public long UpdatedAt { get; set; }

    /// <summary>服务端权威版本号，客户端只读。</summary>
    public long Rev { get; set; }

    /// <summary>仅客户端：待上传标记。</summary>
    [JsonIgnore]
    public bool Dirty { get; set; }

    /// <summary>列表里的第一行：没标题就拿正文首行顶上。</summary>
    [JsonIgnore]
    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title.Trim();

            foreach (var line in Body.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line)) return line.Trim();
            }

            return "无标题便签";
        }
    }

    public void Touch()
    {
        UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Dirty = true;
    }
}
