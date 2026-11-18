using DidaLite.Core.Models;
using DidaLite.Data;
using DidaLite.Data.Sync;

namespace DidaLite.Tests;

/// <summary>
/// 闹钟上云的那条链路：电脑端整批上传 → 服务器按账号存 → 手机端改开关/删除后回传 → 电脑端并回本地。
/// 这几步任何一处错位，用户看到的就是「手机里闹钟不见了」或者「电脑上删掉的闹钟又冒出来」。
/// </summary>
public sealed class LocalAlarmSyncTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public LocalAlarmSyncTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dida-alarm-" + Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_dir, "dida.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 临时目录 */ }
    }

    private static AlarmItem Alarm(string id, int hour, string label = "闹钟") => new()
    {
        Id = id,
        Label = label,
        Hour = hour,
        Minute = 0,
        RepeatRule = AlarmItem.Daily,
        Enabled = true,
        UpdatedAt = 1000,
    };

    [Fact]
    public async Task Replace_then_get_returns_uploaded_alarms()
    {
        var store = new LocalAlarmStore(_dbPath);

        await store.ReplaceAsync(1, [Alarm("a", 7, "起床"), Alarm("b", 22, "睡觉")]);

        var alarms = await store.GetAlarmsAsync(1);
        Assert.Equal(2, alarms.Count);
        Assert.Equal(["起床", "睡觉"], alarms.Select(a => a.Label));
    }

    [Fact]
    public async Task Replace_tombstones_alarms_missing_from_snapshot()
    {
        var store = new LocalAlarmStore(_dbPath);
        await store.ReplaceAsync(1, [Alarm("a", 7), Alarm("b", 22)]);

        // 电脑上删掉了 b，下一次上传只剩 a
        await store.ReplaceAsync(1, [Alarm("a", 7)]);

        var alarms = await store.GetAlarmsAsync(1);
        Assert.Single(alarms);
        Assert.Equal("a", alarms[0].Id);
    }

    [Fact]
    public async Task Empty_snapshot_does_not_wipe_existing_alarms()
    {
        var store = new LocalAlarmStore(_dbPath);
        await store.ReplaceAsync(1, [Alarm("a", 7)]);

        // 一台没配闹钟的电脑登进来（或者老客户端不带这个字段），不该把别人的闹钟清光
        await store.ReplaceAsync(1, []);

        Assert.Single(await store.GetAlarmsAsync(1));
    }

    [Fact]
    public async Task ApplyChanges_toggles_enabled_and_deletes()
    {
        var store = new LocalAlarmStore(_dbPath);
        await store.ReplaceAsync(1, [Alarm("a", 7), Alarm("b", 22)]);

        await store.ApplyChangesAsync(1, [new AlarmChange { Id = "a", Enabled = false }]);

        var alarms = await store.GetAlarmsAsync(1);
        Assert.False(alarms.Single(x => x.Id == "a").Enabled);
        Assert.True(alarms.Single(x => x.Id == "b").Enabled);

        await store.ApplyChangesAsync(1, [new AlarmChange { Id = "b", Deleted = true }]);
        Assert.Single(await store.GetAlarmsAsync(1));
    }

    [Fact]
    public async Task Alarms_are_isolated_per_user()
    {
        var store = new LocalAlarmStore(_dbPath);
        await store.ReplaceAsync(1, [Alarm("a", 7)]);
        await store.ReplaceAsync(2, [Alarm("a", 9), Alarm("c", 21)]);

        // 同一个 id 在两个账号下互不影响
        Assert.Equal(7, (await store.GetAlarmsAsync(1)).Single().Hour);
        Assert.Equal(2, (await store.GetAlarmsAsync(2)).Count);
    }

    [Fact]
    public void Merge_applies_phone_side_changes_but_never_adds_unknown_alarms()
    {
        var local = new LocalStore(_dbPath);
        local.Initialize();

        local.UpsertAlarm(new AlarmItem
        {
            Id = "a", Hour = 7, RepeatRule = AlarmItem.Daily, Enabled = true, UpdatedAt = 1000,
        });

        // 服务器说：a 被手机关掉了（更新），z 是别的设备残留（本地没有）
        var applied = local.MergeAlarmsFromServer(
        [
            new AlarmItem
            {
                Id = "a", Hour = 7, RepeatRule = AlarmItem.Daily, Enabled = false, UpdatedAt = 2000, Rev = 9,
            },
            new AlarmItem
            {
                Id = "z", Hour = 6, RepeatRule = AlarmItem.Daily, Enabled = true, UpdatedAt = 2000, Rev = 10,
            },
        ]);

        Assert.Equal(1, applied);

        var alarms = local.GetAlarms();
        Assert.Single(alarms);                       // z 没有被塞进来
        Assert.False(alarms[0].Enabled);             // a 的开关跟着手机变了
        Assert.False(alarms[0].Dirty);               // 这次改动来自云端，不需要再推上去
    }

    [Fact]
    public void Merge_ignores_stale_server_copy()
    {
        var local = new LocalStore(_dbPath);
        local.Initialize();

        local.UpsertAlarm(new AlarmItem
        {
            Id = "a", Hour = 7, RepeatRule = AlarmItem.Daily, Enabled = true, UpdatedAt = 5000,
        });

        var applied = local.MergeAlarmsFromServer(
        [
            new AlarmItem
            {
                Id = "a", Hour = 7, RepeatRule = AlarmItem.Daily, Enabled = false, UpdatedAt = 1000, Rev = 3,
            },
        ]);

        Assert.Equal(0, applied);
        Assert.True(local.GetAlarms()[0].Enabled);
    }
}
