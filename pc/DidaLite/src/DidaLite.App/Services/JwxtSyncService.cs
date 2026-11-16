using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DidaLite.App.Services;

/// <summary>
/// 一键同步配置。文件放在 exe 同目录或 Assets\jwxt_sync.json，可覆盖默认值。
/// 校外走 WebVPN 时，把 baseUrl / kbPath 改成代理后的地址即可。
/// 这些地址只交给浏览器里的书签脚本用，程序自己不会去打开教务系统。
/// </summary>
public sealed class JwxtSyncConfig
{
    public string BaseUrl { get; set; } = "http://jwxt.gdufe.edu.cn/jsxsd/";
    public string KbPath { get; set; } = "/jsxsd/xskb/xskb_list.do";
    /// <summary>书签脚本用的课表页地址；走 WebVPN / 代理域名时改这里。</summary>
    public string KbUrl { get; set; } = "http://jwxt.gdufe.edu.cn/jsxsd/xskb/xskb_list.do";
    public int HelperPort { get; set; } = 5080;
    public int BrowserWaitSeconds { get; set; } = 300;
    public string Exclude { get; set; } = string.Empty;
}

/// <summary>同步状态，供界面与 /api/schedule/sync/status 共用。</summary>
public sealed record SyncStatus(string State, string Message, int Events, string At);

/// <summary>一次同步的结果。</summary>
public sealed record SyncResult(bool Ok, int Events, int Courses, string Message, bool TimedOut);

/// <summary>
/// 从教务系统取课表并导入 dida。只有一条路：用系统默认浏览器打开本机的「同步助手」页，
/// 用户在助手页把「同步到 dida」拖成书签（仅一次），之后在教务课表页点一下书签，
/// 页面里的脚本把课表 POST 回本机 5080，本服务只负责打开助手页和等待完成。
///
/// 程序不启动浏览器窗口、不开调试端口、不直接打开教务系统、不碰用户浏览器数据。
/// 边界：不读取/保存密码、不读取浏览器 Cookie 库、不识别验证码、不做模拟登录；
/// 只查当前登录账号自己的课表，串行单请求，不并发。
/// </summary>
public static class JwxtSyncService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static SyncStatus _status = new("idle", "尚未同步", 0, "");
    private static long _browserImportTicks;

    /// <summary>默认浏览器回传成功的时间戳，供「一键同步」判断用户是否已经完成同步。</summary>
    public static long BrowserImportTicks => Interlocked.Read(ref _browserImportTicks);

    public static void MarkBrowserImport() =>
        Interlocked.Exchange(ref _browserImportTicks, DateTime.UtcNow.Ticks);

    public static SyncStatus Status => Volatile.Read(ref _status);

    private static void SetStatus(string state, string message, int events = 0) =>
        Volatile.Write(ref _status, new SyncStatus(state, message, events,
            DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));

    /// <summary>主入口。同一时刻只允许一个同步任务。</summary>
    public static async Task<SyncResult> SyncAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct))
            return new(false, 0, 0, "已有同步任务在进行中，请稍候", false);

        try
        {
            return await RunDefaultBrowserAsync(progress, ct, LoadConfig());
        }
        catch (OperationCanceledException)
        {
            SetStatus("idle", "同步已取消");
            return new(false, 0, 0, "同步已取消", false);
        }
        catch (Exception ex)
        {
            SetStatus("failed", "同步失败：" + ex.Message);
            return new(false, 0, 0, "同步失败：" + ex.Message, false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// 每次都用系统默认浏览器打开本机的「同步助手」页，直接复用用户已登录的会话 / 密码本；
    /// 由助手页里的书签或油猴脚本把课表 POST 回 5080，这里只负责打开与等待完成。
    /// </summary>
    private static async Task<SyncResult> RunDefaultBrowserAsync(
        IProgress<string>? progress, CancellationToken ct, JwxtSyncConfig cfg)
    {
        var helperUrl = HelperUrl(cfg);
        var since = BrowserImportTicks;

        // 每次都只开助手页：教务课表页由用户自己在浏览器里开，
        // 助手页同时也当书签的中转站（教务页直连 127.0.0.1 常被拦），
        // 所以它必须一直在，不能只在首次冒一下。
        SetStatus("waitingBookmark", "正在打开「同步助手」…");
        progress?.Report("正在打开「同步助手」…");
        foreach (var page in PagesToOpen(cfg)) OpenInDefaultBrowser(page);

        var hint = "已打开「同步助手」：把「同步到 dida」拖到书签栏，在教务课表页点一下书签即可；" +
                   "装了油猴脚本时会自动完成";
        SetStatus("waitingBookmark", hint);
        progress?.Report(hint);

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(30, cfg.BrowserWaitSeconds));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (BrowserImportTicks > since)
            {
                var events = ScheduleStore.Instance.Events.Count;
                var courses = ScheduleStore.Instance.Events.Select(e => e.Name).Distinct().Count();
                var msg = $"同步成功：{events} 节课 · {courses} 门课（来源：默认浏览器）";
                SetStatus("done", msg, events);
                progress?.Report(msg);
                return new(true, events, courses, msg, false);
            }

            await Task.Delay(500, ct);
        }

        var timeout = $"等待同步超时。助手页 {helperUrl} 里把「同步到 dida」拖到书签栏一次，" +
                      "之后在教务课表页点书签即可；装了油猴脚本时刷新一下课表页就会自动同步。" +
                      "dida 需保持运行（本机 5080）。";
        SetStatus("failed", timeout);
        return new(false, 0, 0, timeout, true);
    }

    /// <summary>本机同步助手页地址。</summary>
    public static string HelperUrl(JwxtSyncConfig cfg) => $"http://127.0.0.1:{cfg.HelperPort}/dida-sync";

    /// <summary>
    /// 一次「一键同步」要打开的页面。只有助手页——程序不再自己去打开教务系统，
    /// 教务课表页由用户在浏览器里开。不看任何「首次」标记：每次点都开。
    /// </summary>
    public static IReadOnlyList<string> PagesToOpen(JwxtSyncConfig cfg) => [HelperUrl(cfg)];

    private static void OpenInDefaultBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("打开默认浏览器失败：" + ex.Message);
        }
    }

    public static JwxtSyncConfig LoadConfig()
    {
        var cfg = new JwxtSyncConfig();
        var baseDir = AppContext.BaseDirectory;
        var direct = Path.Combine(baseDir, "jwxt_sync.json");
        var file = File.Exists(direct) ? direct : Path.Combine(baseDir, "Assets", "jwxt_sync.json");
        if (!File.Exists(file)) return cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<JwxtSyncConfig>(
                File.ReadAllText(file, Encoding.UTF8), ScheduleStore.Json) ?? cfg;
        }
        catch { /* 配置损坏时用内置默认值 */ }
        return cfg;
    }
}
