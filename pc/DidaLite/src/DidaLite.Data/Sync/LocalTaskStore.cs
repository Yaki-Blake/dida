using DidaLite.Core.Models;
using Microsoft.Data.Sqlite;

namespace DidaLite.Data.Sync;

/// <summary>
/// 多用户同步权威库：直接读写本机 SQLite 的 task 表，按 user_id 隔离。
/// 每个用户有独立的 rev 发号器（meta 键 rev_seq:{uid}），A 写任务不会推高 B 的游标。
/// 协议对客户端不变——token 解析出的 userId 由 Api 传进来。
/// </summary>
public sealed class LocalTaskStore : ITaskStore
{
    private const string Columns =
        "id, title, note, location, start_at, due_at, remind_at, repeat_rule, done, deleted, updated_at, rev, all_day";

    private readonly string _connectionString;
    private Task? _schema;

    public LocalTaskStore(string dbPath)
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

    /// <summary>
    /// 服务端权威库用复合主键 (user_id, id)，彻底隔离不同用户的同 id 任务。
    /// 桌面端 LocalStore 建的是 id 单主键表（本地单用户槽位 0），这里在首次使用时就地升级。
    /// </summary>
    private async Task EnsureAsync()
    {
        if (_schema is null) _schema = CreateSchemaAsync();
        try { await _schema; }
        catch { _schema = null; throw; }
    }

    private async Task CreateSchemaAsync()
    {
        await using var conn = await OpenAsync(default);

        // 看 task 表主键列：只有 id 一列说明是旧结构，需要重建为 (user_id, id)
        var pkCount = 0;
        var pkIsUserId = false;
        await using (var info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(task)";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.GetInt32(5) > 0) // pk 序号
                {
                    pkCount++;
                    if (string.Equals(reader.GetString(1), "user_id", StringComparison.OrdinalIgnoreCase)) pkIsUserId = true;
                }
            }
        }

