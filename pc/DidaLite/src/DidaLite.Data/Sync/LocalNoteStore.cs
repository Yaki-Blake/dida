using DidaLite.Core.Models;
using Microsoft.Data.Sqlite;

namespace DidaLite.Data.Sync;

/// <summary>
/// 同步服务端的便签库。与任务一样是增量同步，但**发号器与游标完全独立**
/// （meta 键 note_seq:{uid}），这样便签的改动不会推高任务的 rev、把别人的待办重复拉一遍。
/// 表名用 note_sync，避开桌面端 LocalStore 的单用户 note 表：两者可能共用同一个库文件。
/// </summary>
public sealed class LocalNoteStore : INoteStore
{
    private const string Columns = "id, title, body, pinned, deleted, updated_at, rev";

    private readonly string _connectionString;
    private Task? _schema;

    public LocalNoteStore(string dbPath)
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
            CREATE TABLE IF NOT EXISTS note_sync (
              id          TEXT    NOT NULL,
              user_id     INTEGER NOT NULL DEFAULT 0,
              title       TEXT    NOT NULL DEFAULT '',
              body        TEXT    NOT NULL DEFAULT '',
              pinned      INTEGER NOT NULL DEFAULT 0,
              deleted     INTEGER NOT NULL DEFAULT 0,
              updated_at  INTEGER NOT NULL,
              rev         INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY (user_id, id)
            );
            CREATE INDEX IF NOT EXISTS idx_note_sync_user_rev ON note_sync(user_id, rev);
            -- 发号器存在 meta 里；单独跑这个库（测试 / 只开便签表）时也得能建出来
            CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT NOT NULL);
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>该用户 rev 大于游标的便签，含墓碑。</summary>
    public async Task<IReadOnlyList<NoteItem>> GetChangedSinceAsync(int userId, long sinceRev, CancellationToken ct = default)
    {
        await EnsureAsync();
        await using var conn = await OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM note_sync WHERE user_id = @uid AND rev > @since ORDER BY rev";
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@since", sinceRev);

        var rows = new List<NoteItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(Read(reader));
        return rows;
    }

    public async Task<long> GetMaxRevAsync(int userId, CancellationToken ct = default)
    {
        await EnsureAsync();
        await using var conn = await OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(rev), 0) FROM note_sync WHERE user_id = @uid";
        cmd.Parameters.AddWithValue("@uid", userId);

        var max = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        return Math.Max(max, await ReadSeqAsync(conn, null, userId, ct));
    }

    /// <summary>
    /// 写入该用户的便签改动，整批一个事务。
    /// 裁决规则与任务一致：updated_at 更晚者胜，同一时刻删除优先。
    /// 被服务端版本压过的那条**故意给它换一个新 rev**——这样同一轮响应就会把它带回去，
    /// 客户端立刻被纠正成本地不再 dirty，不会出现「每轮都推、每轮都输」的死循环。
    /// </summary>
    public async Task<ApplyResult> ApplyAsync(int userId, IEnumerable<NoteItem> changes, CancellationToken ct = default)
    {
        await EnsureAsync();
        var pending = changes as IList<NoteItem> ?? changes.ToList();

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ApplyOnceAsync(userId, pending, ct);
            }
            catch (SqliteException ex) when (attempt == 0 && IsBusy(ex))
            {
                await Task.Delay(150, ct);
            }
        }
    }

    private async Task<ApplyResult> ApplyOnceAsync(int userId, IList<NoteItem> changes, CancellationToken ct)
    {
        var result = new ApplyResult();

        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var seq = await ReadSeqAsync(conn, tx, userId, ct);
        var existing = await LoadUserNotesAsync(conn, tx, userId, ct);

        foreach (var change in changes)
        {
            existing.TryGetValue(change.Id, out var server);

            if (server is not null && !Wins(change, server))
            {
                // 服务端那份胜出：换新 rev 让它随本轮响应回传，客户端随后覆盖本地
                seq++;
                await TouchRevAsync(conn, tx, userId, change.Id, seq, ct);
                continue;
            }

            seq++;
            change.Rev = seq;
            await UpsertAsync(conn, tx, userId, change, ct);
            existing[change.Id] = change;
            result.Accepted.Add(new AcceptedChange(change.Id, seq));
        }

        await WriteSeqAsync(conn, tx, userId, seq, ct);
        await tx.CommitAsync(ct);

        result.MaxRev = seq;
        return result;
    }

    /// <summary>
    /// 与任务一致的裁决：删除墓碑绝对优先（谁删了听谁的），两边都活着才按更新时间新者胜。
    /// </summary>
    private static bool Wins(NoteItem incoming, NoteItem server)
    {
        var incomingDeleted = incoming.Deleted;
        var serverDeleted = server.Deleted;

        if (incomingDeleted || serverDeleted) return incomingDeleted;

        return incoming.UpdatedAt != server.UpdatedAt && incoming.UpdatedAt > server.UpdatedAt;
    }

    private static bool IsBusy(SqliteException ex) =>
        ex.SqliteErrorCode is 5 or 6 || ex.Message.Contains("locked", StringComparison.OrdinalIgnoreCase);

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 10000;";
        await pragma.ExecuteNonQueryAsync(ct);
        return conn;
    }

    private static async Task<Dictionary<string, NoteItem>> LoadUserNotesAsync(
        SqliteConnection conn, SqliteTransaction tx, int userId, CancellationToken ct)
    {
        var map = new Dictionary<string, NoteItem>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {Columns} FROM note_sync WHERE user_id = @uid";
        cmd.Parameters.AddWithValue("@uid", userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var note = Read(reader);
            map[note.Id] = note;
        }
        return map;
    }

    private static async Task UpsertAsync(
        SqliteConnection conn, SqliteTransaction tx, int userId, NoteItem n, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO note_sync (id, user_id, title, body, pinned, deleted, updated_at, rev)
            VALUES (@id, @uid, @title, @body, @pinned, @deleted, @updatedAt, @rev)
            ON CONFLICT(user_id, id) DO UPDATE SET
                title = excluded.title, body = excluded.body, pinned = excluded.pinned,
                deleted = excluded.deleted, updated_at = excluded.updated_at, rev = excluded.rev
            """;
        cmd.Parameters.AddWithValue("@id", n.Id);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@title", n.Title);
        cmd.Parameters.AddWithValue("@body", n.Body);
        cmd.Parameters.AddWithValue("@pinned", n.Pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("@deleted", n.Deleted ? 1 : 0);
        cmd.Parameters.AddWithValue("@updatedAt", n.UpdatedAt);
        cmd.Parameters.AddWithValue("@rev", n.Rev);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task TouchRevAsync(
        SqliteConnection conn, SqliteTransaction tx, int userId, string id, long rev, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE note_sync SET rev = @rev WHERE user_id = @uid AND id = @id";
        cmd.Parameters.AddWithValue("@rev", rev);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ReadSeqAsync(
        SqliteConnection conn, SqliteTransaction? tx, int userId, CancellationToken ct)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT v FROM meta WHERE k = @k";
            cmd.Parameters.AddWithValue("@k", SeqKey(userId));

            var value = await cmd.ExecuteScalarAsync(ct);
            return value is null or DBNull ? 0 : Convert.ToInt64(value);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private static async Task WriteSeqAsync(
        SqliteConnection conn, SqliteTransaction tx, int userId, long seq, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO meta (k, v) VALUES (@k, @v)
            ON CONFLICT(k) DO UPDATE SET v = excluded.v
            """;
        cmd.Parameters.AddWithValue("@k", SeqKey(userId));
        cmd.Parameters.AddWithValue("@v", seq.ToString());

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string SeqKey(int userId) => $"note_seq:{userId}";

    private static NoteItem Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Title = r.IsDBNull(1) ? string.Empty : r.GetString(1),
        Body = r.IsDBNull(2) ? string.Empty : r.GetString(2),
        Pinned = r.GetInt64(3) != 0,
        Deleted = r.GetInt64(4) != 0,
        UpdatedAt = r.GetInt64(5),
        Rev = r.GetInt64(6),
    };
}
