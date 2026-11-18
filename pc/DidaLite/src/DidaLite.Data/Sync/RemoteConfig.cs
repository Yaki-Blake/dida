using DidaLite.Core;

namespace DidaLite.Data.Sync;

/// <summary>
/// 自建服务器配置。.env 里填了 <see cref="UrlKey"/> 就把服务器当权威库，
/// 留空则还是本机 SQLite 自己做主——两种模式都保留，改一行配置就能切。
/// </summary>
public sealed record RemoteConfig(string BaseUrl, string Name, string Password)
{
    public const string UrlKey = "DIDA_REMOTE_URL";
    public const string UserKey = "DIDA_REMOTE_USER";
    public const string PasswordKey = "DIDA_REMOTE_PASSWORD";

    /// <summary>
    /// 出厂默认的公共服务器，和 EnvFile 模板里写的保持一致。
    /// 设置页「公共服务器」一栏用它预填，用户可改。
    /// </summary>
    public const string DefaultUrl = "https://huangyiwei.online/dida";

    /// <summary>手机端要填的同步地址，也就是电脑端实际请求的地址。</summary>
    public string SyncUrl => BaseUrl + "/sync";

    public string LoginUrl => BaseUrl + "/login";

    /// <summary>服务器地址有了，但账号还没填——界面要提示去补，而不是报 401。</summary>
    public bool HasCredentials => Name.Length > 0 && Password.Length > 0;

    /// <summary>没配地址就是单机模式，返回 null。</summary>
    public static RemoteConfig? Read()
    {
        var url = EnvFile.Get(UrlKey);
        if (string.IsNullOrWhiteSpace(url)) return null;

        return new RemoteConfig(
            Normalize(url),
            EnvFile.Get(UserKey)?.Trim() ?? string.Empty,
            EnvFile.Get(PasswordKey) ?? string.Empty);
    }

    /// <summary>
    /// 容忍几种常见写法：只写 IP:端口、结尾多一根斜杠、直接把 /sync 抄进来。
    /// 这里只做规整，不校验可达性——连不上要由真正发请求时报告。
    /// </summary>
    public static string Normalize(string url)
    {
        var text = url.Trim().TrimEnd('/');
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (text.EndsWith("/sync", StringComparison.OrdinalIgnoreCase)) text = text[..^5].TrimEnd('/');
        return text;
    }
}
