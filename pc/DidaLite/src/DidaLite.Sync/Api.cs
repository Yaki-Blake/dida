using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DidaLite.Core.Models;
using DidaLite.Data;
using DidaLite.Data.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;

namespace DidaLite.Sync;

/// <summary>
/// 同步 API：/login、/sync（GET/POST）、/api/health。
/// 协议与桌面端内嵌的 5080 完全一致，手机端只改地址即可。
/// 与桌面版的差别只有一处：**取消了“回环地址免鉴权”**。
/// 服务器放在 nginx 后面时所有请求的来源都可能是 127.0.0.1，那条捷径会变成对全网放行。
/// </summary>
internal sealed class Api
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>同一 IP 10 分钟内失败 8 次就短暂封掉登录，挡一下公网上的撞库。</summary>
    private const int MaxLoginFailures = 8;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);

    private readonly ITaskStore _store;
    private readonly IUserStore _users;
    private readonly IAlarmStore _alarms;
    private readonly INoteStore _notes;
    private readonly LocalStore? _local;
    private readonly Options _options;
    private readonly ConcurrentDictionary<string, List<DateTime>> _failures = new();

    public Api(ITaskStore store, IUserStore users, IAlarmStore alarms, INoteStore notes,
        LocalStore? local, Options options)
    {
        _store = store;
        _users = users;
        _alarms = alarms;
        _notes = notes;
        _local = local;
        _options = options;
    }

    public void Run()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(_options.Port);
            options.AddServerHeader = false;
            // 同步请求体只有几十 KB，1MB 足够，挡掉恶意大包
            options.Limits.MaxRequestBodySize = 1024 * 1024;
        });

        var app = builder.Build();

        if (_options.TrustProxy)
        {
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            };
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            app.UseForwardedHeaders(forwarded);
        }

        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "/";
            if (path is "/" or "/login" or "/api/health")
            {
                await next(ctx);
                return;
            }

            var token = ctx.Request.Headers["X-Token"].ToString();
            var userId = await _users.ResolveTokenAsync(token);

            // 桌面端自己的回环请求：默认关闭，只在显式开启时放行（userId=0 视为本地槽位）
            if (userId is null && _options.AllowLoopback && IsLoopback(ctx.Connection.RemoteIpAddress)) userId = 0;

            if (userId is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" }, Json);
                return;
            }

            ctx.Items["uid"] = userId.Value;
            await next(ctx);
        });

        app.MapGet("/", () => Results.Text("dida-sync ok\n"));

        app.MapGet("/api/health", async () =>
        {
            var count = await _users.CountAsync();
            return Results.Json(new
            {
                ok = true,
                backend = "sqlite",
                users = count,
                users_configured = count > 0,
                server_time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }, Json);
        });

        app.MapPost("/login", LoginAsync);
        app.MapGet("/sync", GetAsync);
        app.MapPost("/sync", PostAsync);

        Console.WriteLine($"监听 0.0.0.0:{_options.Port}，健康检查 http://127.0.0.1:{_options.Port}/api/health");
        app.Run();
    }

    /// <summary>POST /login：用电脑/服务器上设置的用户名密码换 token。</summary>
    private async Task LoginAsync(HttpContext ctx)
    {
        var ip = ClientIp(ctx);

        if (IsBlocked(ip))
        {
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await ctx.Response.WriteAsJsonAsync(new { error = "尝试过于频繁，请稍后再试" }, Json);
            return;
        }

        var request = await ReadJsonAsync<LoginRequest>(ctx);
        if (request is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "请求体不是合法的 JSON" }, Json);
            return;
        }

        // 隐式注册：用户名不存在会自动建号；存在且密码正确换发 token；密码错返回 null
        var result = await _users.LoginAsync(request.Name, request.Password);

        if (result is null)
        {
            NoteFailure(ip);
            Console.WriteLine($"登录失败：{ip} → {request?.Name ?? "(空)"}");
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "用户名或密码错误" }, Json);
            return;
        }

        _failures.TryRemove(ip, out _);
        await ctx.Response.WriteAsJsonAsync(new { token = result.Value.Token, name = request!.Name }, Json);
    }

    /// <summary>GET /sync?since=rev&amp;note_since=rev：拉取该用户游标之后的全部变更。</summary>
    private async Task GetAsync(HttpContext ctx)
    {
        var userId = (int)(ctx.Items["uid"] ?? 0);
        var since = long.TryParse(ctx.Request.Query["since"], out var parsed) ? parsed : 0;
        var noteSince = long.TryParse(ctx.Request.Query["note_since"], out var noteParsed) ? noteParsed : 0;

        var tasks = await _store.GetChangedSinceAsync(userId, since);
        var maxRev = await _store.GetMaxRevAsync(userId);
        var alarms = await _alarms.GetAlarmsAsync(userId);
        var notes = await _notes.GetChangedSinceAsync(userId, noteSince);
        var noteMaxRev = await _notes.GetMaxRevAsync(userId);

        await ctx.Response.WriteAsJsonAsync(Payload(maxRev, null, null, tasks, alarms, notes, noteMaxRev), Json);
    }

    /// <summary>POST /sync：推一批变更，同时带回该用户 since 之后的变更，一次往返完成双向同步。</summary>
    private async Task PostAsync(HttpContext ctx)
    {
        var userId = (int)(ctx.Items["uid"] ?? 0);
        var request = await ReadJsonAsync<SyncRequest>(ctx);
        if (request is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "请求体不是合法的 JSON" }, Json);
            return;
        }

        var apply = await _store.ApplyAsync(userId, request.Changes);

        // 电脑端带了闹钟快照：整表替换。手机端发的是空数组，不会误清。
        if (request.Alarms.Count > 0) await _alarms.ReplaceAsync(userId, request.Alarms);

        // 手机端改的开关 / 删除，落在同一张表上，下一轮电脑端拉取时带回本地
        await _alarms.ApplyChangesAsync(userId, request.AlarmChanges);

        // 便签：两边都能改，按更新时间逐条裁决；被压过的会在同轮以新 rev 回传
        var noteApply = request.Notes.Count > 0
            ? await _notes.ApplyAsync(userId, request.Notes)
            : new ApplyResult { MaxRev = 0 };

        var tasks = await _store.GetChangedSinceAsync(userId, request.Since);
        var alarms = await _alarms.GetAlarmsAsync(userId);
        var notes = await _notes.GetChangedSinceAsync(userId, request.NoteSince);
        var noteMaxRev = Math.Max(await _notes.GetMaxRevAsync(userId), noteApply.MaxRev);

        var accepted = apply.Accepted.Select(a => new { id = a.Id, rev = a.Rev });
        var rejected = apply.Rejected.Select(r => new { id = r.Id, reason = r.Reason, server = r.Server });

        await ctx.Response.WriteAsJsonAsync(
            Payload(apply.MaxRev, accepted, rejected, tasks, alarms, notes, noteMaxRev, noteApply.Accepted), Json);
    }

    private static object Payload(long maxRev, IEnumerable<object>? accepted, IEnumerable<object>? rejected,
        IReadOnlyList<TaskItem> tasks, IReadOnlyList<AlarmItem> alarms,
        IReadOnlyList<NoteItem> notes, long noteMaxRev, IReadOnlyList<AcceptedChange>? noteAccepted = null) => new
    {
        server_time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        max_rev = maxRev,
        accepted,
        rejected,
        tasks,
        alarms,
        notes,
        note_max_rev = noteMaxRev,
        note_accepted = noteAccepted?.Select(a => new { id = a.Id, rev = a.Rev }),
    };

    private static bool IsLoopback(IPAddress? address) => address is not null && IPAddress.IsLoopback(address);

    /// <summary>
    /// 不挑 Content-Type，一律按 JSON 解析。
    /// 默认的 ReadFromJsonAsync 遇到非 JSON 头会抛异常变成 500，这里统一收敛成 400。
    /// </summary>
    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx) where T : class
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<T>(text, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>取了反代头就用 X-Forwarded-For 的最后一跳，否则直接用连接地址。</summary>
    private static string ClientIp(HttpContext ctx)
    {
        var forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrEmpty(forwarded))
        {
            var last = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(last)) return last.Trim();
        }

        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private bool IsBlocked(string ip)
    {
        if (!_failures.TryGetValue(ip, out var times)) return false;

        lock (times)
        {
            times.RemoveAll(t => DateTime.UtcNow - t > FailureWindow);
            return times.Count >= MaxLoginFailures;
        }
    }

    private void NoteFailure(string ip)
    {
        var times = _failures.GetOrAdd(ip, _ => []);
        lock (times) times.Add(DateTime.UtcNow);
    }
}
