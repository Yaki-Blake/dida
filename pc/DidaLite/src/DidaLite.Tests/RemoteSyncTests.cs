using System.Net;
using System.Text.Json;
using DidaLite.Core.Models;
using DidaLite.Data;
using DidaLite.Data.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DidaLite.Tests;

/// <summary>
/// 远程模式（自建服务器）的客户端行为：登录换 token、token 复用、失效自动重登、
/// 换后端时游标归零并全量重传。用一个真 Kestrel 假服务器跑，不走 mock。
/// 环境变量是进程级的，所以这一组串行执行。
/// </summary>
[Collection("remote-env")]
public sealed class RemoteSyncTests
{
    [Theory]
    [InlineData("http://1.2.3.4:5081", "http://1.2.3.4:5081")]
    [InlineData("http://1.2.3.4:5081/", "http://1.2.3.4:5081")]
    [InlineData("http://1.2.3.4:5081/sync", "http://1.2.3.4:5081")]
    [InlineData("1.2.3.4:5081", "http://1.2.3.4:5081")]
    [InlineData("  https://dida.example.com/sync/  ", "https://dida.example.com")]
    public void Normalize_accepts_common_forms(string input, string expected) =>
        Assert.Equal(expected, RemoteConfig.Normalize(input));

    [Fact]
    public void No_url_means_local_mode()
    {
        using var env = EnvScope.Clear();
        Assert.Null(RemoteConfig.Read());

        using var store = new TempStore();
        var client = SyncClient.Create(store.Store);

        Assert.False(client.IsRemote);
        Assert.Equal(SyncClient.DefaultUrl, client.Address);
    }

    [Fact]
    public async Task Remote_mode_logs_in_once_then_reuses_token()
    {
        await using var server = await FakeServer.StartAsync();
        using var env = new EnvScope(server.BaseUrl, "yaki", "pw");
        using var store = new TempStore();

        var client = SyncClient.Create(store.Store);
        Assert.True(client.IsRemote);
        Assert.Equal(server.BaseUrl + "/sync", client.Address);

        await client.SyncAsync();
        await client.SyncAsync();

        // 第二次仍然带同一个 token，没有重复登录
        Assert.Equal(1, server.LoginCount);
        Assert.Equal(new[] { "tok-1", "tok-1" }, server.SyncTokens.ToArray());
    }

    [Fact]
    public async Task Rejected_token_triggers_exactly_one_relogin()
    {
        await using var server = await FakeServer.StartAsync();
        using var env = new EnvScope(server.BaseUrl, "yaki", "pw");
        using var store = new TempStore();

        var client = SyncClient.Create(store.Store);
        await client.SyncAsync();

        // 服务器那边改了密码 / 重设账号：旧 token 立刻作废
        server.RevokeToken();

        await client.SyncAsync();

        // 先带旧 token 撞一次 401，再重登换新 token 重发——一共三次 /sync
        Assert.Equal(2, server.LoginCount);
        Assert.Equal(new[] { "tok-1", "tok-1", "tok-2" }, server.SyncTokens.ToArray());
    }

