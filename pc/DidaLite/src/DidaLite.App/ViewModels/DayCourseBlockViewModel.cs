using System.Globalization;
using DidaLite.App.Services;

namespace DidaLite.App.ViewModels;

/// <summary>
/// 日视图左侧课程带上的一节课：位置由上课 / 下课时刻算好，文案在这里拼好，
/// XAML 只做绑定。课程带只有一条「沙岸」，所以不带分栏信息。
/// </summary>
public sealed class DayCourseBlockViewModel
{
    public DayCourseBlockViewModel(ScheduleEvent course, double top, double height)
    {
        Course = course;
        Top = top;
        Height = height;
    }

    public ScheduleEvent Course { get; }

    /// <summary>相对时间轴顶部的偏移（像素），与右侧日程块共用同一套刻度。</summary>
    public double Top { get; }

    public double Height { get; }

    public string Name => Course.Name;

    /// <summary>08:30 – 10:00；没有下课时间的课只留开始时刻。</summary>
    public string TimeText => string.IsNullOrWhiteSpace(Course.End)
        ? Course.Start
        : $"{Course.Start} – {Course.End}";

    /// <summary>
    /// 教室 · 老师，缺哪个就只留另一个。悬停提示用，卡片里两者各占一行。
    /// 这里用原始教室（带楼栋代码 / 实验室说明）：卡片上只写「笃行楼419」，
    /// 想知道完整地点就悬停——和课表页卡片的 ToolTip 同一个分工。
    /// </summary>
    public string MetaText
    {
        get
        {
            var room = Course.Room?.Trim() ?? string.Empty;
            var teacher = TeacherText;

            if (room.Length == 0) return teacher;
            if (teacher.Length == 0) return room;
            return $"{room} · {teacher}";
        }
    }

    /// <summary>教室，单独一行：只留「教学楼名 + 房号」，楼栋代码与实验室说明不显示。</summary>
    public string RoomText => Course.RoomShort;

    /// <summary>老师，单独一行，短课名挤不下时优先让位。</summary>
    public string TeacherText => Course.Teacher?.Trim() ?? string.Empty;

    public string PeriodText => string.IsNullOrWhiteSpace(Course.Period)
        ? string.Empty
        : $"第 {Course.Period} 节";

    /// <summary>矮块只留课名，半节课高的块再补一行时间。</summary>
    public bool ShowTime => Height >= 40;

    /// <summary>再高一点才放得下教室。</summary>
    public bool ShowRoom => Height >= 60 && RoomText.Length > 0;

    /// <summary>老师单独一行。一节课按 90 分钟算约 72px，门槛必须低于它，
    /// 配合 MainWindow 里压紧的 padding / margin，四行都能塞进课程带。</summary>
    public bool ShowTeacher => Height >= 60 && TeacherText.Length > 0;

    /// <summary>悬停提示：一屏里有的东西都在，不用点进去也能看全。</summary>
    public string Tooltip
    {
        get
        {
            var lines = new List<string> { Name, TimeText };
            if (PeriodText.Length > 0) lines.Add(PeriodText);
            var meta = MetaText;
            if (meta.Length > 0) lines.Add(meta);
            lines.Add(CourseDate);
            if (!string.IsNullOrWhiteSpace(Course.WeeksExpr)) lines.Add(Course.WeeksExpr);
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>2026年9月14日 · 周一。</summary>
    private string CourseDate
    {
        get
        {
            if (!DateOnly.TryParseExact(Course.Date, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return Course.Date;
            }

            var day = Course.Weekday is >= 1 and <= 7
                ? " 周" + "一二三四五六日"[Course.Weekday - 1]
                : string.Empty;

            return $"{date.ToString("yyyy年M月d日", CultureInfo.GetCultureInfo("zh-CN"))}{day}";
        }
    }
}
