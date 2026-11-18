using DidaLite.Core.Models;
using DidaLite.Data;

namespace DidaLite.Tests;

/// <summary>
/// 换账号时的本地清空：任务与游标必须清掉（否则会拿旧账号的 rev 去问新账号要增量），
/// 而闹钟属于本机设备设置、不跟着账号走，必须原样保留（否则换个账号就把闹钟清没了）。
/// </summary>
public sealed class LocalStoreAccountSwitchTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public LocalStoreAccountSwitchTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dida-switch-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void ClearSyncedData_RemovesTasksAndCursor_KeepsAlarmsAndAccount()
    {
        var store = NewStore();

        store.Upsert(new TaskItem
        {
            Id = "old-1",
            Title = "上一个账号的待办",
            UpdatedAt = 1,
            RepeatRule = RepeatRules.None,
        });
        store.SetCursor(42);
        store.SetMeta("sync.account", "https://example.com/dida|olduser");
        store.UpsertAlarm(new AlarmItem
        {
            Id = "alarm-1",
            Label = "起床",
            Hour = 7,
            Minute = 0,
            RepeatRule = AlarmItem.Daily,
            Enabled = true,
            UpdatedAt = 1,
        });

        store.ClearSyncedData();

        Assert.Empty(store.GetAll());
        Assert.Equal(0, store.GetCursor());
        Assert.Single(store.GetAlarms());
        // 记的是"上一个账号"，同步时要能据此看出这一步是切换而不是首次运行
        Assert.Equal("https://example.com/dida|olduser", store.GetMeta("sync.account"));
    }

    [Fact]
    public void ClearSyncedData_OnEmptyStore_IsNoOp()
    {
        var store = NewStore();

        store.ClearSyncedData();

        Assert.Empty(store.GetAll());
        Assert.Equal(0, store.GetCursor());
    }
}
