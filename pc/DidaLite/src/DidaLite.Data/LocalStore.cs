using System.Globalization;
using DidaLite.Core.Models;
using Microsoft.Data.Sqlite;

namespace DidaLite.Data;

/// <summary>
/// PC 端本地库（SQLite）。每端持有完整副本，断网可全功能使用。
/// </summary>
public sealed class LocalStore
{
    private const string Columns =
        "id, title, note, location, start_at, due_at, remind_at, repeat_rule, done, deleted, updated_at, rev, dirty, reminded_at, all_day";

    private readonly string _connectionString;

    public LocalStore(string? dbPath = null)
    {
        DbPath = dbPath ?? DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public string DbPath { get; }

    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DidaLite");
        return Path.Combine(dir, "dida.db");
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void Initialize()
    {
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS task (
                  id          TEXT PRIMARY KEY,
                  title       TEXT NOT NULL,
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
                  reminded_at INTEGER
                );
                CREATE INDEX IF NOT EXISTS idx_task_rev ON task(rev);
                CREATE INDEX IF NOT EXISTS idx_task_due ON task(due_at);
                CREATE TABLE IF NOT EXISTS meta (
                  k TEXT PRIMARY KEY,
                  v TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS alarm (
                  id           TEXT PRIMARY KEY,
                  label        TEXT,
                  hour         INTEGER NOT NULL,
                  minute       INTEGER NOT NULL,
                  repeat_rule  TEXT NOT NULL DEFAULT 'daily',
                  once_at      INTEGER,
                  enabled      INTEGER NOT NULL DEFAULT 1,
                  last_fired_at INTEGER,
                  deleted      INTEGER NOT NULL DEFAULT 0,
                  updated_at   INTEGER NOT NULL,
                  rev          INTEGER NOT NULL DEFAULT 0,
                  dirty        INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS idx_alarm_rev ON alarm(rev);
                """;
            cmd.ExecuteNonQuery();
        }

        // 老库补列（地点 / 起始时间 / 全天）
        EnsureColumn(conn, "location", "TEXT");
        EnsureColumn(conn, "start_at", "INTEGER");
        EnsureColumn(conn, "all_day", "INTEGER NOT NULL DEFAULT 0");

        // rev=0 表示从未同步过的本地行（老数据 / 示例任务），补上待上传标记，首次同步会带到服务端
        using var mark = conn.CreateCommand();
        mark.CommandText = "UPDATE task SET dirty = 1 WHERE rev = 0 AND dirty = 0";
        mark.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection conn, string column, string definition)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(task)";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
            }
        }

        using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE task ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    public List<TaskItem> GetAll()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns}
            FROM task
            ORDER BY (COALESCE(start_at, due_at) IS NULL), COALESCE(start_at, due_at) ASC, updated_at DESC
            """;

        var list = new List<TaskItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(Map(reader));
        return list;
    }

    public List<TaskItem> GetDirty()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM task WHERE dirty = 1";
        var list = new List<TaskItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(Map(reader));
        return list;
    }

    public void Upsert(TaskItem task)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO task (id, title, note, location, start_at, due_at, remind_at, repeat_rule, done, deleted, updated_at, rev, dirty, reminded_at, all_day)
            VALUES ($id, $title, $note, $location, $start, $due, $remind, $rule, $done, $deleted, $updated, $rev, $dirty, $reminded, $allday)
            ON CONFLICT(id) DO UPDATE SET
              title = excluded.title,
              note = excluded.note,
              location = excluded.location,
              start_at = excluded.start_at,
              due_at = excluded.due_at,
              remind_at = excluded.remind_at,
              repeat_rule = excluded.repeat_rule,
              done = excluded.done,
              deleted = excluded.deleted,
              updated_at = excluded.updated_at,
              rev = excluded.rev,
              dirty = excluded.dirty,
              reminded_at = excluded.reminded_at,
              all_day = excluded.all_day
            """;
        Bind(cmd, task);
        cmd.ExecuteNonQuery();
    }

    public void UpsertMany(IEnumerable<TaskItem> tasks)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var task in tasks)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO task (id, title, note, location, start_at, due_at, remind_at, repeat_rule, done, deleted, updated_at, rev, dirty, reminded_at, all_day)
                VALUES ($id, $title, $note, $location, $start, $due, $remind, $rule, $done, $deleted, $updated, $rev, $dirty, $reminded, $allday)
                ON CONFLICT(id) DO UPDATE SET
                  title = excluded.title, note = excluded.note,
                  location = excluded.location, start_at = excluded.start_at,
                  due_at = excluded.due_at, remind_at = excluded.remind_at,
                  repeat_rule = excluded.repeat_rule, done = excluded.done,
                  deleted = excluded.deleted, updated_at = excluded.updated_at,
                  rev = excluded.rev, dirty = excluded.dirty,
                  reminded_at = excluded.reminded_at, all_day = excluded.all_day
                """;
            Bind(cmd, task);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>服务端接受了本次推送：记下新 rev 并清掉待上传标记。推送途中又被改动的行保持 dirty，等下一轮再推。</summary>
    public void MarkSynced(string id, long rev, long sentUpdatedAt)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE task SET rev = $rev, dirty = 0 WHERE id = $id AND updated_at = $sent";
        cmd.Parameters.AddWithValue("$rev", rev);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$sent", sentUpdatedAt);
        cmd.ExecuteNonQuery();
    }

    /// <summary>落服务端版本：本地在这之后没有再改过才覆盖，否则保留本地待推的那一份。</summary>
    public void ApplyServer(TaskItem task)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO task (id, title, note, location, start_at, due_at, remind_at, repeat_rule, done, deleted, updated_at, rev, dirty, reminded_at, all_day)
            VALUES ($id, $title, $note, $location, $start, $due, $remind, $rule, $done, $deleted, $updated, $rev, $dirty, $reminded, $allday)
            ON CONFLICT(id) DO UPDATE SET
              title = excluded.title, note = excluded.note,
              location = excluded.location, start_at = excluded.start_at,
              due_at = excluded.due_at, remind_at = excluded.remind_at,
              repeat_rule = excluded.repeat_rule, done = excluded.done,
              deleted = excluded.deleted, updated_at = excluded.updated_at,
              rev = excluded.rev, dirty = 0,
              reminded_at = excluded.reminded_at, all_day = excluded.all_day
            WHERE excluded.updated_at >= task.updated_at
            """;
        Bind(cmd, task, false);
        cmd.ExecuteNonQuery();
    }

    public string? GetMeta(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM meta WHERE k = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO meta (k, v) VALUES ($k, $v)
            ON CONFLICT(k) DO UPDATE SET v = excluded.v
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public long GetCursor() =>
        long.TryParse(GetMeta("cursor"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public void SetCursor(long cursor) =>
        SetMeta("cursor", cursor.ToString(CultureInfo.InvariantCulture));

    public List<AlarmItem> GetAlarms()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, label, hour, minute, repeat_rule, once_at, enabled, last_fired_at,
                   deleted, updated_at, rev, dirty
            FROM alarm
            ORDER BY hour, minute
            """;

        var list = new List<AlarmItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AlarmItem
            {
                Id = reader.GetString(0),
                Label = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Hour = reader.GetInt32(2),
                Minute = reader.GetInt32(3),
                RepeatRule = reader.GetString(4),
                OnceAt = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                Enabled = reader.GetInt32(6) != 0,
                LastFiredAt = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                Deleted = reader.GetInt32(8) != 0,
                UpdatedAt = reader.GetInt64(9),
                Rev = reader.GetInt64(10),
                Dirty = reader.GetInt32(11) != 0
            });
        }

        return list;
    }

    public void UpsertAlarm(AlarmItem alarm)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO alarm (id, label, hour, minute, repeat_rule, once_at, enabled, last_fired_at,
                               deleted, updated_at, rev, dirty)
            VALUES ($id, $label, $hour, $minute, $rule, $once, $enabled, $fired, $deleted, $updated, $rev, $dirty)
            ON CONFLICT(id) DO UPDATE SET
              label = excluded.label,
              hour = excluded.hour,
              minute = excluded.minute,
              repeat_rule = excluded.repeat_rule,
              once_at = excluded.once_at,
              enabled = excluded.enabled,
              last_fired_at = excluded.last_fired_at,
              deleted = excluded.deleted,
              updated_at = excluded.updated_at,
              rev = excluded.rev,
              dirty = excluded.dirty
            """;
        cmd.Parameters.AddWithValue("$id", alarm.Id);
        cmd.Parameters.AddWithValue("$label", alarm.Label);
        cmd.Parameters.AddWithValue("$hour", alarm.Hour);
        cmd.Parameters.AddWithValue("$minute", alarm.Minute);
        cmd.Parameters.AddWithValue("$rule", alarm.RepeatRule);
        cmd.Parameters.AddWithValue("$once", (object?)alarm.OnceAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled", alarm.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$fired", (object?)alarm.LastFiredAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$deleted", alarm.Deleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$updated", alarm.UpdatedAt);
        cmd.Parameters.AddWithValue("$rev", alarm.Rev);
        cmd.Parameters.AddWithValue("$dirty", alarm.Dirty ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private static void Bind(SqliteCommand cmd, TaskItem t, bool? dirty = null)
    {
        cmd.Parameters.AddWithValue("$id", t.Id);
        cmd.Parameters.AddWithValue("$title", t.Title);
        cmd.Parameters.AddWithValue("$note", (object?)t.Note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$location", (object?)t.Location ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$start", (object?)t.StartAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$due", (object?)t.DueAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$remind", (object?)t.RemindAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rule", t.RepeatRule);
        cmd.Parameters.AddWithValue("$done", t.Done ? 1 : 0);
        cmd.Parameters.AddWithValue("$deleted", t.Deleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$updated", t.UpdatedAt);
        cmd.Parameters.AddWithValue("$rev", t.Rev);
        cmd.Parameters.AddWithValue("$dirty", (dirty ?? t.Dirty) ? 1 : 0);
        cmd.Parameters.AddWithValue("$reminded", (object?)t.RemindedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$allday", t.AllDay ? 1 : 0);
    }

    private static TaskItem Map(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Title = r.GetString(1),
        Note = r.IsDBNull(2) ? null : r.GetString(2),
        Location = r.IsDBNull(3) ? null : r.GetString(3),
        StartAt = r.IsDBNull(4) ? null : r.GetInt64(4),
        DueAt = r.IsDBNull(5) ? null : r.GetInt64(5),
        RemindAt = r.IsDBNull(6) ? null : r.GetInt64(6),
        RepeatRule = r.GetString(7),
        Done = r.GetInt32(8) != 0,
        Deleted = r.GetInt32(9) != 0,
        UpdatedAt = r.GetInt64(10),
        Rev = r.GetInt64(11),
        Dirty = r.GetInt32(12) != 0,
        RemindedAt = r.IsDBNull(13) ? null : r.GetInt64(13),
        AllDay = r.GetInt32(14) != 0
    };
}