    [Fact]
    public async Task Missing_credentials_fails_with_readable_message()
    {
        await using var server = await FakeServer.StartAsync();
        using var env = new EnvScope(server.BaseUrl, name: string.Empty, password: string.Empty);
        using var store = new TempStore();

        var client = SyncClient.Create(store.Store);
        Assert.False(client.HasCredentials);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SyncAsync());
        Assert.Contains("DIDA_REMOTE_USER", error.Message);
    }

    [Fact]
    public void Switching_backend_resets_cursor_and_marks_all_dirty()
    {
        using var store = new TempStore();

        // 模拟：这台电脑之前在单机模式跑过，游标推到 42，待办都已同步
        store.Store.SetMeta("sync.mode", "local");
        store.Store.SetCursor(42);
        store.Store.Upsert(new TaskItem { Id = "t1", Title = "旧待办", UpdatedAt = 1000, Rev = 7, Dirty = false });
        Assert.Empty(store.Store.GetDirty());

        using var env = new EnvScope("http://127.0.0.1:65000", "yaki", "pw");
        var client = SyncClient.Create(store.Store);

        // 换了后端：游标从 0 重新开始，本地那份全部标脏等上传
        Assert.True(client.IsRemote);
        Assert.Equal(0, store.Store.GetCursor());
        Assert.Single(store.Store.GetDirty());
        Assert.Equal("http://127.0.0.1:65000/sync", store.Store.GetMeta("sync.mode"));
    }

    [Fact]
    public void Same_backend_keeps_cursor_and_dirty_flags()
    {
        using var store = new TempStore();

        store.Store.SetMeta("sync.mode", "local");
        store.Store.SetCursor(42);
        store.Store.Upsert(new TaskItem { Id = "t1", Title = "已同步", UpdatedAt = 1000, Rev = 7, Dirty = false });

        using var env = EnvScope.Clear();
        _ = SyncClient.Create(store.Store);

        // 模式没变，不能无端重传
        Assert.Equal(42, store.Store.GetCursor());
        Assert.Empty(store.Store.GetDirty());
    }

    [Fact]
    public async Task RefreshConfig_switch_account_clears_old_token_and_relogs()
    {
        await using var server = await FakeServer.StartAsync();
        using var store = new TempStore();

        // 运行中的客户端以 alice 登录
        using var env = new EnvScope(server.BaseUrl, "alice", "pw1");
        var client = SyncClient.Create(store.Store);
        await client.SyncAsync();
        Assert.Equal(1, server.LoginCount);

        // 设置页换成 bob 写进 .env（同一进程内存），对同一个客户端热刷新
        Environment.SetEnvironmentVariable(RemoteConfig.UserKey, "bob");
        Environment.SetEnvironmentVariable(RemoteConfig.PasswordKey, "pw2");
        client.RefreshConfig();

        // 账号变了：界面据此弹确认；确认后旧 token 作废，重新登录拿 bob 的新 token
        Assert.Equal("bob", client.Remote!.Name);
        Assert.True(client.HasAccountChanged);
        await client.ApplyAccountSwitchAsync();

        await client.SyncAsync();
        Assert.Equal(2, server.LoginCount);
        Assert.EndsWith("-2", server.SyncTokens[^1] ?? string.Empty);
    }

    [Fact]
    public async Task Switching_account_wipes_local_then_imports_new_account_data()
    {
        await using var server = await FakeServer.StartAsync();
        server.SeedTask("cloud-1", "bob 的云端待办");
        using var store = new TempStore();

        // alice 先同步一轮，再在本地加一条待办（脏行，正常同步会推上去）
        using (new EnvScope(server.BaseUrl, "alice", "pw1"))
        {
            var alice = SyncClient.Create(store.Store);
            await alice.SyncAsync();
            store.Store.Upsert(new TaskItem
            {
                Id = "alice-local",
                Title = "alice 的本地待办",
                UpdatedAt = 2000,
                RepeatRule = RepeatRules.None,
            });
        }

        Assert.Equal(2, store.Store.GetAll().Count);

        // 设置页换成 bob：同一个库、同一个地址，只是账号变了
        using var env = new EnvScope(server.BaseUrl, "bob", "pw2");
        var client = SyncClient.Create(store.Store);
        Assert.True(client.HasAccountChanged);

        await client.ApplyAccountSwitchAsync();

        // 清空：本地一条不剩，游标归零，新账号的 token 已经拿到
        Assert.Empty(store.Store.GetAll());
        Assert.Equal(0, store.Store.GetCursor());
        Assert.False(client.HasAccountChanged);

        var stats = await client.SyncAsync();

        // 只拉不推：请求里没有任何本地改动，云端的 data 原样进来
        Assert.Equal(0, server.LastChangeCount);
        Assert.Equal(0, stats.Accepted);
        Assert.Equal(1, stats.Pulled);
        Assert.True(stats.AccountSwitched);
        Assert.Equal(["cloud-1"], store.Store.GetAll().Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task Sync_refuses_to_push_when_account_changed_but_not_confirmed()
    {
        await using var server = await FakeServer.StartAsync();
        using var store = new TempStore();

        using (new EnvScope(server.BaseUrl, "alice", "pw1"))
            await SyncClient.Create(store.Store).SyncAsync();

        using var env = new EnvScope(server.BaseUrl, "bob", "pw2");
        var client = SyncClient.Create(store.Store);

        // 没走确认就直接同步：宁可报错，也不能把 alice 的数据推到 bob 名下
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SyncAsync());
        Assert.Contains("换了同步账号", error.Message);
    }

    [Fact]
    public async Task ClearToken_forces_relogin_on_next_sync()
    {
        await using var server = await FakeServer.StartAsync();
        using var env = new EnvScope(server.BaseUrl, "yaki", "pw");
        using var store = new TempStore();

        var client = SyncClient.Create(store.Store);
        await client.SyncAsync();
        Assert.Equal(1, server.LoginCount);

        // 登出清 token
        Assert.False(string.IsNullOrWhiteSpace(store.Store.GetMeta("remote.token:" + server.BaseUrl)));
        client.ClearToken();
        Assert.True(string.IsNullOrWhiteSpace(store.Store.GetMeta("remote.token:" + server.BaseUrl)));

        // 再同步必须重新登录
        await client.SyncAsync();
        Assert.Equal(2, server.LoginCount);
    }

    /// <summary>临时库，每个用例一份，结束就删。</summary>
    private sealed class TempStore : IDisposable
    {
        private readonly string _dir;

        public TempStore()
        {
            _dir = Path.Combine(Path.GetTempPath(), "dida-remote-test", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Store = new LocalStore(Path.Combine(_dir, "dida.db"));
            Store.Initialize();
        }

        public LocalStore Store { get; }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { /* SQLite 文件句柄可能还没全放，留着也无妨 */ }
        }
    }

    /// <summary>临时改写环境变量，Dispose 时还原，避免污染其他用例。</summary>
    private sealed class EnvScope : IDisposable
    {
        private readonly (string Key, string? Value)[] _saved;

        public EnvScope(string url, string name, string password)
        {
            _saved = Save();
            Environment.SetEnvironmentVariable(RemoteConfig.UrlKey, url);
            Environment.SetEnvironmentVariable(RemoteConfig.UserKey, name);
            Environment.SetEnvironmentVariable(RemoteConfig.PasswordKey, password);
        }

        /// <summary>清空远程三项，模拟单机模式。</summary>
        public static EnvScope Clear()
        {
            var scope = new EnvScope(string.Empty, string.Empty, string.Empty);
            Environment.SetEnvironmentVariable(RemoteConfig.UrlKey, null);
            return scope;
        }

        private static (string, string?)[] Save() =>
        [
            (RemoteConfig.UrlKey, Environment.GetEnvironmentVariable(RemoteConfig.UrlKey)),
            (RemoteConfig.UserKey, Environment.GetEnvironmentVariable(RemoteConfig.UserKey)),
            (RemoteConfig.PasswordKey, Environment.GetEnvironmentVariable(RemoteConfig.PasswordKey)),
        ];

        public void Dispose()
        {
            foreach (var (key, value) in _saved) Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <summary>
    /// 极简假服务器：/login 每次发新 token，/sync 只认当前 token。
    /// 用来验证客户端的 token 缓存与 401 重登，不需要真数据库。
    /// </summary>
    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private string _token = string.Empty;
        private int _issued;
        private readonly List<(string Id, string Title)> _seed = [];

        private FakeServer(WebApplication app) => _app = app;

        public string BaseUrl { get; private set; } = string.Empty;

        public int LoginCount { get; private set; }

        public List<string?> SyncTokens { get; } = [];

        /// <summary>最近一次 /sync 请求里携带的本地改动条数，用来验证"换账号后只拉不推"。</summary>
        public int LastChangeCount { get; private set; }

        /// <summary>服务器上预置一条待办，任何 token 调 /sync 都会把它下发。</summary>
        public void SeedTask(string id, string title) => _seed.Add((id, title));

        public static async Task<FakeServer> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

            var app = builder.Build();
            var server = new FakeServer(app);

            app.MapPost("/login", async (HttpContext ctx) =>
            {
                server.LoginCount++;
                server._token = "tok-" + (++server._issued);
                await ctx.Response.WriteAsJsonAsync(new { token = server._token });
            });

            app.MapPost("/sync", async (HttpContext ctx) =>
            {
                var token = ctx.Request.Headers["X-Token"].ToString();
                server.SyncTokens.Add(token);

                using (var body = await JsonDocument.ParseAsync(ctx.Request.Body))
                {
                    server.LastChangeCount = body.RootElement.TryGetProperty("changes", out var changes)
                        ? changes.GetArrayLength()
                        : 0;
                }

                if (token != server._token)
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                    return;
                }

                await ctx.Response.WriteAsJsonAsync(new
                {
                    server_time = 0,
                    max_rev = 0,
                    accepted = Array.Empty<object>(),
                    rejected = Array.Empty<object>(),
                    tasks = server._seed.Select(t => new
                    {
                        id = t.Id,
                        title = t.Title,
                        repeat_rule = RepeatRules.None,
                        updated_at = 5000L,
                        rev = 1L,
                    }).ToArray(),
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            });

            await app.StartAsync();
            server.BaseUrl = app.Urls.First();
            return server;
        }

        /// <summary>让手上那个 token 失效，模拟服务器改了密码。</summary>
        public void RevokeToken() => _token = "revoked";

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

/// <summary>把改写环境变量的用例排成一队，别让它们互相看到对方的值。</summary>
[CollectionDefinition("remote-env", DisableParallelization = true)]
public sealed class RemoteEnvCollection
{
}
