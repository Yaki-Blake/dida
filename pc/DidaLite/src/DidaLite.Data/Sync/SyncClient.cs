using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using DidaLite.Core;
using DidaLite.Core.Models;

namespace DidaLite.Data.Sync;

/// <summary>
/// PC 端同步客户端：推本地待上传的改动，同时把服务端增量拉回来，一次往返完成。
/// 两种后端共用同一套协议：单机模式打本机 5080（回环免鉴权），
/// 远程模式打自建服务器（先 /login 换 token，再带 X-Token 请求）。
/// </summary>
public sealed class SyncClient
{
    public const string DefaultUrl = "http://127.0.0.1:5080/sync";

    private const string ModeKey = "sync.mode";

    /// <summary>上一次同步用的账号身份。换账号时据此判断要不要先清空本地再全量拉取。</summary>
    private const string AccountKey = "sync.account";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly LocalStore _store;
    private readonly HttpClient _http;
    private string _url;
    private RemoteConfig? _remote;

    /// <summary>本轮同步是不是刚刚完成一次换账号清空，用来给界面换一句更准确的提示。</summary>
    private bool _switchedThisRun;

    private SyncClient(LocalStore store, string url, RemoteConfig? remote)
    {
        _store = store;
        _url = url;
        _remote = remote;

        // 远程要跨公网，8 秒对移动网络太紧；单机模式保持原来的快失败
        _http = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new LanBypassProxy(HttpClient.DefaultProxy),
            ConnectTimeout = TimeSpan.FromSeconds(6),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(remote is null ? 8 : 20)
        };
    }

    /// <summary>true = 权威库在自建服务器上；false = 本机 SQLite 兼做服务端。</summary>
    public bool IsRemote => _remote is not null;

    /// <summary>远程配置；单机模式为 null。</summary>
    public RemoteConfig? Remote => _remote;

    /// <summary>实际请求地址，也用于界面展示「手机该填什么」。</summary>
    public string Address => _url;

    public bool HasCredentials => _remote is null || _remote.HasCredentials;

    /// <summary>远程 token 按服务器地址分开存：换了服务器不会拿着旧 token 去撞。</summary>
    private string TokenKey => "remote.token:" + _remote!.BaseUrl;

    /// <summary>
    /// 当前账号身份：单机模式固定 "local"，远程模式是「服务器地址|用户名」。
    /// 密码不参与——改密码只是换凭据，数据还是同一份，不该触发清空。
    /// </summary>
    private string CurrentAccount => _remote is null
        ? "local"
        : _remote.BaseUrl.TrimEnd('/') + "|" + _remote.Name;

    /// <summary>
    /// 建客户端。配了 DIDA_REMOTE_URL 走服务器，否则仍是本机 5080；
    /// 顺便检测前后端是否换了——换了就把游标归零、本地全部标脏，重传一次做全量对齐。
    /// </summary>
    public static SyncClient Create(LocalStore store)
    {
        var remote = RemoteConfig.Read();

        var client = remote is null
            ? new SyncClient(store, ResolveLocalUrl(), null)
            : new SyncClient(store, remote.SyncUrl, remote);

        client.EnsureMode(remote?.SyncUrl ?? "local");
        return client;
    }

    private static string ResolveLocalUrl()
    {
        var url = EnvFile.Get("DIDA_SYNC_URL");
        return string.IsNullOrWhiteSpace(url) ? DefaultUrl : url;
    }

    /// <summary>
    /// 运行时重读 .env，让登录 / 登出 / 换账号立即生效，不再依赖重启。
    /// 配置写盘后由设置页调用；服务器或账号变了就清掉旧 token，
    /// 下次同步会用新账号重新登录。后端切换仍走 EnsureMode 做全量对齐。
    /// </summary>
    public void RefreshConfig()
    {
        var previousBase = _remote?.BaseUrl;
        var previousName = _remote?.Name;

        var remote = RemoteConfig.Read();
        _remote = remote;
        _url = remote is null ? ResolveLocalUrl() : remote.SyncUrl;
        // 注意：HttpClient.Timeout 发过请求后不能再改，这里保持构造时的值。
        // 远程/单机超时差异只在首次建客户端时体现，热刷新不调整（改了会抛异常）。

        // 服务器地址或账号变了：旧 token 属于另一个账号，必须作废
        if (!string.Equals(previousBase, remote?.BaseUrl, StringComparison.Ordinal)
            || !string.Equals(previousName, remote?.Name, StringComparison.Ordinal))
        {
            if (previousBase is not null)
                _store.SetMeta("remote.token:" + previousBase, string.Empty);
        }

        EnsureMode(remote?.SyncUrl ?? "local");
    }

    /// <summary>
    /// 登出：清掉当前服务器缓存的 token，让下一次同步不再带着旧凭据。
    /// </summary>
    public void ClearToken()
    {
        if (_remote is not null && !string.IsNullOrWhiteSpace(_remote.BaseUrl))
            _store.SetMeta(TokenKey, string.Empty);
    }

    /// <summary>
    /// 登录自检的结果。<see cref="Ok"/> 为真时带 token，否则 <see cref="Message"/>
    /// 是一句能直接放到界面上、用户看得懂的话。
    /// </summary>
    public sealed record LoginCheck(bool Ok, string Message);

    /// <summary>
    /// 设置页「登录」按钮用：拿界面上现填的地址和账号真发一次 /login。
    /// 不落盘、不存 token——能不能登进去先回答清楚，要不要保存由界面决定。
    /// 地址填成网站首页时也会返回 200（一张 HTML），所以这里得看内容而不是只看状态码。
    /// </summary>
    public static async Task<LoginCheck> TestLoginAsync(RemoteConfig cfg, CancellationToken ct = default)
    {
        using var http = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new LanBypassProxy(HttpClient.DefaultProxy),
            ConnectTimeout = TimeSpan.FromSeconds(6),
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        try
        {
            using var response = await http.PostAsJsonAsync(
                cfg.LoginUrl, new LoginBody(cfg.Name, cfg.Password), Json, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new LoginCheck(false, "用户名或密码不对");

            if (!response.IsSuccessStatusCode)
                return new LoginCheck(false, Describe(response.StatusCode));

            var raw = await response.Content.ReadAsStringAsync(ct);
            LoginResult? payload;
            try
            {
                payload = JsonSerializer.Deserialize<LoginResult>(raw, Json);
            }
            catch (JsonException)
            {
                return new LoginCheck(false,
                    "这个地址返回的是网页而不是接口数据——看起来填成了网站首页，不是同步服务");
            }

            if (string.IsNullOrWhiteSpace(payload?.Token))
                return new LoginCheck(false, "接口没有返回 token，这个地址不像滴答的同步服务");

            return new LoginCheck(true, "登录成功");
        }
        catch (TaskCanceledException)
        {
            return new LoginCheck(false, "连接超时：服务器没在 15 秒内响应，检查地址和端口");
        }
        catch (HttpRequestException ex)
        {
            return new LoginCheck(false, $"连不上服务器：{ex.Message}");
        }
    }

    /// <summary>
    /// 两种模式的 rev 序列互不相干，混用会让游标指到不存在的区间、增量永远拉不到东西。
    /// 所以后端一变就重置一次：游标归零 + 本地所有行标脏，下一轮把本地那份全量推上去。
    /// </summary>
    private void EnsureMode(string mode)
    {
        var previous = _store.GetMeta(ModeKey);

        if (previous is null)
        {
            // 老版本没有这个键：记下当前模式即可，不要无端重传
            _store.SetMeta(ModeKey, mode);
            return;
        }

        if (string.Equals(previous, mode, StringComparison.Ordinal)) return;

        _store.SetMeta(ModeKey, mode);
        _store.SetCursor(0);
        _store.MarkAllDirty();
        _store.SetMeta("sync.mode.changed_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
    }

    public async Task<SyncStats> SyncAsync(CancellationToken ct = default)
    {
        EnsureAccountRecorded();

        // 全量快照同步：每次上传本端整份日历（含墓碑），服务器按修改时间逐条合并。
        // 这样任何一条都不会因为「没标脏 / 标脏失败 / 游标错位」而漏传——不存在增量漏同步。
        var dirty = _store.GetAll();
        var since = _store.GetCursor();

        // 闹钟跟着每轮同步整批上传：服务器拿它当该账号的快照，手机端「闹钟」页就是照这份渲染。
        // 已删除的不传，服务器会给没出现的旧闹钟自己打墓碑。
        var alarms = _store.GetAlarms().Where(a => !a.Deleted).ToList();

        // 便签同任务：整份上传（含墓碑），由服务器按修改时间合并。
        var dirtyNotes = _store.GetAllNotes();

        var body = new SyncRequest
        {
            Since = since,
            Changes = dirty,
            Alarms = alarms,
            Notes = dirtyNotes,
            NoteSince = _store.GetNoteCursor(),
        };

        using var response = await SendAsync(body, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Describe(response.StatusCode));

        var payload = await response.Content.ReadFromJsonAsync<SyncResponse>(Json, ct)
                      ?? throw new InvalidOperationException("服务端返回了空内容");

        // 记下发出去时的修改时间：推送途中又改过的行不该被误清 dirty
        var sentAt = dirty.ToDictionary(t => t.Id, t => t.UpdatedAt);

        foreach (var accepted in payload.Accepted ?? [])
            _store.MarkSynced(accepted.Id, accepted.Rev, sentAt.GetValueOrDefault(accepted.Id));

        foreach (var rejected in payload.Rejected ?? [])
            if (rejected.Server is not null) _store.ApplyServer(rejected.Server);

        foreach (var task in payload.Tasks ?? []) _store.ApplyServer(task);

        // 手机的开关 / 删除会随服务器回来；本地闹钟是出处，只接受比本地新的那条
        var alarmChanges = _store.MergeAlarmsFromServer(payload.Alarms ?? []);

        // 便签：先给被接受的脏行补 rev 并清 dirty（用发送时的 updated_at 判重，途中改过的不清），
        // 再落服务端增量（时间戳裁决，本地更新的版本不会被覆盖），最后推进便签游标。
        if (dirtyNotes.Count > 0)
        {
            var sentNoteAt = dirtyNotes.ToDictionary(n => n.Id, n => n.UpdatedAt, StringComparer.Ordinal);
            foreach (var accepted in payload.NoteAccepted ?? [])
                _store.NoteMarkSynced(accepted.Id, accepted.Rev, sentNoteAt.GetValueOrDefault(accepted.Id));
        }

        var notesApplied = 0;
        foreach (var note in payload.Notes ?? [])
            if (_store.ApplyServerNote(note)) notesApplied++;

        if (payload.NoteMaxRev > 0) _store.SetNoteCursor(payload.NoteMaxRev);

        _store.SetCursor(payload.MaxRev);

        var switched = _switchedThisRun;
        _switchedThisRun = false;

        return new SyncStats(payload.Accepted?.Count ?? 0, payload.Rejected?.Count ?? 0, payload.Tasks?.Count ?? 0,
            switched, alarmChanges, notesApplied);
    }

    /// <summary>
    /// 账号是否与上次同步时不同。换账号等于换一份数据，界面据此先弹确认再清空。
    /// 没有历史记录（首次运行 / 刚升级）不算切换，避免误删用户已有数据。
    /// </summary>
    public bool HasAccountChanged
    {
        get
        {
            var previous = _store.GetMeta(AccountKey);
            return previous is not null
                   && !string.Equals(previous, CurrentAccount, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 确认换账号：先用新账号登一次，登录成功才清空本地任务与游标、作废旧 token，
    /// 下一轮同步就是「since=0、只拉不推」，本地唯一数据来源变成新账号的云数据。
    ///
    /// 顺序不能反：登录失败（密码错、服务器不通）时本地一个字节都不动，
    /// 否则会出现"清空了本地、又连不上新账号"的纯丢数据。
    /// 回到单机模式不清空——那份数据本来就存在本机，仍按原来的全量重传处理。
    /// </summary>
    public async Task ApplyAccountSwitchAsync(CancellationToken ct = default)
    {
        if (_remote is not null)
        {
            // 旧 token 属于上一个账号，必须先作废，否则这一步会拿旧身份登进来
            _store.SetMeta(TokenKey, string.Empty);
            await LoginAsync(ct);
        }

        _store.SetMeta(AccountKey, CurrentAccount);

        if (_remote is null) return;

        _store.ClearSyncedData();
        _switchedThisRun = true;
    }

    /// <summary>
    /// 同步前的账目核对：首次运行只登记账号；检测到账号变了却没走 <see cref="ApplyAccountSwitch"/>，
    /// 说明界面跳过了确认环节——这时宁可报错也不能把旧账号的数据推到新账号里去。
    /// </summary>
    private void EnsureAccountRecorded()
    {
        if (_store.GetMeta(AccountKey) is null)
        {
            _store.SetMeta(AccountKey, CurrentAccount);
            return;
        }

        if (HasAccountChanged)
            throw new InvalidOperationException("换了同步账号：需要先确认清空本地数据，才能从新账号导入。");
    }

    /// <summary>远程模式先要 token；服务器说 token 不认（改了密码 / 重设账号）就重新登录再发一次。</summary>
    private async Task<HttpResponseMessage> SendAsync(SyncRequest body, CancellationToken ct)
    {
        if (_remote is null) return await SendOnceAsync(body, null, ct);

        var token = await EnsureTokenAsync(ct);
        var response = await SendOnceAsync(body, token, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        _store.SetMeta(TokenKey, string.Empty);

        var fresh = await LoginAsync(ct);
        return await SendOnceAsync(body, fresh, ct);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(SyncRequest body, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = JsonContent.Create(body, options: Json)
        };

        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Add("X-Token", token);

        return await _http.SendAsync(request, ct);
    }

    private async Task<string> EnsureTokenAsync(CancellationToken ct)
    {
        var cached = _store.GetMeta(TokenKey);
        return string.IsNullOrWhiteSpace(cached) ? await LoginAsync(ct) : cached;
    }

    private async Task<string> LoginAsync(CancellationToken ct)
    {
        if (_remote is null || !_remote.HasCredentials)
            throw new InvalidOperationException("还没填服务器账号：.env 里需要 DIDA_REMOTE_USER 和 DIDA_REMOTE_PASSWORD");

        using var response = await _http.PostAsJsonAsync(
            _remote.LoginUrl, new LoginBody(_remote.Name, _remote.Password), Json, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("服务器说用户名或密码不对");

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"登录失败：HTTP {(int)response.StatusCode}");

        var payload = await response.Content.ReadFromJsonAsync<LoginResult>(Json, ct);
        var token = payload?.Token;
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("登录返回里没有 token");

        _store.SetMeta(TokenKey, token);
        return token;
    }

    /// <summary>把 HTTP 状态翻成用户看得懂的一句话，别在界面上直接甩编码。</summary>
    private static string Describe(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized => "服务器拒绝了凭据：检查 .env 里的远程账号和密码",
        HttpStatusCode.Forbidden => "服务器拒绝访问：可能是 nginx 或防火墙拦了",
        HttpStatusCode.TooManyRequests => "登录失败次数太多，服务器暂时限流，等十分钟再试",
        HttpStatusCode.RequestEntityTooLarge => "要上传的数据太大，超出了服务器允许的请求体",
        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable => "连不上服务器：确认 dida-sync 在跑，也看看本机代理是否拦了这个地址",
        HttpStatusCode.NotFound => "服务器上没有 /sync 接口：地址填错了端口或路径",
        _ => $"服务端返回 HTTP {(int)code}"
    };

    private sealed class SyncResponse
    {
        public long MaxRev { get; set; }
        public List<AcceptedDto>? Accepted { get; set; }
        public List<RejectedDto>? Rejected { get; set; }
        public List<TaskItem>? Tasks { get; set; }

        /// <summary>该账号在服务器上的闹钟。老服务器没有这个字段，反序列化后为 null。</summary>
        public List<AlarmItem>? Alarms { get; set; }

        /// <summary>便签增量（note_since 之后）。老服务器没有这个字段，为 null 时整段跳过。</summary>
        public List<NoteItem>? Notes { get; set; }

        /// <summary>便签空间的当前最大 rev，用作下一轮的 note_since。</summary>
        public long NoteMaxRev { get; set; }

        /// <summary>本轮被服务端接受的便签（含被更高版本压过后重新编号的那条）。</summary>
        public List<AcceptedDto>? NoteAccepted { get; set; }
    }

    private sealed class AcceptedDto
    {
        public string Id { get; set; } = string.Empty;
        public long Rev { get; set; }
    }

    private sealed class RejectedDto
    {
        public string Id { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public TaskItem? Server { get; set; }
    }

    private sealed record LoginBody(string Name, string Password);

    private sealed class LoginResult
    {
        public string? Token { get; set; }
    }
}

/// <summary>一轮同步的结果，用于界面提示。</summary>
public sealed record SyncStats(
    int Accepted, int Rejected, int Pulled, bool AccountSwitched = false, int AlarmsUpdated = 0,
    int NotesChanged = 0);

/// <summary>
/// 环回与内网地址不走系统代理：本机 5080、局域网、WSL / 虚拟机的私有网段都不该经过代理。
/// 公网地址仍沿用系统代理设置——有些环境必须靠代理才能出去。
/// 实测踩过：Windows 开着代理时，连内网地址会被代理接走并返回 502。
/// </summary>
internal sealed class LanBypassProxy(IWebProxy inner) : IWebProxy
{
    public ICredentials? Credentials
    {
        get => inner.Credentials;
        set => inner.Credentials = value;
    }

    public Uri? GetProxy(Uri destination) => IsPrivate(destination) ? null : inner.GetProxy(destination);

    public bool IsBypassed(Uri host) => IsPrivate(host) || inner.IsBypassed(host);

    private static bool IsPrivate(Uri uri)
    {
        if (uri.IsLoopback) return true;

        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254);
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;
    }
}
