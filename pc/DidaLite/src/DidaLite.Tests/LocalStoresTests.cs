using DidaLite.Data;
using DidaLite.Data.Sync;
using DidaLite.Core.Models;

namespace DidaLite.Tests;

/// <summary>
/// 本机 SQLite 存储测试：任务隔离（按 user_id）、rev 每用户独立、账号隐式注册与 token 独立。
/// 每个用例用独立的临时库，不碰用户数据。
/// </summary>
public sealed class LocalStoresTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly LocalTaskStore _store;

    public LocalStoresTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dida-local-" + Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_dir, "dida.db");
        new LocalStore(_dbPath).Initialize();
        _store = new LocalTaskStore(_dbPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 临时目录 */ }
    }

    private static TaskItem NewTask(string id, long updatedAt, string title = "写作业") => new()
    {
        Id = id,
        Title = title,
        UpdatedAt = updatedAt,
        RepeatRule = RepeatRules.None,
    };

    // ---------- 单用户语义（userId=0）----------

    [Fact]
    public async Task Apply_NewTask_GetsFirstRev_AndReadsBack()
    {
        var result = await _store.ApplyAsync(0, [NewTask("t1", 1000)]);

        Assert.Single(result.Accepted);
        Assert.Equal(1, result.Accepted[0].Rev);
        Assert.Equal(1, result.MaxRev);
        Assert.Empty(result.Rejected);

        var tasks = await _store.GetChangedSinceAsync(0, 0);
        Assert.Single(tasks);
        Assert.Equal("t1", tasks[0].Id);
        Assert.Equal(1, await _store.GetMaxRevAsync(0));
    }

    [Fact]
    public async Task Apply_OlderUpdate_IsRejectedWithServerCopy()
    {
        await _store.ApplyAsync(0, [NewTask("t1", 2000, "新版本")]);
        var result = await _store.ApplyAsync(0, [NewTask("t1", 1000, "旧版本")]);

        Assert.Empty(result.Accepted);
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal("stale", rejected.Reason);
        Assert.Equal("新版本", rejected.Server.Title);
    }

    [Fact]
    public async Task Apply_SameTime_IsAccepted_Idempotently()
    {
        await _store.ApplyAsync(0, [NewTask("t1", 1000)]);
        var result = await _store.ApplyAsync(0, [new TaskItem { Id = "t1", Title = "写作业", UpdatedAt = 1000, Deleted = true }]);

        // 时间相等视为幂等接受（全量快照重传），不当成"被拒"；写入即生效。
        Assert.Single(result.Accepted);
        Assert.True(Assert.Single(await _store.GetChangedSinceAsync(0, 0)).Deleted, "相等时间的墓碑应幂等写入");
    }

    // ---------- 新裁决规则：纯修改时间 newer-wins（删除也只是普通修改） ----------

    [Fact]
    public async Task Apply_NewerLocalDelete_BeatsOlderServerEdit()
    {
        // 服务端有一条更旧的编辑（t=1000），本地在更晚时刻删了它（t=5000）。
        // 纯时间规则：更晚的删除胜出。
        await _store.ApplyAsync(0, [NewTask("t1", 1000, "服务端的旧编辑")]);
        var localDelete = new TaskItem
        {
            Id = "t1", Title = "写作业", UpdatedAt = 5000, Deleted = true,
            RepeatRule = RepeatRules.None,
        };

        var result = await _store.ApplyAsync(0, [localDelete]);

        Assert.Single(result.Accepted);
        Assert.Empty(result.Rejected);
        var saved = Assert.Single(await _store.GetChangedSinceAsync(0, 0));
        Assert.True(saved.Deleted, "更晚的本地删除应覆盖服务端更旧的编辑");
    }

    [Fact]
    public async Task Apply_OlderLocalDelete_RejectedByNewerServerResurrect()
    {
        // 服务端已删（t=5000），本地拿一条更早的删除/编辑来（t=1000）：更旧的版本应被拒。
        await _store.ApplyAsync(0, [new TaskItem
        {
            Id = "t1", Title = "写作业", UpdatedAt = 5000, Deleted = true,
            RepeatRule = RepeatRules.None,
        }]);
        var older = NewTask("t1", 1000, "更旧的编辑想复活");

        var result = await _store.ApplyAsync(0, [older]);

        Assert.Empty(result.Accepted);
        var rejected = Assert.Single(result.Rejected);
        Assert.True(rejected.Server.Deleted, "被拒时回传服务端较新的版本");
        var saved = Assert.Single(await _store.GetChangedSinceAsync(0, 0));
        Assert.True(saved.Deleted);
    }

    [Fact]
    public async Task Apply_BothAlive_NewerEditWins()
    {
        // 两边都活着、没有删除：仍然按更新时间，新者胜（用户确认的编辑冲突规则）。
        await _store.ApplyAsync(0, [NewTask("t1", 1000, "旧标题")]);
        var result = await _store.ApplyAsync(0, [NewTask("t1", 9000, "新标题")]);

        Assert.Single(result.Accepted);
        Assert.Equal("新标题", Assert.Single(await _store.GetChangedSinceAsync(0, 0)).Title);
    }

    [Fact]
    public async Task Apply_LocalNew_IdAbsentOnServer_IsAccepted()
    {
        // 新增本地优先：服务端没有这个 id，直接接受，本地新建的任务总能推上去。
        var result = await _store.ApplyAsync(0, [NewTask("brand-new", 1000, "本地新建")]);

        Assert.Single(result.Accepted);
        Assert.Equal("本地新建", Assert.Single(await _store.GetChangedSinceAsync(0, 0)).Title);
    }

    [Fact]
    public async Task MaxRev_KeepsGrowing_AfterEverythingDeleted()
    {
        var applied = await _store.ApplyAsync(0, [NewTask("t1", 1000)]);
        var first = applied.MaxRev;
        await _store.ApplyAsync(0, [new TaskItem { Id = "t1", Title = "写作业", UpdatedAt = 2000, Deleted = true }]);
        Assert.Equal(first + 1, await _store.GetMaxRevAsync(0));
    }

    // ---------- 多用户隔离（本次改造核心）----------

    [Fact]
    public async Task Tasks_AreIsolated_BetweenUsers()
    {
        await _store.ApplyAsync(1, [NewTask("a1", 1000, "甲的任务")]);
        await _store.ApplyAsync(2, [NewTask("b1", 1000, "乙的任务")]);

        var u1 = await _store.GetChangedSinceAsync(1, 0);
        var u2 = await _store.GetChangedSinceAsync(2, 0);

        Assert.Single(u1);
        Assert.Equal("甲的任务", u1[0].Title);
        Assert.Single(u2);
        Assert.Equal("乙的任务", u2[0].Title);
    }

    [Fact]
    public async Task RevSequence_IsIndependent_PerUser()
    {
        // 用户1写 3 条，用户2只写 1 条；两人的游标互不影响
        await _store.ApplyAsync(1, [NewTask("a1", 1000), NewTask("a2", 1001), NewTask("a3", 1002)]);
        var u2Apply = await _store.ApplyAsync(2, [NewTask("b1", 1000)]);

        Assert.Equal(3, await _store.GetMaxRevAsync(1));
        Assert.Equal(1, await _store.GetMaxRevAsync(2));
        Assert.Equal(1, u2Apply.MaxRev);
    }

    [Fact]
    public async Task SameTaskId_DifferentUsers_DoNotOverwrite()
    {
        // 即使两个用户用了相同的任务 id，也各存各的
        await _store.ApplyAsync(1, [NewTask("same-id", 1000, "甲的")]);
        await _store.ApplyAsync(2, [NewTask("same-id", 1000, "乙的")]);

        Assert.Equal("甲的", Assert.Single(await _store.GetChangedSinceAsync(1, 0)).Title);
        Assert.Equal("乙的", Assert.Single(await _store.GetChangedSinceAsync(2, 0)).Title);
    }

    // ---------- 账号：隐式注册 + token 独立 ----------

    [Fact]
    public async Task Login_NewUser_AutoRegisters_AndWrongPasswordFails()
    {
        var users = new LocalUserStore(_dbPath);

        Assert.Equal(0, await users.CountAsync());

        var first = await users.LoginAsync("小王", "123456");
        Assert.NotNull(first);
        Assert.Equal(1, await users.CountAsync());
        Assert.Equal(first.Value.UserId, (await users.ResolveTokenAsync(first.Value.Token))!.Value);

        // 同名不同密码 → 登录失败，且不会建第二个号
        Assert.Null(await users.LoginAsync("小王", "wrong"));
        Assert.Equal(1, await users.CountAsync());
    }

    [Fact]
    public async Task Tokens_AreIndependent_PerUser_AndPerDevice()
    {
        var users = new LocalUserStore(_dbPath);
        var a1 = (await users.LoginAsync("甲", "pw-a"))!.Value;
        var b = (await users.LoginAsync("乙", "pw-b"))!.Value;

        Assert.NotEqual(a1.UserId, b.UserId);

        // 甲的另一台设备（手机）再登录：拿到另一枚 token
        var a2 = (await users.LoginAsync("甲", "pw-a"))!.Value;
        Assert.NotEqual(a1.Token, a2.Token);

        // 两台设备同时在线，互不顶号——这是「电脑一开机手机就掉线」的根因所在
        Assert.Equal(a1.UserId, (await users.ResolveTokenAsync(a1.Token))!.Value);
        Assert.Equal(a2.UserId, (await users.ResolveTokenAsync(a2.Token))!.Value);
        Assert.Equal(b.UserId, (await users.ResolveTokenAsync(b.Token))!.Value);
    }

    [Fact]
    public async Task LegacyToken_InAppUserRow_StillResolves()
    {
        // 老版本把 token 写在 app_user.token 上，升级后这些设备不该被迫重新登录
        var users = new LocalUserStore(_dbPath);
        var first = (await users.LoginAsync("老王", "pw"))!.Value;

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var insert = conn.CreateCommand();
            insert.CommandText = "UPDATE app_user SET token = 'legacy-token-abc' WHERE id = @id";
            insert.Parameters.AddWithValue("@id", first.UserId);
            insert.ExecuteNonQuery();
        }

        Assert.Equal(first.UserId, (await users.ResolveTokenAsync("legacy-token-abc"))!.Value);
    }

    [Fact]
    public async Task SetUser_CanResetPassword_AndInvalidatesThatUsersToken()
    {
        var users = new LocalUserStore(_dbPath);
        var a = (await users.LoginAsync("甲", "old"))!.Value;
        var b = (await users.LoginAsync("乙", "pw-b"))!.Value;

        Assert.False(await users.SaveAsync("甲", "new")); // 已存在账号改密 → 返回 false
        Assert.Null(await users.ResolveTokenAsync(a.Token));
        Assert.NotNull(await users.ResolveTokenAsync(b.Token)); // 不影响别人
    }

    // ---------- 老库兼容（升级场景）----------

    [Fact]
    public void Initialize_OldSchemaWithoutUserId_DoesNotThrow_AndBackfillsColumn()
    {
        // 模拟改造前的旧库：task 表没有 user_id，也没有 idx_task_user_rev 索引
        var oldDb = Path.Combine(_dir, "old.db");
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={oldDb}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE task (
                  id TEXT PRIMARY KEY, title TEXT NOT NULL, note TEXT, location TEXT,
                  start_at INTEGER, due_at INTEGER, remind_at INTEGER,
                  repeat_rule TEXT NOT NULL DEFAULT 'none',
                  done INTEGER NOT NULL DEFAULT 0, deleted INTEGER NOT NULL DEFAULT 0,
                  updated_at INTEGER NOT NULL, rev INTEGER NOT NULL DEFAULT 0,
                  dirty INTEGER NOT NULL DEFAULT 0, reminded_at INTEGER,
                  all_day INTEGER NOT NULL DEFAULT 0
                );
                INSERT INTO task(id, title, updated_at) VALUES ('old-1', '老任务', 1000);
                """;
            cmd.ExecuteNonQuery();
        }

        // 新版 Initialize 必须先补列、再建索引，不能抛异常
        var store = new LocalStore(oldDb);
        store.Initialize();

        // 老数据仍在，且被补上 user_id=0 与待上传标记
        var all = store.GetAll();
        Assert.Single(all);
        Assert.Equal("老任务", all[0].Title);
    }
}
