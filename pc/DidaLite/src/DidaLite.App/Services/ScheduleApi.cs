using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DidaLite.App.Services;

/// <summary>
/// 课表只读接口，挂在内嵌 5080 服务上：
///   GET /                  课表页面（快照已内联）
///   GET /api/health        健康检查
///   GET /api/schedule      全量或按 date/from/to/week/weekday 过滤
///   GET /api/schedule/today
///   GET /api/schedule/week?week=
///   GET /api/courses
///   GET /schedule.ics      原始 ICS，可直接作为日历订阅源
///   POST /api/schedule/import  导入 .ics / .json（?name= 可带中文文件名）
///   POST /api/schedule/clear   清空课表
///   POST /api/schedule/sync    触发同步（默认走默认浏览器，配置 mode=cdp 可回退专用窗口）
///   GET /api/schedule/sync/status  查询同步状态
///   GET /dida-sync            默认浏览器同步助手（书签 / 油猴脚本安装页）
///   GET /api/schedule/sync-config  浏览器脚本用的公共参数（不含凭据）
///   GET /gdufe_export.js、/dida-sync-runner.js、/dida-sync.user.js  同步助手资源
/// 契约与独立版 schedule_server.py 一致；鉴权沿用现有 SyncServer 中间件
/// （本机 loopback 放行，局域网需 X-Token）。教务系统页面 -> 本机 127.0.0.1
/// 属于跨源 / 私有网络请求，因此上述端点额外放行 CORS 与 PNA 预检。
/// </summary>
public static class ScheduleApi
{
    public static void MapScheduleEndpoints(this WebApplication app)
    {
        var store = ScheduleStore.Instance;

        Func<HttpContext, Task> page = async ctx =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            await ctx.Response.WriteAsync(store.PageHtml);
        };

        app.MapGet("/", page);
        app.MapGet("/index.html", page);

        // ---- 默认浏览器同步助手：页面内脚本把课表 POST 回本机 5080 ----
        app.MapGet("/dida-sync", (HttpContext ctx) =>
            WriteAsset(ctx, "dida_sync.html", "text/html; charset=utf-8"));
        app.MapGet("/dida-sync-runner.js", (HttpContext ctx) =>
            WriteAsset(ctx, "dida_sync_runner.js", "application/javascript; charset=utf-8"));
        app.MapGet("/gdufe_export.js", (HttpContext ctx) =>
            WriteAsset(ctx, "gdufe_export.js", "application/javascript; charset=utf-8"));
        app.MapGet("/dida-sync.user.js", (HttpContext ctx) =>
            WriteAsset(ctx, "dida_sync.user.js", "application/javascript; charset=utf-8"));

        // 浏览器脚本用的公共参数：只有校历 / 接口路径，不含任何账号凭据
        app.MapGet("/api/schedule/sync-config", async (HttpContext ctx) =>
        {
            var cfg = JwxtSyncService.LoadConfig();
            await WriteJson(ctx, new
            {
                api = $"http://127.0.0.1:{cfg.HelperPort}",
                term = store.Data.Term,
                monday = store.Data.Week1Monday,
                kbPath = cfg.KbPath,
                kbUrl = cfg.KbUrl,
                exclude = cfg.Exclude,
                baseUrl = cfg.BaseUrl,
            });
        });

        // 教务系统页面 -> 127.0.0.1 属于跨源 / 私有网络请求，Chrome 会先发 OPTIONS 预检。
        // 不显式放行的话书签脚本的 POST 根本到不了服务端。
        foreach (var path in new[]
                 {
                     "/api/schedule/import", "/api/schedule/sync-config",
                     "/gdufe_export.js", "/dida-sync-runner.js", "/dida-sync.user.js", "/dida-sync",
                 })
        {
            app.MapMethods(path, ["OPTIONS"], (HttpContext ctx) =>
            {
                AllowCrossOrigin(ctx);
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            });
        }

