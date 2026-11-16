using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using DidaLite.Core;
using DidaLite.Core.Models;
using DidaLite.Data;
using DidaLite.Data.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DidaLite.App.Services;

/// <summary>内嵌同步 API：监听所有网卡的 5080，本机直连，手机走局域网地址。</summary>
public sealed class SyncServer : IDisposable
{
    private const int Port = 5080;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly MySqlTaskStore _store;
    private readonly UserStore _users;
    private readonly LocalStore _local;

    private WebApplication? _app;

    /// <summary>手机端可用的完整地址，如 http://192.168.1.5:5080/sync；取不到网络时为 null。</summary>
    public string? LanUrl { get; private set; }

    private SyncServer(MySqlTaskStore store, UserStore users, LocalStore local)
    {
        _store = store;
        _users = users;
        _local = local;
    }

    /// <summary>配置齐全才启动；缺 .env 或端口起不来时静默返回 null，本地功能不受影响。</summary>
    public static SyncServer? Start(LocalStore local)
    {
        var connection = EnvFile.Get("DIDA_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return null;

        var server = new SyncServer(new MySqlTaskStore(connection), new UserStore(connection), local);
        return server.Run() ? server : null;
    }

    private bool Run()
    {
        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            // 监听所有网卡：本机走 127.0.0.1，手机走局域网 IP
            builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(Port));

            var app = builder.Build();

            // 鉴权：本机请求直接放行（PC 自己就是服务端）；手机必须带登录换到的 token。
            app.Use(async (ctx, next) =>
            {
                // /api/health 不含数据，和 /login 一样免鉴权，便于外部探活
                if (IsLoopback(ctx.Connection.RemoteIpAddress)
                    || ctx.Request.Path == "/login"
                    || ctx.Request.Path == "/api/health")
                {
                    await next(ctx);
                    return;
                }

                if (!await _users.IsValidTokenAsync(ctx.Request.Headers["X-Token"]))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" }, Json);
                    return;
                }

                await next(ctx);
            });

            app.MapPost("/login", LoginAsync);
            app.MapGet("/sync", (HttpContext ctx) => GetAsync(ctx));
            app.MapPost("/sync", (HttpContext ctx) => PostAsync(ctx));

            // 课表：与 /sync 共用 5080，只读数据，不碰任务库
            app.MapScheduleEndpoints();

