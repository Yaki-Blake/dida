using System.Security.Cryptography;
using DidaLite.Core;
using MySqlConnector;

namespace DidaLite.Data.Sync;

/// <summary>
/// 同步账号：只有一个用户，用户名 + 密码存在电脑的 MySQL 里。
/// 密码只存 PBKDF2 派生值；手机端登录后拿 token，之后的 /sync 用 token 认人。
/// </summary>
public sealed class UserStore
{
    /// <summary>PBKDF2 迭代次数：手机上登录一次约几十毫秒，够用且不拖慢同步。</summary>
    private const int Iterations = 100_000;

    private readonly string _connectionString;
    private Task? _schema;

    public UserStore(string connectionString) => _connectionString = connectionString;

    /// <summary>.env 里有数据库地址才创建；没有就返回 null。</summary>
    public static UserStore? Create()
    {
        var connection = EnvFile.Get("DIDA_DB_CONNECTION");
        return string.IsNullOrWhiteSpace(connection) ? null : new UserStore(connection);
    }

    /// <summary>当前用户名；还没设置过账号时返回 null。</summary>
    public async Task<string?> GetNameAsync()
    {
        await EnsureAsync();
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM app_user WHERE id = 1";
        return await cmd.ExecuteScalarAsync() as string;
    }

    /// <summary>
    /// 保存账号（首次即创建）。密码没变时保留已换到的 token，手机端不用重新登录；
    /// 改了密码才让旧 token 失效。返回是否清掉了旧登录。
    /// </summary>
    public async Task<bool> SaveAsync(string name, string password)
    {
        await EnsureAsync();

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 密码没变：只更新用户名，token 原样留着
        if (await ReadAsync(conn) is { } old && Verify(password, old.Salt, old.Hash))
        {
            await using var update = conn.CreateCommand();
            update.CommandText = "UPDATE app_user SET name = @name, updated_at = @now WHERE id = 1";
            update.Parameters.AddWithValue("@name", name);
            update.Parameters.AddWithValue("@now", now);
            await update.ExecuteNonQueryAsync();
            return false;
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_user (id, name, salt, hash, token, updated_at)
            VALUES (1, @name, @salt, @hash, NULL, @now)
            ON DUPLICATE KEY UPDATE name = @name, salt = @salt, hash = @hash, token = NULL, updated_at = @now
            """;
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@salt", Convert.ToBase64String(salt));
        cmd.Parameters.AddWithValue("@hash", Convert.ToBase64String(hash));
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync();
        return true;
    }

    /// <summary>校验用户名密码；通过则换发新 token，不通过返回 null。</summary>
    public async Task<string?> LoginAsync(string name, string password)
    {
        await EnsureAsync();

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();

        if (await ReadAsync(conn) is not { } stored) return null;
        if (!string.Equals(stored.Name, name, StringComparison.Ordinal)) return null;
        if (!Verify(password, stored.Salt, stored.Hash)) return null;

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        await using var update = conn.CreateCommand();
        update.CommandText = "UPDATE app_user SET token = @token, updated_at = @now WHERE id = 1";
        update.Parameters.AddWithValue("@token", token);
        update.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await update.ExecuteNonQueryAsync();

        return token;
    }

    /// <summary>token 是否属于当前账号；没设置账号或没登录过时一律无效。token 不设过期时间，登录一次长期有效。</summary>
    public async Task<bool> IsValidTokenAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        await EnsureAsync();

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM app_user WHERE id = 1 AND token = @token";
        cmd.Parameters.AddWithValue("@token", token);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    /// <summary>读当前账号的凭据；还没建账号时返回 null。</summary>
    private static async Task<(string Name, string Salt, string Hash)?> ReadAsync(MySqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, salt, hash FROM app_user WHERE id = 1";

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;

        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    /// <summary>按存储的盐重算派生值，定长比较。</summary>
    private static bool Verify(string password, string saltBase64, string hashBase64)
    {
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(saltBase64), Iterations,
            HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(hashBase64));
    }

    /// <summary>建表只跑一次；失败（MySQL 没起）不缓存错误，下次请求还能重试。</summary>
    private async Task EnsureAsync()
    {
        if (_schema is null) _schema = CreateSchemaAsync();

        try
        {
            await _schema;
        }
        catch
        {
            _schema = null;
            throw;
        }
    }

    private async Task CreateSchemaAsync()
    {
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS app_user (
              id         TINYINT     NOT NULL PRIMARY KEY COMMENT '单用户，固定为 1',
              name       VARCHAR(64) NOT NULL,
              salt       VARCHAR(32) NOT NULL,
              hash       VARCHAR(64) NOT NULL COMMENT 'PBKDF2-SHA256 派生值，Base64',
              token      VARCHAR(64) NULL COMMENT '手机登录后换到的凭证',
              updated_at BIGINT      NOT NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci
            """;
        await cmd.ExecuteNonQueryAsync();
    }
}
