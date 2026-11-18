using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace DidaLite.Data.Sync;

/// <summary>
/// 多用户同步账号：本机 SQLite 的 app_user 表，用户名唯一，id 自增做 userId。
/// 密码只存 PBKDF2 派生值。登录时用户名不存在即自动注册（隐式注册），
/// 存在则校验密码。token 存在 app_token 表里，一个账号可以同时有多台设备在线，
/// 各自一枚 token，谁登录都不会把另一台顶下线。
/// </summary>
public sealed class LocalUserStore : IUserStore
{
    /// <summary>与旧版保持同一套参数，迁移历史密码时派生值不变。</summary>
    private const int Iterations = 100_000;

    private readonly string _connectionString;
    private Task? _schema;

    public LocalUserStore(string dbPath)
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

    public async Task<int> CountAsync()
    {
        await EnsureAsync();
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM app_user";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    public async Task<string?> GetNameAsync(string name)
    {
        await EnsureAsync();
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM app_user WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);
        return await cmd.ExecuteScalarAsync() as string;
    }

    public async Task<IReadOnlyList<string>> ListNamesAsync()
    {
        await EnsureAsync();
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM app_user ORDER BY id";
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    /// <summary>
    /// 登录：用户名不存在→建号；存在且密码对→换发 token；存在但密码错→null。
    /// 整段放进一个事务，避免两个设备同时用新用户名登录时建出重复账号。
    /// </summary>
    public async Task<(string Token, int UserId)?> LoginAsync(string name, string password)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password)) return null;

        await EnsureAsync();
        await using var conn = await OpenAsync();
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();

        var row = await ReadAsync(conn, tx, name);
        int userId;

        if (row is null)
        {
            // 隐式注册：用户名没见过就建号
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Derive(password, salt);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            await using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO app_user (name, salt, hash, token, created_at, updated_at)
                VALUES (@name, @salt, @hash, NULL, @now, @now)
                """;
            insert.Parameters.AddWithValue("@name", name);
            insert.Parameters.AddWithValue("@salt", Convert.ToBase64String(salt));
            insert.Parameters.AddWithValue("@hash", Convert.ToBase64String(hash));
            insert.Parameters.AddWithValue("@now", now);
            await insert.ExecuteNonQueryAsync();

            userId = Convert.ToInt32(await LastInsertIdAsync(conn, tx));
            Console.WriteLine($"新账号注册：{name}（id={userId}）");
        }
        else
        {
            if (!Verify(password, row.Value.Salt, row.Value.Hash)) return null;
            userId = row.Value.Id;
        }

        // 每台设备发一枚自己的 token：以前是覆盖 app_user.token，电脑一登手机就掉线
        var token = NewToken();
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await using var addToken = conn.CreateCommand();
        addToken.Transaction = tx;
        addToken.CommandText = """
            INSERT INTO app_token (token, user_id, created_at) VALUES (@token, @id, @now);
            DELETE FROM app_token WHERE user_id = @id AND created_at < @cut;
            """;
        addToken.Parameters.AddWithValue("@token", token);
        addToken.Parameters.AddWithValue("@id", userId);
        addToken.Parameters.AddWithValue("@now", stamp);
        addToken.Parameters.AddWithValue("@cut", stamp - TokenKeepMs);
        await addToken.ExecuteNonQueryAsync();

        await tx.CommitAsync();
        return (token, userId);
    }

    /// <summary>token 认人：先查 app_token（新表，多设备），再退回 app_user.token（老版本留下的那枚）。</summary>
    public async Task<int?> ResolveTokenAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        await EnsureAsync();
        await using var conn = await OpenAsync();

        int? hit = null;
        long created = 0;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT user_id, created_at FROM app_token WHERE token = @token";
            cmd.Parameters.AddWithValue("@token", token);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                hit = reader.GetInt32(0);
                created = reader.GetInt64(1);
            }
        }

        if (hit is { } uid)
        {
            // 用过就把时间戳往后推：清理只对真正闲置的设备生效
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - created > TokenTouchMs)
            {
                await using var touch = conn.CreateCommand();
                touch.CommandText = "UPDATE app_token SET created_at = @now WHERE token = @token";
                touch.Parameters.AddWithValue("@now", now);
                touch.Parameters.AddWithValue("@token", token);
                await touch.ExecuteNonQueryAsync();
            }

            return uid;
        }

        await using var legacy = conn.CreateCommand();
        legacy.CommandText = "SELECT id FROM app_user WHERE token = @token";
        legacy.Parameters.AddWithValue("@token", token);
        var old = await legacy.ExecuteScalarAsync();
        return old is null or DBNull ? null : Convert.ToInt32(old);
    }

    /// <summary>命令行 set-user：存在则改密（并把该账号名下所有设备的 token 作废），不存在则创建。</summary>
    public async Task<bool> SaveAsync(string name, string password)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password)) return false;

        await EnsureAsync();
        await using var conn = await OpenAsync();
        var row = await ReadAsync(conn, null, name);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Derive(password, salt);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (row is not null)
        {
            await using var update = conn.CreateCommand();
            update.CommandText = """
                UPDATE app_user SET salt = @salt, hash = @hash, token = NULL, updated_at = @now
                WHERE id = @id;
                DELETE FROM app_token WHERE user_id = @id;
                """;
            update.Parameters.AddWithValue("@salt", Convert.ToBase64String(salt));
            update.Parameters.AddWithValue("@hash", Convert.ToBase64String(hash));
            update.Parameters.AddWithValue("@now", now);
            update.Parameters.AddWithValue("@id", row.Value.Id);
            await update.ExecuteNonQueryAsync();
            return false;
        }

        await using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO app_user (name, salt, hash, token, created_at, updated_at)
            VALUES (@name, @salt, @hash, NULL, @now, @now)
            """;
        insert.Parameters.AddWithValue("@name", name);
        insert.Parameters.AddWithValue("@salt", Convert.ToBase64String(salt));
        insert.Parameters.AddWithValue("@hash", Convert.ToBase64String(hash));
        insert.Parameters.AddWithValue("@now", now);
        await insert.ExecuteNonQueryAsync();
        return true;
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 10000;";
        await pragma.ExecuteNonQueryAsync();
        return conn;
    }

    private static async Task<(int Id, string Name, string Salt, string Hash)?> ReadAsync(
        SqliteConnection conn, SqliteTransaction? tx, string name)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, name, salt, hash FROM app_user WHERE name = @name";
        cmd.Parameters.AddWithValue("@name", name);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return (reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    private static async Task<long> LastInsertIdAsync(SqliteConnection conn, SqliteTransaction tx)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

    private static bool Verify(string password, string saltBase64, string hashBase64)
    {
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(saltBase64), Iterations,
            HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(hashBase64));
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>token 保留 90 天：换过一茬设备之后旧行自动清掉，表不会无限涨。</summary>
    private const long TokenKeepMs = 90L * 24 * 60 * 60 * 1000;

    /// <summary>
    /// 常用 token 的使用时间戳最多 30 天推一次：天天同步的手机永远够不到 90 天那条线，
    /// 真正闲置三个月的设备才会被清理，避免「明明还在用却被踢下线」。
    /// </summary>
    private const long TokenTouchMs = 30L * 24 * 60 * 60 * 1000;

    /// <summary>建表只跑一次；出错不缓存，下次请求还能重试。顺带把旧版单用户记录迁移过来。</summary>
    private async Task EnsureAsync()
    {
        if (_schema is null) _schema = CreateSchemaAsync();
        try { await _schema; }
        catch { _schema = null; throw; }
    }

    private async Task CreateSchemaAsync()
    {
        await using var conn = await OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS app_user (
              id         INTEGER PRIMARY KEY AUTOINCREMENT,
              name       TEXT    NOT NULL UNIQUE,
              salt       TEXT    NOT NULL,
              hash       TEXT    NOT NULL,
              token      TEXT    NULL,
              created_at INTEGER NOT NULL,
              updated_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_app_user_token ON app_user(token);

            CREATE TABLE IF NOT EXISTS app_token (
              token      TEXT    PRIMARY KEY,
              user_id    INTEGER NOT NULL,
              created_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_app_token_user ON app_token(user_id);
            """;
        await cmd.ExecuteNonQueryAsync();

        // 旧版单用户库迁移：id=1 且无 created_at 列时，就地补列并把那条记录留下
        await using (var cols = conn.CreateCommand())
        {
            cols.CommandText = "PRAGMA table_info(app_user)";
            var hasCreated = false;
            await using var reader = await cols.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                if (string.Equals(reader.GetString(1), "created_at", StringComparison.OrdinalIgnoreCase)) hasCreated = true;
            reader.Close();

            if (!hasCreated)
            {
                await using var alter = conn.CreateCommand();
                alter.CommandText = """
                    ALTER TABLE app_user ADD COLUMN created_at INTEGER NOT NULL DEFAULT 0;
                    UPDATE app_user SET created_at = updated_at WHERE created_at = 0;
                    """;
                await alter.ExecuteNonQueryAsync();
            }
        }
    }
}
