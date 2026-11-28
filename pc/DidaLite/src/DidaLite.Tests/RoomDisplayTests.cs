using DidaLite.App.Services;

namespace DidaLite.Tests;

/// <summary>
/// 教室显示口径：只留「教学楼名 + 房号」。
/// 用例取自本机真实课表（%LOCALAPPDATA%\DidaLite\schedule.json）里出现过的写法。
/// </summary>
public class RoomDisplayTests
{
    [Theory]
    // 半角括号里的楼栋代码
    [InlineData("笃行楼(SJ2)419", "笃行楼419")]
    [InlineData("笃行楼(SJ2)411", "笃行楼411")]
    [InlineData("励学楼(SJ1)303", "励学楼303")]
    [InlineData("拓新楼(SS1)236", "拓新楼236")]
    // 全角括号里的实验室说明
    [InlineData("拓新楼205（计算机基础实验室）", "拓新楼205")]
    // 破折号后缀的实验室说明
    [InlineData("同心楼405-计算机专业实验室", "同心楼405")]
    // 本来就没有多余信息
    [InlineData("风雨球馆", "风雨球馆")]
    [InlineData("笃行楼419", "笃行楼419")]
    // 括号在房号前也要留住房号
    [InlineData("笃行楼(SJ2)", "笃行楼")]
    // 空白与空串
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Shorten_KeepsBuildingAndRoomNumber(string? raw, string expected)
        => Assert.Equal(expected, RoomDisplay.Shorten(raw));

    [Fact]
    public void Shorten_KeepsTeacherTitleUntouched()
    {
        // 「刘楼副教授」含「楼」但不是教室，显示层不该改动教师字段；
        // 这里确认 Shorten 对纯人名不会切掉内容
        Assert.Equal("刘楼副教授", RoomDisplay.Shorten("刘楼副教授"));
    }

    [Fact]
    public void Shorten_DoesNotCutDashWhenNoRoomNumber()
    {
        // 房号特征（数字）不在破折号前时不动它，避免切坏正常地名
        Assert.Equal("笃行楼-食堂", RoomDisplay.Shorten("笃行楼-食堂"));
    }
}
