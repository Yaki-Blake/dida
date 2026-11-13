using DidaLite.Core.Models;
using MySqlConnector;

namespace DidaLite.Data.Sync;

/// <summary>MySQL 同步权威库：按 rev 增量读、按 updated_at 裁决写。</summary>
public sealed class MySqlTaskStore
{
    private const string Columns =
        "id, title, note, location, start_at, due_at, all_day, remind_at, repeat_rule, done, deleted, updated_at, rev";

    private readonly string _connectionString;

    public MySqlTaskStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    /// <summary>读取 rev 大于游标的全部任务，含墓碑。</summary>
    public async Task<IReadOnlyList<TaskItem>> GetChangedSinceAsync(long sinceRev, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM task WHERE rev > @since ORDER BY rev";
        cmd.Parameters.AddWithValue("@since", sinceRev);

        var rows = new List<TaskItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(Read(reader));
        return rows;
    }

    /// <summary>服务端当前最大 rev，用作客户端游标上限。</summary>
    public async Task<long> GetMaxRevAsync(CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(rev), 0) FROM task";

        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null ? 0 : Convert.ToInt64(value);
    }

    /// <summary>写入一批变更：逐条裁决、分配新 rev，整批一个事务。</summary>
    public async Task<ApplyResult> ApplyAsync(IEnumerable<TaskItem> changes, CancellationToken ct = default)
    {
        var result = new ApplyResult();

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var seq = await ReadSeqAsync(conn, tx, ct);
        var receivedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var change in changes)
        {
            var server = await FindAsync(conn, tx, change.Id, ct);
            if (server is not null && !Wins(change, server))
            {
                result.Rejected.Add(new RejectedChange(change.Id, "stale", server));
                continue;
            }

            seq++;
            change.Rev = seq;
            await UpsertAsync(conn, tx, change, receivedAt, ct);
            result.Accepted.Add(new AcceptedChange(change.Id, seq));
        }

        await WriteSeqAsync(conn, tx, seq, ct);
        await tx.CommitAsync(ct);

        result.MaxRev = seq;
        return result;
    }

    /// <summary>传入版本是否压过服务端：更新时间更晚者胜；同一时刻删除优先；再相同时保留服务端。</summary>
    private static bool Wins(TaskItem incoming, TaskItem server)
    {
        if (incoming.UpdatedAt != server.UpdatedAt) return incoming.UpdatedAt > server.UpdatedAt;
        return incoming.Deleted && !server.Deleted;
    }

    private async Task<TaskItem?> FindAsync(MySqlConnection conn, MySqlTransaction tx, string id, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {Columns} FROM task WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    private async Task UpsertAsync(MySqlConnection conn, MySqlTransaction tx, TaskItem t, long receivedAt,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO task (id, title, note, location, start_at, due_at, all_day, remind_at,
                              repeat_rule, done, deleted, updated_at, received_at, rev)
            VALUES (@id, @title, @note, @location, @startAt, @dueAt, @allDay, @remindAt,
                    @repeatRule, @done, @deleted, @updatedAt, @receivedAt, @rev)
            ON DUPLICATE KEY UPDATE
                title = @title, note = @note, location = @location,
                start_at = @startAt, due_at = @dueAt, all_day = @allDay,
                remind_at = @remindAt, repeat_rule = @repeatRule,
                done = @done, deleted = @deleted,
                updated_at = @updatedAt, received_at = @receivedAt, rev = @rev
            """;

        cmd.Parameters.AddWithValue("@id", t.Id);
        cmd.Parameters.AddWithValue("@title", t.Title);
        cmd.Parameters.AddWithValue("@note", (object?)t.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@location", (object?)t.Location ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@startAt", (object?)t.StartAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dueAt", (object?)t.DueAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@allDay", t.AllDay);
        cmd.Parameters.AddWithValue("@remindAt", (object?)t.RemindAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@repeatRule", t.RepeatRule);
        cmd.Parameters.AddWithValue("@done", t.Done);
        cmd.Parameters.AddWithValue("@deleted", t.Deleted);
        cmd.Parameters.AddWithValue("@updatedAt", t.UpdatedAt);
        cmd.Parameters.AddWithValue("@receivedAt", receivedAt);
        cmd.Parameters.AddWithValue("@rev", t.Rev);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ReadSeqAsync(MySqlConnection conn, MySqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT v FROM sync_meta WHERE k = 'rev_seq' FOR UPDATE";

        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null ? 0 : Convert.ToInt64(value);
    }

    private static async Task WriteSeqAsync(MySqlConnection conn, MySqlTransaction tx, long seq, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO sync_meta (k, v) VALUES ('rev_seq', @v) ON DUPLICATE KEY UPDATE v = @v";
        cmd.Parameters.AddWithValue("@v", seq);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static TaskItem Read(MySqlDataReader r) => new()
    {
        Id = r.GetString(0),
        Title = r.GetString(1),
        Note = r.IsDBNull(2) ? null : r.GetString(2),
        Location = r.IsDBNull(3) ? null : r.GetString(3),
        StartAt = r.IsDBNull(4) ? null : r.GetInt64(4),
        DueAt = r.IsDBNull(5) ? null : r.GetInt64(5),
        AllDay = r.GetBoolean(6),
        RemindAt = r.IsDBNull(7) ? null : r.GetInt64(7),
        RepeatRule = r.IsDBNull(8) ? RepeatRules.None : r.GetString(8),
        Done = r.GetBoolean(9),
        Deleted = r.GetBoolean(10),
        UpdatedAt = r.GetInt64(11),
        Rev = r.GetInt64(12),
    };
}

/// <summary>一批变更的处理结果。</summary>
public sealed class ApplyResult
{
    public long MaxRev { get; set; }
    public List<AcceptedChange> Accepted { get; } = [];
    public List<RejectedChange> Rejected { get; } = [];
}

/// <summary>被接纳的变更及服务端分配的新 rev。</summary>
public sealed record AcceptedChange(string Id, long Rev);

/// <summary>被拒的变更，附带服务端当前版本供客户端覆盖本地。</summary>
public sealed record RejectedChange(string Id, string Reason, TaskItem Server);
