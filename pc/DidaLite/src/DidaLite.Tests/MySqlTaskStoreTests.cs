using DidaLite.Core;
using DidaLite.Core.Models;
using DidaLite.Data.Sync;
using MySqlConnector;

namespace DidaLite.Tests;

/// <summary>MySQL 访问层集成测试：需要本机 MySQL84 已启动且已执行 sql\schema.sql。</summary>
public sealed class MySqlTaskStoreTests : IAsyncLifetime
{
    private readonly string _connectionString;
    private readonly MySqlTaskStore _store;
    private readonly List<string> _ids = [];

    public MySqlTaskStoreTests()
    {
        EnvFile.Load();
        _connectionString = EnvFile.Get("DIDA_DB_CONNECTION")
                            ?? throw new InvalidOperationException(@"缺少 DIDA_DB_CONNECTION，检查 pc\DidaLite\.env");
        _store = new MySqlTaskStore(_connectionString);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>测试数据物理删掉，不污染真实数据、也不留墓碑。</summary>
    public async Task DisposeAsync()
    {
        if (_ids.Count == 0) return;

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM task WHERE id IN ({string.Join(",", _ids.Select((_, i) => $"@p{i}"))})";
        for (var i = 0; i < _ids.Count; i++) cmd.Parameters.AddWithValue($"@p{i}", _ids[i]);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Apply_NewTask_IsAcceptedAndReadBack()
    {
        var task = new TaskItem
        {
            Title = "写周报",
            Note = "包含本周进度",
            Location = "工位",
            DueAt = 1762790400000,
            AllDay = true,
            UpdatedAt = Now(),
        };
        _ids.Add(task.Id);

        var result = await _store.ApplyAsync([task]);

        Assert.Single(result.Accepted);
        Assert.Equal(task.Id, result.Accepted[0].Id);
        Assert.True(result.Accepted[0].Rev > 0);

        var rows = await _store.GetChangedSinceAsync(0);
        var saved = Assert.Single(rows, r => r.Id == task.Id);
        Assert.Equal("写周报", saved.Title);
        Assert.Equal("包含本周进度", saved.Note);
        Assert.Equal("工位", saved.Location);
        Assert.Equal(1762790400000, saved.DueAt);
        Assert.True(saved.AllDay);
        Assert.False(saved.Deleted);
        Assert.Equal(result.Accepted[0].Rev, saved.Rev);
    }

    [Fact]
    public async Task Apply_LaterUpdate_Wins()
    {
        var (id, _) = await InsertAsync("初版标题");

        var edit = new TaskItem { Id = id, Title = "改后的标题", UpdatedAt = Now() + 1000 };
        var result = await _store.ApplyAsync([edit]);

        Assert.Single(result.Accepted);
        AssertTitle(await _store.GetChangedSinceAsync(0), id, "改后的标题");
    }

    [Fact]
    public async Task Apply_EarlierUpdate_IsRejected()
    {
        var (id, _) = await InsertAsync("服务端较新");

        var stale = new TaskItem { Id = id, Title = "过期的本地改法", UpdatedAt = Now() - 60_000 };
        var result = await _store.ApplyAsync([stale]);

        Assert.Empty(result.Accepted);
        var rejected = Assert.Single(result.Rejected);
        Assert.Equal("stale", rejected.Reason);
        Assert.Equal("服务端较新", rejected.Server.Title);

        // 服务端内容不能被过期版本覆盖
        AssertTitle(await _store.GetChangedSinceAsync(0), id, "服务端较新");
    }

    [Fact]
    public async Task Apply_TombstoneWinsOnEqualTimestamp()
    {
        var (id, _) = await InsertAsync("准备删除");

        var server = Assert.Single(await _store.GetChangedSinceAsync(0), r => r.Id == id);
        var tombstone = new TaskItem { Id = id, Title = "准备删除", Deleted = true, UpdatedAt = server.UpdatedAt };
        var result = await _store.ApplyAsync([tombstone]);

        Assert.Single(result.Accepted);

        var after = Assert.Single(await _store.GetChangedSinceAsync(0), r => r.Id == id);
        Assert.True(after.Deleted);
    }

    [Fact]
    public async Task GetChangedSince_ReturnsOnlyNewerRevs()
    {
        var (olderId, olderRev) = await InsertAsync("游标之前");
        var (newerId, _) = await InsertAsync("游标之后");

        var rows = await _store.GetChangedSinceAsync(olderRev);

        Assert.Contains(rows, r => r.Id == newerId);
        Assert.DoesNotContain(rows, r => r.Id == olderId);
    }

    private async Task<(string Id, long Rev)> InsertAsync(string title)
    {
        var task = new TaskItem { Title = title, UpdatedAt = Now() };
        _ids.Add(task.Id);

        var result = await _store.ApplyAsync([task]);
        Assert.Single(result.Accepted);
        return (task.Id, task.Rev);
    }

    private static void AssertTitle(IReadOnlyList<TaskItem> rows, string id, string expectedTitle)
    {
        var saved = Assert.Single(rows, r => r.Id == id);
        Assert.Equal(expectedTitle, saved.Title);
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
