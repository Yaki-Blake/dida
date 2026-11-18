namespace DidaLite.Core;

/// <summary>极简 .env 读取：KEY=VALUE 每行一条，忽略空行与 # 注释。</summary>
public static class EnvFile
{
    /// <summary>
    /// 配置文件放在用户数据目录（%LOCALAPPDATA%\DidaLite\.env），不跟着程序目录走：
    /// 安装包不再带这个文件，升级、卸载、换安装位置都不会把用户填好的配置冲掉。
    /// 老版本把它放在程序目录，仍然优先读那里，保证升级后原来的配置照旧生效。
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DidaLite", ".env");

    /// <summary>读取配置到环境变量；文件不存在就先建一份空模板。已存在的环境变量优先。</summary>
    public static void Load()
    {
        var path = FindPath() ?? EnsureDefault();
        if (path is null || !File.Exists(path)) return;

        foreach (var line in File.ReadAllLines(path))
        {
            var text = line.Trim();
            if (text.Length == 0 || text.StartsWith('#')) continue;

            var split = text.IndexOf('=');
            if (split <= 0) continue;

            var key = text[..split].Trim();
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, text[(split + 1)..].Trim());
        }
    }

    /// <summary>改写某个键（没有就追加），保留注释与其它键；UTF-8 无 BOM，和启动脚本保持一致。</summary>
    public static void Set(string key, string value)
    {
        var path = FindPath() ?? EnsureDefault();
        if (path is null || !File.Exists(path)) return;

        var lines = File.ReadAllLines(path).ToList();
        var prefix = key + "=";
        var replaced = false;

        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].TrimStart().StartsWith(prefix, StringComparison.Ordinal)) continue;

            lines[i] = prefix + value;
            replaced = true;
            break;
        }

        if (!replaced) lines.Add(prefix + value);

        File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(false));

        // 写盘的同时更新进程内存，让运行中的程序读到新值，不必重启。
        // Get 读的是环境变量；只写盘不更新内存的话，登录/登出都要重启才生效。
        Environment.SetEnvironmentVariable(key, value);
    }

    public static string? Get(string key) => Environment.GetEnvironmentVariable(key);

    private const string Template = """
        # 滴答 · 本地配置
        #
        # 留空即可正常使用：待办、闹钟、日历、手机同步都存在本机，不需要装数据库。
        # 只有在想让多台电脑共用一个数据库时，才把下面这行改成自己的 MySQL 连接串，例如：
        # DIDA_DB_CONNECTION=server=127.0.0.1;port=3306;database=ticktick;user=root;password=你的密码
        DIDA_DB_CONNECTION=

        # 手机端展示的地址（同一 Wi-Fi 下填 http://电脑IP:5080，留空则自动探测）
        DIDA_LAN_ADDR=

        # ---- 同步服务器 ----
        # 数据主库在这台服务器上：电脑和手机都连它，电脑关机也不影响手机用。
        # 账号在服务器上用 set-user 建；软件里「设置 → 同步服务器」填地址和账号、点「登录」即可，不必改本文件。
        # 地址留空则退回单机模式（本机 5080 兼做服务端，手机连同一个 Wi-Fi）。
        # 两种模式切换时，本地数据会全量重传一次，属于正常现象。
        DIDA_REMOTE_URL=https://huangyiwei.online/dida
        DIDA_REMOTE_USER=
        DIDA_REMOTE_PASSWORD=

        """;

    /// <summary>首次运行时写一份模板；目录不可写（比如装在 Program Files 且没有权限）就跳过，纯本地模式照常可用。</summary>
    private static string? EnsureDefault()
    {
        var path = DefaultPath;
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, Template, new System.Text.UTF8Encoding(false));
            }

            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FindPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, ".env");
            if (File.Exists(path)) return path;
        }

        var user = DefaultPath;
        return File.Exists(user) ? user : null;
    }
}