        if (pkCount == 1 && !pkIsUserId)
        {
            // 旧表（id 单主键）→ 复合主键。服务端库是全新部署，旧数据按要求已清空，直接重建。
            await using var rebuild = conn.CreateCommand();
            rebuild.CommandText = """
                DROP TABLE IF EXISTS task_new;
                CREATE TABLE task_new (
                  id          TEXT    NOT NULL,
                  user_id     INTEGER NOT NULL DEFAULT 0,
                  title       TEXT    NOT NULL,
                  note        TEXT,
                  location    TEXT,
                  start_at    INTEGER,
                  due_at      INTEGER,
                  remind_at   INTEGER,
                  repeat_rule TEXT NOT NULL DEFAULT 'none',
                  done        INTEGER NOT NULL DEFAULT 0,
                  deleted     INTEGER NOT NULL DEFAULT 0,
                  updated_at  INTEGER NOT NULL,
                  rev         INTEGER NOT NULL DEFAULT 0,
                  dirty       INTEGER NOT NULL DEFAULT 0,
                  reminded_at INTEGER,
                  all_day     INTEGER NOT NULL DEFAULT 0,
                  PRIMARY KEY (user_id, id)
                );
                INSERT INTO task_new (id, user_id, title, note, location, start_at, due_at, remind_at,
                    repeat_rule, done, deleted, updated_at, rev, dirty, reminded_at, all_day)
                    SELECT id, COALESCE(user_id,0), title, note, location, start_at, due_at, remind_at,
                    repeat_rule, done, deleted, updated_at, rev, dirty, reminded_at, all_day FROM task;
                DROP TABLE task;
                ALTER TABLE task_new RENAME TO task;
                CREATE INDEX IF NOT EXISTS idx_task_user_rev ON task(user_id, rev);
                """;
            await rebuild.ExecuteNonQueryAsync();
        }
        else if (pkCount == 0)
        {
            // 表还不存在（直接 new LocalTaskStore 而没经过 LocalStore.Initialize）
            await using var create = conn.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS task (
                  id          TEXT    NOT NULL,
                  user_id     INTEGER NOT NULL DEFAULT 0,
                  title       TEXT    NOT NULL,
                  note        TEXT,
                  location    TEXT,
                  start_at    INTEGER,
                  due_at      INTEGER,
                  remind_at   INTEGER,
                  repeat_rule TEXT NOT NULL DEFAULT 'none',
                  done        INTEGER NOT NULL DEFAULT 0,
                  deleted     INTEGER NOT NULL DEFAULT 0,
                  updated_at  INTEGER NOT NULL,
                  rev         INTEGER NOT NULL DEFAULT 0,
                  dirty       INTEGER NOT NULL DEFAULT 0,
                  reminded_at INTEGER,
                  all_day     INTEGER NOT NULL DEFAULT 0,
                  PRIMARY KEY (user_id, id)
                );
                """;
            await create.ExecuteNonQueryAsync();
        }
    }

    /// <summary>读取该用户 rev 大于游标的全部任务，含墓碑。</summary>
    public async Task<IReadOnlyList<TaskItem>> GetChangedSinceAsync(int userId, long sinceRev, CancellationToken ct = default)
    {
        await EnsureAsync();
        await using var conn = await OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM task WHERE user_id = @uid AND rev > @since ORDER BY rev";
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@since", sinceRev);

        var rows = new List<TaskItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(Read(reader));
        return rows;
    }

    /// <summary>该用户当前最大 rev：表内该用户最大值与该用户发号器取大，保证游标只进不退。</summary>
    public async Task<long> GetMaxRevAsync(int userId, CancellationToken ct = default)
    {
        await EnsureAsync();
        await using var conn = await OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(rev), 0) FROM task WHERE user_id = @uid";
        cmd.Parameters.AddWithValue("@uid", userId);

        var max = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        return Math.Max(max, await ReadSeqAsync(conn, null, userId, ct));
    }

    /// <summary>写入该用户的一批变更：逐条裁决、分配该用户自己的新 rev，整批一个事务。</summary>
    public async Task<ApplyResult> ApplyAsync(int userId, IEnumerable<TaskItem> changes, CancellationToken ct = default)
    {
        await EnsureAsync();
        var pending = changes as IList<TaskItem> ?? changes.ToList();

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

    private async Task<ApplyResult> ApplyOnceAsync(int userId, IEnumerable<TaskItem> changes, CancellationToken ct)
    {
        var result = new ApplyResult();

        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var seq = await ReadSeqAsync(conn, tx, userId, ct);

        // 一次性把该用户现有任务读进字典，避免逐条查库（原来的 FindAsync 每批 O(n) 查询）
        var existing = await LoadUserTasksAsync(conn, tx, userId, ct);

        foreach (var change in changes)
        {
            existing.TryGetValue(change.Id, out var server);
            if (server is not null && !Wins(change, server))
            {
                result.Rejected.Add(new RejectedChange(change.Id, "stale", server));
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
    /// 传入版本是否压过服务端。规则（优先级从高到低）：
    /// 1) 删除绝对优先：任一方是墓碑就按"删除胜出"裁决——云端已删则拒绝本地的复活/编辑并把墓碑回传，
    ///    本地已删则接受墓碑、让云端也删。这样删除一定能传到另一端，不会出现"这边删了那边还在"。
    /// 2) 新增本地优先：调用方在 server 为 null 时直接接受，这里不拦，本地新建的任务总能推上去。
    /// 3) 编辑按时间：两边都活着时 updated_at 更晚者胜；同一时刻保留服务端（幂等，不来回抖动）。
    /// </summary>
    private static bool Wins(TaskItem incoming, TaskItem server)
    {
        var incomingDeleted = incoming.Deleted;
        var serverDeleted = server.Deleted;

        // 删除优先：谁删了听谁的；两边都删视为接受（墓碑幂等），只有服务端删则拒绝并回传墓碑。
        if (incomingDeleted || serverDeleted) return incomingDeleted;

        // 都活着：时间新者胜；完全相同则保留服务端。
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

    private static async Task<Dictionary<string, TaskItem>> LoadUserTasksAsync(
        SqliteConnection conn, SqliteTransaction tx, int userId, CancellationToken ct)
    {
        var map = new Dictionary<string, TaskItem>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {Columns} FROM task WHERE user_id = @uid";
        cmd.Parameters.AddWithValue("@uid", userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var task = Read(reader);
            map[task.Id] = task;
        }
        return map;
    }

    private static async Task UpsertAsync(
        SqliteConnection conn, SqliteTransaction tx, int userId, TaskItem t, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO task (id, user_id, title, note, location, start_at, due_at, remind_at, repeat_rule,
                              done, deleted, updated_at, rev, dirty, all_day)
            VALUES (@id, @uid, @title, @note, @location, @startAt, @dueAt, @remindAt, @repeatRule,
                    @done, @deleted, @updatedAt, @rev, 0, @allDay)
            ON CONFLICT(user_id, id) DO UPDATE SET
                title = excluded.title, note = excluded.note, location = excluded.location,
                start_at = excluded.start_at, due_at = excluded.due_at, all_day = excluded.all_day,
                remind_at = excluded.remind_at, repeat_rule = excluded.repeat_rule,
                done = excluded.done, deleted = excluded.deleted,
                updated_at = excluded.updated_at, rev = excluded.rev, dirty = 0
            """;

        cmd.Parameters.AddWithValue("@id", t.Id);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@title", t.Title);
        cmd.Parameters.AddWithValue("@note", (object?)t.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@location", (object?)t.Location ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@startAt", (object?)t.StartAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dueAt", (object?)t.DueAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@remindAt", (object?)t.RemindAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@repeatRule", t.RepeatRule);
        cmd.Parameters.AddWithValue("@done", t.Done ? 1 : 0);
        cmd.Parameters.AddWithValue("@deleted", t.Deleted ? 1 : 0);
        cmd.Parameters.AddWithValue("@updatedAt", t.UpdatedAt);
        cmd.Parameters.AddWithValue("@rev", t.Rev);
        cmd.Parameters.AddWithValue("@allDay", t.AllDay ? 1 : 0);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>发号器按用户分开：键为 rev_seq:{uid}，删光任务也不会把该用户的 rev 退回去。</summary>
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

    private static string SeqKey(int userId) => $"rev_seq:{userId}";

    private static TaskItem Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Title = r.GetString(1),
        Note = r.IsDBNull(2) ? null : r.GetString(2),
        Location = r.IsDBNull(3) ? null : r.GetString(3),
        StartAt = r.IsDBNull(4) ? null : r.GetInt64(4),
        DueAt = r.IsDBNull(5) ? null : r.GetInt64(5),
        RemindAt = r.IsDBNull(6) ? null : r.GetInt64(6),
        RepeatRule = r.IsDBNull(7) ? RepeatRules.None : r.GetString(7),
        Done = r.GetInt64(8) != 0,
        Deleted = r.GetInt64(9) != 0,
        UpdatedAt = r.GetInt64(10),
        Rev = r.GetInt64(11),
        AllDay = r.GetInt64(12) != 0,
    };
}