        app.MapGet("/api/health", async (HttpContext ctx) => await WriteJson(ctx,
            new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["events"] = store.Events.Count,
                ["term"] = store.Data.Term,
                ["week1Monday"] = store.Data.Week1Monday,
                ["source"] = store.Data.Source,
            }));

        app.MapGet("/api/schedule", async (HttpContext ctx) =>
        {
            var q = ctx.Request.Query;
            var list = store.Query(Get(q, "date"), Get(q, "from"), Get(q, "to"),
                Int(q, "week"), Int(q, "weekday"));
            await WriteJson(ctx, store.Payload(list));
        });

        app.MapGet("/api/schedule/today", async (HttpContext ctx) =>
        {
            var today = store.Today;
            var body = store.Payload(store.Query(today, null, null, null, null));
            body["today"] = today;
            body["currentWeek"] = store.CurrentWeek;
            await WriteJson(ctx, body);
        });

        app.MapGet("/api/schedule/week", async (HttpContext ctx) =>
        {
            var q = ctx.Request.Query;
            var body = store.Payload(store.Query(null, null, null, Int(q, "week"), null));
            body["week"] = Get(q, "week") ?? string.Empty;
            await WriteJson(ctx, body);
        });

        app.MapGet("/api/courses", async (HttpContext ctx) =>
            await WriteJson(ctx, store.Courses()));

        // 导入 / 清空：与界面按钮共用 ScheduleImporter，落盘后即时刷新，不重启也能生效。
        // curl --data-binary @课表.ics "http://127.0.0.1:5080/api/schedule/import?name=课表.ics"
        // 文件名优先取 ?name=（URL 编码可带中文），其次 X-Filename 头（仅 ASCII）。
        app.MapPost("/api/schedule/import", async (HttpContext ctx) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            var raw = ms.ToArray();
            var name = ctx.Request.Query["name"].ToString();
            if (string.IsNullOrWhiteSpace(name)) name = ctx.Request.Headers["X-Filename"].ToString();
            if (string.IsNullOrWhiteSpace(name)) name = "api-import.ics";
            var result = ScheduleImporter.ImportText(Encoding.UTF8.GetString(raw), raw, name);
            // 默认浏览器书签 / 油猴脚本回传：打时间戳，让正在等待的「一键同步」立即收尾
            if (result.Ok && string.Equals(ctx.Request.Query["source"].ToString(), "browser",
                    StringComparison.OrdinalIgnoreCase))
                JwxtSyncService.MarkBrowserImport();
            ctx.Response.StatusCode = result.Ok
                ? StatusCodes.Status200OK
                : StatusCodes.Status400BadRequest;
            await WriteJson(ctx, new
            {
                ok = result.Ok,
                events = result.Events,
                courses = result.Courses,
                source = result.Source,
                message = result.Message,
            });
        });

        app.MapPost("/api/schedule/clear", async (HttpContext ctx) =>
        {
            var result = ScheduleImporter.Clear();
            ctx.Response.StatusCode = result.Ok
                ? StatusCodes.Status200OK
                : StatusCodes.Status500InternalServerError;
            await WriteJson(ctx, new
            {
                ok = result.Ok,
                events = result.Events,
                message = result.Message,
            });
        });

        // 一键同步：触发后台任务后立即返回，进度用 status 端点查询。
        // curl -X POST http://127.0.0.1:5080/api/schedule/sync
        app.MapGet("/api/schedule/sync/status", async (HttpContext ctx) =>
        {
            var s = JwxtSyncService.Status;
            await WriteJson(ctx, new
            {
                state = s.State,
                message = s.Message,
                events = s.Events,
                at = s.At,
                ok = s.State == "done",
            });
        });

        app.MapPost("/api/schedule/sync", async (HttpContext ctx) =>
        {
            var s = JwxtSyncService.Status;
            if (s.State is "starting" or "waitingLogin" or "waitingBookmark" or "importing")
            {
                ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                await WriteJson(ctx, new
                {
                    started = false,
                    state = s.State,
                    message = "已有同步任务在进行中，请查询 /api/schedule/sync/status",
                });
                return;
            }

            _ = Task.Run(async () =>
            {
                await JwxtSyncService.SyncAsync(null, CancellationToken.None);
            });
            await WriteJson(ctx, new
            {
                started = true,
                state = "starting",
                message = "同步已启动：已用默认浏览器打开教务系统，按提示完成一次同步即可",
            });
        });

        app.MapGet("/schedule.ics", async (HttpContext ctx) =>
        {
            if (store.IcsBytes.Length == 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsync("schedule.ics not found");
                return;
            }

            ctx.Response.ContentType = "text/calendar; charset=utf-8";
            ctx.Response.Headers["Content-Disposition"] = "attachment; filename=\"schedule.ics\"";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            await ctx.Response.Body.WriteAsync(store.IcsBytes, 0, store.IcsBytes.Length);
        });
    }

    private static async Task WriteJson(HttpContext ctx, object value)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        AllowCrossOrigin(ctx);
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(value, ScheduleStore.Json));
    }

    /// <summary>只监听本机 5080，对浏览器页面放行跨源读取；不开放凭据 Cookie。</summary>
    private static void AllowCrossOrigin(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["Access-Control-Allow-Origin"] = "*";
        h["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        h["Access-Control-Allow-Headers"] = "Content-Type, X-Filename";
        h["Access-Control-Allow-Private-Network"] = "true";
        h["Access-Control-Max-Age"] = "600";
    }

    /// <summary>随包附带的静态资源；直接读文件，改文件即可生效，不必重新编译。</summary>
    private static async Task WriteAsset(HttpContext ctx, string fileName, string contentType)
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "Assets", fileName);
        if (!File.Exists(path)) path = Path.Combine(baseDir, fileName);
        if (!File.Exists(path))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsync("asset not found: " + fileName);
            return;
        }

        ctx.Response.ContentType = contentType;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        AllowCrossOrigin(ctx);
        await ctx.Response.WriteAsync(
            await File.ReadAllTextAsync(path, Encoding.UTF8), Encoding.UTF8);
    }

    private static string? Get(IQueryCollection q, string key) =>
        q.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : null;

    private static int? Int(IQueryCollection q, string key) =>
        int.TryParse(Get(q, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
}
