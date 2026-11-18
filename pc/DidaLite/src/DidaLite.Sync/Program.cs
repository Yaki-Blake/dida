using System.Globalization;
using System.Reflection;
using DidaLite.Data;
using DidaLite.Data.Sync;
using Microsoft.Data.Sqlite;

namespace DidaLite.Sync;

/// <summary>
/// 独立同步服务入口。
///   dida-sync serve                     启动服务（默认命令）
///   dida-sync set-user &lt;用户名&gt; &lt;密码&gt;   设置/重设同步账号（会清掉手机已登录的 token）
///   dida-sync info                      打印当前配置与库内状态
/// 配置全部走环境变量或命令行参数，不读 .env，避免和桌面端的配置互相干扰。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            return options.Command switch
            {
                "set-user" => SetUser(options),
                "info" => Info(options),
                "serve" => Serve(options),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"启动失败：{ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("用法：");
        Console.Error.WriteLine("  dida-sync serve                      启动同步服务（默认端口 5081）");
        Console.Error.WriteLine("  dida-sync set-user <用户名> <密码>    设置同步账号");
        Console.Error.WriteLine("  dida-sync info                       查看配置与数据状态");
        Console.Error.WriteLine();
        Console.Error.WriteLine("可选参数：--port 5081  --data /var/lib/dida-sync");
        return 2;
    }

    private static int SetUser(Options options)
    {
        if (string.IsNullOrWhiteSpace(options.User) || string.IsNullOrEmpty(options.Password))
        {
            Console.Error.WriteLine("需要用户名和密码：dida-sync set-user <用户名> <密码>");
            return 2;
        }

        var users = OpenUsers(options);
        var created = users.SaveAsync(options.User, options.Password).GetAwaiter().GetResult();

        Console.WriteLine(created
            ? $"账号已创建：{options.User}"
            : $"账号密码已更新：{options.User}（旧 token 已作废，需重新登录）");
        return 0;
    }

    private static int Info(Options options)
    {
        var store = OpenStore(options);
        var users = OpenUsers(options);
        var names = users.ListNamesAsync().GetAwaiter().GetResult();
        var tasks = store.GetAll();
        var alarms = store.GetAlarms();

        Console.WriteLine($"数据目录：{options.DataDir}");
        Console.WriteLine($"数据库：  {store.DbPath}");
        Console.WriteLine($"端口：    {options.Port}");
        Console.WriteLine($"后端：    本机 SQLite（多用户）");
        Console.WriteLine($"账号数：  {names.Count}");
        foreach (var name in names) Console.WriteLine($"  - {name}");
        Console.WriteLine($"任务：    {tasks.Count} 条（含墓碑 {tasks.Count(t => t.Deleted)} 条）");
        Console.WriteLine($"闹钟：    {alarms.Count(a => !a.Deleted)} 条（随电脑端同步上传，手机端只读）");
        return 0;
    }

    private static int Serve(Options options)
    {
        var store = OpenStore(options);
        ITaskStore tasks = new LocalTaskStore(store.DbPath);
        IUserStore users = new LocalUserStore(store.DbPath);
        IAlarmStore alarms = new LocalAlarmStore(store.DbPath);
        INoteStore notes = new LocalNoteStore(store.DbPath);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        Console.WriteLine($"dida-sync {version}（多用户版）");
        Console.WriteLine($"  端口    {options.Port}");
        Console.WriteLine($"  数据库  {store.DbPath}");
        Console.WriteLine($"  回环免鉴权 {(options.AllowLoopback ? "已开启" : "关闭（公网部署请保持关闭）")}");

        var count = users.CountAsync().GetAwaiter().GetResult();
        Console.WriteLine($"  已注册账号 {count} 个");
        if (count == 0)
        {
            Console.WriteLine();
            Console.WriteLine("!! 还没有账号：手机/电脑端登录时输入新用户名会自动注册，");
            Console.WriteLine("!! 也可用 dida-sync set-user <用户名> <密码> 预先创建。");
        }

        Console.WriteLine();

        var api = new Api(tasks, users, alarms, notes, null, options);
        api.Run();
        return 0;
    }

    private static LocalUserStore OpenUsers(Options options) => new(options.DbPath);

    /// <summary>打开（必要时创建）SQLite 库，并切到 WAL：崩溃后不丢已提交的写入。</summary>
    internal static LocalStore OpenStore(Options options)
    {
        var store = new LocalStore(options.DbPath);
        store.Initialize();

        using var conn = new SqliteConnection($"Data Source={store.DbPath};Default Timeout=30");
        conn.Open();

        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL";
            _ = wal.ExecuteScalar();
        }

        using (var tune = conn.CreateCommand())
        {
            tune.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=30000;";
            tune.ExecuteNonQuery();
        }

        return store;
    }
}

/// <summary>命令行与环境变量的合并结果。命令行优先，其次环境变量，最后是默认值。</summary>
internal sealed class Options
{
    public string Command { get; init; } = "serve";

    public int Port { get; init; } = 5081;

    public string DataDir { get; init; } = DefaultDataDir();

    /// <summary>本机回环请求免 token。桌面端需要，服务器上不要开（反代后所有请求都可能来自 127.0.0.1）。</summary>
    public bool AllowLoopback { get; init; }

    /// <summary>放在 nginx / caddy 后面时打开，才能拿到真实客户端 IP 用于限流。</summary>
    public bool TrustProxy { get; init; }

    public string? User { get; init; }

    public string? Password { get; init; }

    public string DbPath => Path.Combine(DataDir, "dida.db");

    public static Options Parse(string[] args)
    {
        LoadServerEnv();

        var command = "serve";
        int? port = null;
        string? data = null;
        string? user = null;
        string? password = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--port" when i + 1 < args.Length:
                    port = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--data" when i + 1 < args.Length:
                    data = args[++i];
                    break;
                case "--help" or "-h":
                    command = "help";
                    break;
                default:
                    if (arg.StartsWith('-')) break;

                    // set-user 后面跟的两个位置参数
                    if (command == "serve" && user is null) { command = arg; }
                    else if (user is null) user = arg;
                    else if (password is null) password = arg;
                    break;
            }
        }

        return new Options
        {
            Command = command,
            Port = port ?? EnvInt("DIDA_SYNC_PORT") ?? 5081,
            DataDir = data ?? Env("DIDA_DATA_DIR") ?? DefaultDataDir(),
            AllowLoopback = Env("DIDA_ALLOW_LOOPBACK") == "1",
            TrustProxy = Env("DIDA_TRUST_PROXY") == "1",
            User = user,
            Password = password,
        };
    }

    private static string? Env(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// systemd 靠 EnvironmentFile 读 /etc/dida-sync.env，手工执行 set-user / info 时没有这一步，
    /// 就会出现「info 说端口 5081、实际跑在 5093」这种误导。这里补上同样的读取；
    /// 已经存在的环境变量优先，命令行参数仍然最高。
    /// </summary>
    private static void LoadServerEnv()
    {
        const string path = "/etc/dida-sync.env";
        if (!File.Exists(path)) return;

        foreach (var line in File.ReadAllLines(path))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;

            var split = text.IndexOf('=');
            if (split <= 0) continue;

            var key = text[..split].Trim();
            if (Environment.GetEnvironmentVariable(key) is not null) continue;

            Environment.SetEnvironmentVariable(key, text[(split + 1)..].Trim().Trim('"'));
        }
    }

    private static int? EnvInt(string name) =>
        int.TryParse(Env(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string DefaultDataDir()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DidaLiteSync");
        }

        return "/var/lib/dida-sync";
    }
}
