using DidaLite.Core.Models;
using DidaLite.Data;

namespace DidaLite.Tests;

/// <summary>
/// 云端版本下推到本地的裁决（LocalStore.ApplyServer / ApplyServerNote）。
/// 新规则：删除墓碑绝对优先，本地墓碑不被复活，两边都活着才按更新时间。
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
    public void ServerTombstone_DeletesLocal_EvenWhenLocalEditIsNewer()
    {
        // 本地有一条更晚的编辑（t=9000），云端墓碑更旧（t=1000）。
        // 删除绝对优先：云端墓碑必须删掉本地任务，否则"另一端删不掉"。
        _store.Upsert(Task("t1", 9000, "本地的新编辑"));

        _store.ApplyServer(Task("t1", 1000, "服务端已删", deleted: true));

        // GetAll 含墓碑行（UI 层再过滤），这里断言该任务已被打上删除标记。
        var saved = Assert.Single(_store.GetAll(), t => t.Id == "t1");
        Assert.True(saved.Deleted, "云端墓碑应删除本地任务，哪怕本地编辑时间更晚");
    }

    [Fact]
    public void LocalTombstone_NotResurrected_ByOlderServerVersion()
    {
        // 本地已删（墓碑），云端还留着更旧的版本：不能复活本地的删除。
        _store.Upsert(Task("t1", 5000, "本地已删", deleted: true));

        _store.ApplyServer(Task("t1", 1000, "云端的旧版本"));

        var t = Assert.Single(_store.GetAll());
        Assert.Equal("t1", t.Id);
        Assert.True(t.Deleted, "本地墓碑不应被服务端旧版本复活");
    }

    [Fact]
    public void BothAlive_NewerServerWins_OlderServerIgnored()
    {
        _store.Upsert(Task("t1", 5000, "本地"));

        _store.ApplyServer(Task("t1", 9000, "云端较新"));
        Assert.Equal("云端较新", Assert.Single(_store.GetAll()).Title);

        _store.ApplyServer(Task("t1", 1000, "云端更旧"));
        Assert.Equal("云端较新", Assert.Single(_store.GetAll()).Title);
    }

    [Fact]
    public void Note_ServerTombstone_DeletesLocal()
    {
        var local = new NoteItem
        {
            Id = "n1", Title = "本地便签", Body = "内容",
            UpdatedAt = 9000,
        };
        _store.UpsertNote(local);

        var serverDelete = new NoteItem
        {
            Id = "n1", Title = "云端已删", Body = "",
            UpdatedAt = 1000, Deleted = true,
        };
        _store.ApplyServerNote(serverDelete);

        Assert.DoesNotContain(_store.GetNotes(), n => n.Id == "n1");
    }
}