            app.Start();
            _app = app;
            LanUrl = ResolveLanUrl();
            return true;
        }
        catch (Exception ex)
        {
            // 端口被占或 MySQL 未启动：同步不可用，但待办和提醒照常
            System.Diagnostics.Debug.WriteLine($"同步服务启动失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>POST /login：手机端用电脑上设置的「用户名 + 密码」换一个 token。</summary>
    private async Task LoginAsync(HttpContext ctx)
    {
        var request = await ctx.Request.ReadFromJsonAsync<LoginRequest>(Json);
        var token = request is null ? null : await _users.LoginAsync(request.Name, request.Password);

        if (token is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "用户名或密码错误" }, Json);
            return;
        }

        await ctx.Response.WriteAsJsonAsync(new { token, name = request!.Name }, Json);
    }

    /// <summary>手机端改过闹钟（停用 / 删除）后触发，让电脑端「闹钟」列表跟着刷新。</summary>
    public event EventHandler? AlarmsChanged;

    /// <summary>GET /sync?since=rev：拉取该游标之后的全部变更。</summary>
    private async Task GetAsync(HttpContext ctx)
    {
        var since = long.TryParse(ctx.Request.Query["since"], out var parsed) ? parsed : 0;

        var tasks = await _store.GetChangedSinceAsync(since);
        var maxRev = await _store.GetMaxRevAsync();

        await ctx.Response.WriteAsJsonAsync(Payload(maxRev, null, null, tasks, Alarms()), Json);
    }

    /// <summary>POST /sync：推一批变更，同时带回 since 之后的变更，一次往返完成双向同步。</summary>
    private async Task PostAsync(HttpContext ctx)
    {
        var request = await ctx.Request.ReadFromJsonAsync<SyncRequest>(Json);
        if (request is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var apply = await _store.ApplyAsync(request.Changes);
        ApplyAlarmChanges(request.AlarmChanges);
        var tasks = await _store.GetChangedSinceAsync(request.Since);

        var accepted = apply.Accepted.Select(a => new { id = a.Id, rev = a.Rev });
        var rejected = apply.Rejected.Select(r => new { id = r.Id, reason = r.Reason, server = r.Server });

        await ctx.Response.WriteAsJsonAsync(Payload(apply.MaxRev, accepted, rejected, tasks, Alarms()), Json);
    }

    /// <summary>
    /// 手机端改闹钟只能改两件事：开关、删除。电脑端是闹钟数据的唯一出处，
    /// 所以手机上报的改动直接落到本地 alarm 表，下一次 GET 就不再带出去。
    /// </summary>
    private void ApplyAlarmChanges(List<AlarmChange> changes)
    {
        if (changes.Count == 0) return;

        var alarms = _local.GetAlarms().ToDictionary(a => a.Id);
        var touched = false;
        foreach (var change in changes)
        {
            if (!alarms.TryGetValue(change.Id, out var alarm)) continue;

            if (change.Enabled is bool enabled) alarm.Enabled = enabled;
            if (change.Deleted is bool deleted) alarm.Deleted = deleted;
            alarm.Touch();
            _local.UpsertAlarm(alarm);
            touched = true;
        }

        if (touched) AlarmsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 手机端的「闹钟」页只认这张表：电脑端「闹钟」标签里的独立闹钟。
    /// 任务里的提醒时间、从日历导入的日程都不属于闹钟，不带出去。
    /// </summary>
    private IReadOnlyList<AlarmItem> Alarms() =>
        _local.GetAlarms().Where(a => !a.Deleted).ToList();

    private static object Payload(long maxRev, IEnumerable<object>? accepted, IEnumerable<object>? rejected,
        IReadOnlyList<TaskItem> tasks, IReadOnlyList<AlarmItem> alarms) => new
    {
        server_time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        max_rev = maxRev,
        accepted,
        rejected,
        tasks,
        alarms,
    };

    /// <summary>设置里改了服务器地址：立即用它作为手机端展示地址，不必重启。</summary>
    public void UpdateLanUrl(string baseUrl) => LanUrl = baseUrl.TrimEnd('/') + "/sync";

    private static bool IsLoopback(IPAddress? address) => address is not null && IPAddress.IsLoopback(address);

    /// <summary>脚本会把局域网地址写进 DIDA_LAN_ADDR，优先用它；没写就自己找一个非回环 IPv4。</summary>
    private static string? ResolveLanUrl()
    {
        var configured = EnvFile.Get("DIDA_LAN_ADDR");
        if (string.IsNullOrWhiteSpace(configured))
        {
            var ip = FindLanIp();
            if (ip is null) return null;
            configured = $"http://{ip}:{Port}";
        }

        return configured.TrimEnd('/') + "/sync";
    }

    private static string? FindLanIp()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(address.Address)) continue;
                return address.Address.ToString();
            }
        }

        return null;
    }

    public void Dispose()
    {
        var app = _app;
        if (app is null) return;
        _app = null;

        // 不在 UI 线程上同步等 Kestrel 优雅关闭：正在处理请求时会一直等到默认 30 秒超时，
        // 而那个请求的后续又要回 UI 线程，等于互相锁死。丢到后台限时停，进程退出时一并回收。
        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await app.StopAsync(cts.Token).ConfigureAwait(false); } catch { /* 退出路径不报错 */ }
            await app.DisposeAsync().ConfigureAwait(false);
        });
    }
}

/// <summary>POST /sync 的请求体。</summary>
public sealed class SyncRequest
{
    public long Since { get; set; }

    public List<TaskItem> Changes { get; set; } = [];

    /// <summary>手机端对闹钟的开关 / 删除，可为空。</summary>
    public List<AlarmChange> AlarmChanges { get; set; } = [];
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
