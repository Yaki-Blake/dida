using DidaLite.Core.Models;
using DidaLite.Core.Recurring;
using DidaLite.Data;

namespace DidaLite.Tests;

/// <summary>
/// 新同步规则：纯修改时间 newer-wins——谁的时间更晚就听谁的，删除也只是带时间戳的普通修改。
/// 云端版本下推到本地的裁决（LocalStore.ApplyServer / ApplyServerNote）。
/// 每个用例一份临时库，不碰用户数据。
/// </summary>
public sealed class ApplyServerRuleTests : IDisposable
{
    private readonly string _dir;
    private readonly LocalStore _store;

    public ApplyServerRuleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dida-apply-server-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new LocalStore(Path.Combine(_dir, "dida.db"));
        _store.Initialize();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* SQLite 句柄未必立刻释放 */ }
    }

    private static TaskItem Task(string id, long at, string title = "任务", bool deleted = false) => new()
    {
        Id = id, Title = title, UpdatedAt = at, Deleted = deleted,
        RepeatRule = RepeatRules.None,
    };

    [Fact]
    public void NewerServerTombstone_DeletesLocal()
    {
        // 云端删除时间更晚 → 删除生效。
        _store.Upsert(Task("t1", 1000, "本地还在"));

        _store.ApplyServer(Task("t1", 9000, "服务端已删", deleted: true));

        var saved = Assert.Single(_store.GetAll(), t => t.Id == "t1");
        Assert.True(saved.Deleted, "云端更晚的删除应覆盖本地");
    }

    [Fact]
    public void OlderServerTombstone_LosesToNewerLocalEdit()
    {
        // 纯时间规则：本地编辑更晚，云端的旧墓碑不能删掉它（删除会被更晚编辑"复活"）。
        _store.Upsert(Task("t1", 9000, "本地的新编辑"));

        _store.ApplyServer(Task("t1", 1000, "服务端旧墓碑", deleted: true));

        var t = Assert.Single(_store.GetAll());
        Assert.False(t.Deleted, "云端更旧的墓碑不应覆盖本地更晚的编辑");
        Assert.Equal("本地的新编辑", t.Title);
    }

    [Fact]
    public void NewerLocalTombstone_NotResurrected_ByOlderServerVersion()
    {
        _store.Upsert(Task("t1", 9000, "本地已删", deleted: true));

        _store.ApplyServer(Task("t1", 1000, "云端的旧版本"));

        var t = Assert.Single(_store.GetAll());
        Assert.True(t.Deleted, "本地更晚的墓碑不应被云端旧版本复活");
    }

    [Fact]
    public void BothAlive_NewerServerWins_OlderServerIgnored_EqualIgnored()
    {
        _store.Upsert(Task("t1", 5000, "本地"));

        _store.ApplyServer(Task("t1", 9000, "云端较新"));
        Assert.Equal("云端较新", Assert.Single(_store.GetAll()).Title);

        _store.ApplyServer(Task("t1", 1000, "云端更旧"));
        Assert.Equal("云端较新", Assert.Single(_store.GetAll()).Title);

        _store.ApplyServer(Task("t1", 9000, "云端同刻不同内容"));
        Assert.Equal("云端较新", Assert.Single(_store.GetAll()).Title); // 相等不动，幂等
    }

    [Fact]
    public void Note_NewerServerTombstone_DeletesLocal()
    {
        _store.UpsertNote(new NoteItem
        {
            Id = "n1", Title = "本地便签", Body = "内容", UpdatedAt = 1000,
        });

        _store.ApplyServerNote(new NoteItem
        {
            Id = "n1", Title = "云端已删", Body = "", UpdatedAt = 9000, Deleted = true,
        });

        Assert.DoesNotContain(_store.GetNotes(), n => n.Id == "n1");
    }

    [Fact]
    public void Note_OlderServerVersion_Ignored()
    {
        _store.UpsertNote(new NoteItem
        {
            Id = "n1", Title = "本地较新", Body = "本地", UpdatedAt = 9000,
        });

        _store.ApplyServerNote(new NoteItem
        {
            Id = "n1", Title = "云端旧版", Body = "旧", UpdatedAt = 1000,
        });

        Assert.Equal("本地较新", Assert.Single(_store.GetNotes()).Title);
    }
}

/// <summary>重复日程展开：设了重复规则后一次生成多条独立日程，日历上真的出现多天。</summary>
public sealed class RecurrenceExpanderTests
{
    private static long Utc(int y, int m, int d, int h = 9) =>
        new DateTimeOffset(y, m, d, h, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private static TaskItem Daily(long start) => new()
    {
        Id = "master", Title = "晨会", StartAt = start,
        DueAt = start + 30 * 60_000, RepeatRule = RepeatRules.Daily,
        UpdatedAt = 1000,
    };

    [Fact]
    public void NonRepeating_StaysSingle()
    {
        var master = Daily(Utc(2026, 11, 20));
        master.RepeatRule = RepeatRules.None;

        var list = RecurrenceExpander.Expand(master, months: 1);

        Assert.Single(list);
    }

    [Fact]
    public void Daily_ExpandsIntoManyIndependentTasks()
    {
        var list = RecurrenceExpander.Expand(Daily(Utc(2026, 11, 20)), months: 1);

        Assert.True(list.Count >= 28, $"一个月应展开出约 30 条，实际 {list.Count}");
        Assert.All(list, t => Assert.Equal(RepeatRules.None, t.RepeatRule));
        Assert.Equal(list.Count, list.Select(t => t.Id).Distinct().Count()); // 每条 id 独立
        Assert.All(list, t => Assert.False(t.Done));
        Assert.All(list, t => Assert.Equal("晨会", t.Title));
    }

    [Fact]
    public void Daily_KeepsDueOffset()
    {
        var list = RecurrenceExpander.Expand(Daily(Utc(2026, 11, 20)), months: 1);

        Assert.All(list, t => Assert.Equal(30 * 60_000, t.DueAt - t.StartAt));
        var dates = list.Select(t => DateTimeOffset.FromUnixTimeMilliseconds(t.StartAt!.Value).UtcDateTime.Date)
            .Distinct().OrderBy(x => x).ToList();
        Assert.Equal(dates.Count, list.Count); // 每天恰好一条
    }

    [Fact]
    public void Weekly_ExpandsWeekly()
    {
        var master = Daily(Utc(2026, 11, 20));
        master.RepeatRule = RepeatRules.Weekly;

        var list = RecurrenceExpander.Expand(master, months: 2);

        var gaps = list.Zip(list.Skip(1), (a, b) => b.StartAt - a.StartAt).ToList();
        Assert.All(gaps, g => Assert.Equal(7 * 24 * 60 * 60_000, g));
    }
}
