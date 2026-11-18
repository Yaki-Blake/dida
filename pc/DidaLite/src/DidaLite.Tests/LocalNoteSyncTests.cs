using DidaLite.Core.Models;
using DidaLite.Data;
using DidaLite.Data.Sync;

namespace DidaLite.Tests;

/// <summary>
/// 便签同步的关键不变量：
/// ① 两端都能改，冲突靠 updated_at 裁决，本地未推的改动不能被远端旧版本盖掉；
/// ② 删除走墓碑，否则对面永远删不掉；
/// ③ 便签游标与任务游标互不干扰（各自独立 rev 空间）。
/// </summary>
public sealed class LocalNoteSyncTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public LocalNoteSyncTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dida-note-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "dida.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 临时目录 */ }
    }

    private LocalStore NewStore()
    {
        var store = new LocalStore(_dbPath);
        store.Initialize();
        return store;
    }

    private static NoteItem Note(string id, long updatedAt, string title = "便签", string body = "") => new()
    {
        Id = id,
        Title = title,
        Body = body,
        UpdatedAt = updatedAt,
        Dirty = true,
    };

    [Fact]
    public void Upsert_marks_row_dirty_and_lists_pinned_first()
    {
        var store = NewStore();

        store.UpsertNote(Note("a", 1000, "先写的"));
        var pinned = Note("b", 2000, "置顶的");
        pinned.Pinned = true;
        store.UpsertNote(pinned);

        var notes = store.GetNotes();
        Assert.Equal(["置顶的", "先写的"], notes.Select(n => n.Title));
        Assert.Equal(2, store.GetDirtyNotes().Count);
    }

    [Fact]
    public void Mark_synced_clears_dirty_only_when_content_did_not_change()
    {
        var store = NewStore();
        store.UpsertNote(Note("a", 1000));

        // 推送途中又改了一次：updated_at 已变，不该清 dirty
        store.NoteMarkSynced("a", 7, sentUpdatedAt: 900);
        Assert.Single(store.GetDirtyNotes());

        store.NoteMarkSynced("a", 7, sentUpdatedAt: 1000);
        Assert.Empty(store.GetDirtyNotes());
        Assert.Equal(7, store.GetNote("a")!.Rev);
    }

    [Fact]
    public void Server_note_wins_only_when_newer()
    {
        var store = NewStore();
        var local = Note("a", 5000, "本地新写的");
        store.UpsertNote(local);

        // 服务器上那条更旧：本地不动
        var stale = Note("a", 4000, "服务器旧版本");
        stale.Dirty = false;
        Assert.False(store.ApplyServerNote(stale));
        Assert.Equal("本地新写的", store.GetNote("a")!.Title);

        // 服务器上那条更新（例如手机上改过）：覆盖本地
        var fresh = Note("a", 6000, "手机上改的");
        fresh.Dirty = false;
        Assert.True(store.ApplyServerNote(fresh));
        Assert.Equal("手机上改的", store.GetNote("a")!.Title);
        Assert.Empty(store.GetDirtyNotes());
    }

    [Fact]
    public void Tombstone_from_server_removes_note_from_list()
    {
        var store = NewStore();
        store.UpsertNote(Note("a", 1000));

        var tomb = new NoteItem { Id = "a", Deleted = true, UpdatedAt = 2000 };
        Assert.True(store.ApplyServerNote(tomb));

        Assert.Empty(store.GetNotes());
        // 服务端来的墓碑是已同步状态，本地不需要再推一次
        Assert.Empty(store.GetDirtyNotes());
    }

    [Fact]
    public void Note_cursor_is_independent_from_task_cursor()
    {
        var store = NewStore();

        store.SetCursor(92);
        store.SetNoteCursor(3);

        Assert.Equal(92, store.GetCursor());
        Assert.Equal(3, store.GetNoteCursor());

        // 换账号清空：两个游标都要归零，否则会拿旧账号的 rev 去问新账号要增量
        store.ClearSyncedData();
        Assert.Equal(0, store.GetCursor());
        Assert.Equal(0, store.GetNoteCursor());
        Assert.Empty(store.GetNotes());
    }

    [Fact]
    public async Task Server_store_applies_notes_per_account_with_own_rev_space()
    {
        var server = new LocalNoteStore(_dbPath);

        var first = Note("a", 1000, "电脑写的");
        var apply = await server.ApplyAsync(1, [first]);
        Assert.Single(apply.Accepted);

        // 另一账号看不到、也不受影响
        var other = await server.GetChangedSinceAsync(2, 0);
        Assert.Empty(other);

        // 同账号第二轮：手机改了更晚的版本，服务端接受并给新 rev
        var second = Note("a", 2000, "手机改的");
        var apply2 = await server.ApplyAsync(1, [second]);
        Assert.Single(apply2.Accepted);
        Assert.True(apply2.Accepted[0].Rev > apply.Accepted[0].Rev);

        var all = await server.GetChangedSinceAsync(1, 0);
        Assert.Single(all);
        Assert.Equal("手机改的", all[0].Title);
    }

    [Fact]
    public async Task Server_store_keeps_newer_version_when_older_arrives()
    {
        var server = new LocalNoteStore(_dbPath);

        var first = await server.ApplyAsync(1, [Note("a", 5000, "新的")]);
        var firstRev = first.Accepted[0].Rev;

        // 老版本后到：服务端保留新的，不接受这一条，但会换一个新 rev
        // 让它在同轮响应里回传，客户端据此把本地纠正过来，不会每轮反复推。
        var apply = await server.ApplyAsync(1, [Note("a", 4000, "旧的")]);
        Assert.Empty(apply.Accepted);

        var all = await server.GetChangedSinceAsync(1, 0);
        Assert.Single(all);
        Assert.Equal("新的", all[0].Title);
        Assert.True(all[0].Rev > firstRev, "被压过的条目应换到新 rev，才能在增量里回传");
    }
}
