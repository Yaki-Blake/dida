using System;
using System.IO;
using DidaLite.Core;
using Xunit;

namespace DidaLite.Tests;

/// <summary>
/// 数据落点必须在用户目录而不是 exe 同目录：
/// 程序可能被装到 Program Files，那里普通用户写不进去。
/// </summary>
public sealed class DataPathsTests
{
    [Fact]
    public void Data_dir_is_under_local_app_data()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DidaLite");

        Assert.Equal(expected, DataPaths.Dir, ignoreCase: true);
        Assert.True(Directory.Exists(DataPaths.Dir));
    }

    [Fact]
    public void Write_path_stays_inside_data_dir()
    {
        var path = DataPaths.Write("schedule.json");

        Assert.Equal(DataPaths.Dir, Path.GetDirectoryName(path));
        Assert.Equal("schedule.json", Path.GetFileName(path));
    }

    [Fact]
    public void Find_readable_returns_file_written_to_data_dir()
    {
        var name = $"probe-{Guid.NewGuid():N}.json";
        var path = DataPaths.Write(name);

        try
        {
            File.WriteAllText(path, "{}");
            Assert.Equal(path, DataPaths.FindReadable(name));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Find_readable_returns_null_when_absent_everywhere()
    {
        Assert.Null(DataPaths.FindReadable($"missing-{Guid.NewGuid():N}.json"));
    }
}
