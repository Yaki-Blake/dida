using DidaLite.Core.Models;
using Microsoft.Data.Sqlite;

namespace DidaLite.Data.Sync;

/// <summary>
/// 多用户同步服务端的闹钟库：表名用 <c>alarm_sync</c>，刻意避开桌面端 LocalStore 建的 <c>alarm</c> 表
/// （那张是单用户、id 单主键），两者可能落在同一个 dida.db 里。
///
/// 闹钟不参与任务那条 rev 游标：它不是"逐条增量同步"，而是**电脑端上传一份全量快照**，
/// 服务器整表替换、并给每条发一个新 rev；手机端只改两项（开关、删除），
/// 改完由服务器写回，下一轮电脑端拉取时把这两项带回本地。
/// </summary>
public sealed class LocalAlarmStore : IAlarmStore
{
    private const string Columns =
        "id, label, hour, minute, repeat_rule, once_at, enabled, last_fired_at, deleted, updated_at, rev";

    private readonly string _connectionString;
    private Task? _schema;

    public LocalAlarmStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30,
        }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private async Task EnsureAsync()
    {
        if (_schema is null) _schema = CreateSchemaAsync();
        try { await _schema; }
        catch { _schema = null; throw; }
    }

    private async Task CreateSchemaAsync()
    {
        await using var conn = await OpenAsync(default);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS alarm_sync (
              id            TEXT    NOT NULL,
              user_id       INTEGER NOT NULL DEFAULT 0,
              label         TEXT,
              hour          INTEGER NOT NULL,
              minute        INTEGER NOT NULL,
              repeat_rule   TEXT    NOT NULL DEFAULT 'daily',
              once_at       INTEGER,
              enabled       INTEGER NOT NULL DEFAULT 1,
              last_fired_at INTEGER,
              deleted       INTEGER NOT NULL DEFAULT 0,
              updated_at    INTEGER NOT NULL,
              rev           INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (user_id, id)
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>该账号全部未删除的闹钟，按响铃时刻排序，手机端「闹钟」页直接照着渲染。</summary>
    public async Task<IReadOnlyList<AlarmItem>> GetAlarmsAsync(int userId, CancellationToken ct = default)
    {
        await EnsureAsync();
        await using var conn = await OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {Columns} FROM alarm_sync WHERE user_id = @uid AND deleted = 0 ORDER BY hour, minute";
        cmd.Parameters.AddWithValue("@uid", userId);

        var list = new List<AlarmItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(Read(reader));
        return list;
    }

    /// <summary>
    /// 电脑端上传的全量快照：逐条 upsert，列表里没出现的旧闹钟置墓碑。
    /// 快照为空时不动库——否则一台没配闹钟的电脑登进来就会把别人的闹钟清光。
    /// </summary>
    public async Task ReplaceAsync(int userId, IReadOnlyList<AlarmItem> alarms, CancellationToken ct = default)
    {
        if (alarms.Count == 0) return;

        await EnsureAsync();
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var seq = await NextRevAsync(conn, tx, userId, ct);

        foreach (var alarm in alarms)
            await UpsertAsync(conn, tx, userId, alarm, now, seq++, ct);

        // 电脑上删掉的闹钟要在这里变墓碑，否则手机端永远还看得见。
        // 本次刚写进来的行 updated_at 等于 @now，不会被这一句命中。
        await using (var tomb = conn.CreateCommand())
        {
            tomb.Transaction = tx;
            tomb.CommandText =
                "UPDATE alarm_sync SET deleted = 1, updated_at = @now, rev = @rev " +
                "WHERE user_id = @uid AND deleted = 0 AND updated_at < @now";
            tomb.Parameters.AddWithValue("@now", now);
            tomb.Parameters.AddWithValue("@rev", seq);
            tomb.Parameters.AddWithValue("@uid", userId);
            await tomb.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>手机端的开关 / 删除：只改这两列，改完把 updated_at 顶新，电脑端下次拉取能认出来。</summary>
    public async Task ApplyChangesAsync(int userId, IReadOnlyList<AlarmChange> changes, CancellationToken ct = default)
    {
        if (changes.Count == 0) return;

        await EnsureAsync();
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rev = await NextRevAsync(conn, tx, userId, ct);

        foreach (var change in changes)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE alarm_sync SET " +
                "enabled = COALESCE(@enabled, enabled), " +
                "deleted = COALESCE(@deleted, deleted), " +
                "updated_at = @now, rev = @rev " +
                "WHERE user_id = @uid AND id = @id";
            cmd.Parameters.AddWithValue("@enabled", change.Enabled is bool e ? (e ? 1 : 0) : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@deleted", change.Deleted is bool d ? (d ? 1 : 0) : (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@rev", rev++);
            cmd.Parameters.AddWithValue("@uid", userId);
            cmd.Parameters.AddWithValue("@id", change.Id);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private static async Task<long> NextRevAsync(
        SqliteConnection conn, SqliteTransaction tx, int userId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(rev), 0) + 1 FROM alarm_sync WHERE user_id = @uid";
        cmd.Parameters.AddWithValue("@uid", userId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 1L);
    }

    private static async Task UpsertAsync(
        SqliteConnection conn, SqliteTransaction tx, int userId, AlarmItem alarm, long now, long rev, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO alarm_sync
              (id, user_id, label, hour, minute, repeat_rule, once_at, enabled, last_fired_at, deleted, updated_at, rev)
            VALUES (@id, @uid, @label, @hour, @minute, @rule, @once, @enabled, @fired, 0, @now, @rev)
            ON CONFLICT(user_id, id) DO UPDATE SET
              label = excluded.label,
              hour = excluded.hour,
              minute = excluded.minute,
              repeat_rule = excluded.repeat_rule,
              once_at = excluded.once_at,
              enabled = excluded.enabled,
              last_fired_at = excluded.last_fired_at,
              deleted = 0,
              updated_at = excluded.updated_at,
              rev = excluded.rev;
            """;
        cmd.Parameters.AddWithValue("@id", alarm.Id);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@label", string.IsNullOrEmpty(alarm.Label) ? string.Empty : alarm.Label);
        cmd.Parameters.AddWithValue("@hour", alarm.Hour);
        cmd.Parameters.AddWithValue("@minute", alarm.Minute);
        cmd.Parameters.AddWithValue("@rule", string.IsNullOrEmpty(alarm.RepeatRule) ? AlarmItem.Daily : alarm.RepeatRule);
        cmd.Parameters.AddWithValue("@once", alarm.OnceAt is long at ? at : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@enabled", alarm.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@fired", alarm.LastFiredAt is long fired ? fired : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@rev", rev);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static AlarmItem Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Label = r.IsDBNull(1) ? string.Empty : r.GetString(1),
        Hour = r.GetInt32(2),
        Minute = r.GetInt32(3),
        RepeatRule = r.GetString(4),
        OnceAt = r.IsDBNull(5) ? null : r.GetInt64(5),
        Enabled = r.GetInt32(6) != 0,
        LastFiredAt = r.IsDBNull(7) ? null : r.GetInt64(7),
        Deleted = r.GetInt32(8) != 0,
        UpdatedAt = r.GetInt64(9),
        Rev = r.GetInt64(10),
    };
}
