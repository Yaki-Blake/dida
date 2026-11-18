namespace DidaLite.App.ViewModels;

/// <summary>同步浮层的三种状态。</summary>
public enum SyncToastKind
{
    /// <summary>点了同步、结果还没回来：只有一句「正在同步…」。</summary>
    Working,

    /// <summary>同步完成：报上传 / 下载各几条。</summary>
    Success,

    /// <summary>失败或取消：报原因。</summary>
    Error,
}

/// <summary>
/// 一条同步浮层要显示的内容。这里只说「显示什么」，动画与排布交给窗口层，
/// 视图模型不碰任何界面类型。
/// </summary>
public sealed record SyncToastInfo(
    SyncToastKind Kind,
    int Uploaded = 0,
    int Downloaded = 0,
    string? Extra = null,
    string? Message = null)
{
    public static SyncToastInfo Working() => new(SyncToastKind.Working);

    public static SyncToastInfo Success(int uploaded, int downloaded, string? extra = null) =>
        new(SyncToastKind.Success, uploaded, downloaded, extra);

    public static SyncToastInfo Error(string message) => new(SyncToastKind.Error, Message: message);
}
