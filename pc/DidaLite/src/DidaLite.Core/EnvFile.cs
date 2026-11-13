namespace DidaLite.Core;

/// <summary>极简 .env 读取：KEY=VALUE 每行一条，忽略空行与 # 注释。</summary>
public static class EnvFile
{
    /// <summary>从程序目录向上找 .env 并读入环境变量；已存在的环境变量优先，找不到就什么也不做。</summary>
    public static void Load()
    {
        var path = FindPath();
        if (path is null) return;

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
        var path = FindPath();
        if (path is null) return;

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
    }

    public static string? Get(string key) => Environment.GetEnvironmentVariable(key);

    private static string? FindPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, ".env");
            if (File.Exists(path)) return path;
        }

        return null;
    }
}
