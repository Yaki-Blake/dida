using System;
using System.IO;

namespace DidaLite.Core;

/// <summary>
/// 用户数据的统一落点：%LOCALAPPDATA%\DidaLite。
///
/// 为什么不写 exe 同目录：程序可能被别人装到 Program Files 之类只读位置，
/// 那一层目录普通用户没有写权限，导入课表会直接报「拒绝访问」。
/// 读的时候仍然兼容 exe 同目录里的旧文件，升级上来的用户不会丢数据。
/// </summary>
public static class DataPaths
{
    private static string? _dir;

    /// <summary>数据目录，首次访问时创建。</summary>
    public static string Dir => _dir ??= Resolve();

    /// <summary>可写位置的完整路径（目录已就绪，文件不保证存在）。</summary>
    public static string Write(string fileName) => Path.Combine(Dir, fileName);

    /// <summary>
    /// 读取时按顺序找：用户数据目录 → exe 同目录 → exe\Assets。
    /// 后两个是旧版本遗留的位置，只读沿用，写入永远去用户数据目录。
    /// </summary>
    public static string? FindReadable(string fileName)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(Dir, fileName),
                     Path.Combine(AppContext.BaseDirectory, fileName),
                     Path.Combine(AppContext.BaseDirectory, "Assets", fileName),
                 })
        {
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static string Resolve()
    {
        var preferred = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DidaLite");

        if (TryEnsure(preferred)) return preferred;

        // 用户配置目录被策略锁死时的兜底，至少让程序能跑起来
        var fallback = Path.Combine(Path.GetTempPath(), "DidaLite");
        if (TryEnsure(fallback)) return fallback;

        return AppContext.BaseDirectory;
    }

    private static bool TryEnsure(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
