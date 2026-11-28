using System.Text.RegularExpressions;

namespace DidaLite.App.Services;

/// <summary>
/// 教室文案的显示口径：只留「教学楼名 + 房号」，去掉楼栋代码和补充说明。
/// 教务系统里同一间教室有多种写法，括号里可能是 SJ2 / SS1 这类楼栋代码，
/// 也可能是「计算机基础实验室」这类说明，还混着全角/半角括号与破折号后缀：
///   笃行楼(SJ2)419        → 笃行楼419
///   拓新楼205（计算机基础实验室） → 拓新楼205
///   同心楼405-计算机专业实验室    → 同心楼405
/// 与安卓端 <c>ScheduleText.cleanRoom</c> 同一套规则（那边只处理括号，
/// 这里补上「楼名+房号」后面用破折号接实验室的情况）。
/// <para>
/// 只在显示层做——原始 Room 仍按导入值落盘并写进 ICS 与 5080 接口，
/// 免得改了数据以后教务系统再同步回来对不上，也保住订阅方拿到的原文。
/// </para>
/// </summary>
public static class RoomDisplay
{
    /// <summary>所有圆括号（全角 / 半角）连同里面的内容一起删掉。</summary>
    private static readonly Regex Parens = new(@"[（(][^）)]*[）)]", RegexOptions.Compiled);

    /// <summary>
    /// 形如「楼名 + 房号」后面用 - — – 接的补充说明（实验室 / 中心 之类）。
    /// 只认这一种「房号在前、说明在后」的写法，不动「笃行楼-SJ2419」这类
    /// 没有房号特征的串，避免把正常地名切坏。
    /// </summary>
    private static readonly Regex DashSuffix = new(
        @"(?<=[0-9])\s*[-—–]\s*[^0-9]*$", RegexOptions.Compiled);

    /// <summary>显示用的教室名；空串原样返回，交给调用方决定占位文案。</summary>
    public static string Shorten(string? room)
    {
        if (string.IsNullOrWhiteSpace(room)) return string.Empty;

        var s = Parens.Replace(room.Trim(), "");
        s = DashSuffix.Replace(s, "");

        return s.Trim();
    }
}
