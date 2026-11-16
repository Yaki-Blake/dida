namespace DidaLite.App.ViewModels;

/// <summary>年视图迷你月历里的一格：非本月只占位，不显示数字、不可点。</summary>
public sealed class YearDayViewModel
{
    public YearDayViewModel(DateTime date, bool isOtherMonth, bool isToday, bool isSelected, int taskCount)
    {
        Date = date;
        IsOtherMonth = isOtherMonth;
        IsToday = isToday;
        IsSelected = isSelected;
        TaskCount = taskCount;
        // 非本月只占位：不画绿点，否则空白格里会冒出一颗点
        HasTasks = !isOtherMonth && taskCount > 0;
        HeatLevel = isOtherMonth ? 0 : taskCount switch
        {
            0 => 0,
            <= 2 => 1,
            <= 6 => 2,
            <= 10 => 3,
            _ => 4
        };
    }

    public DateTime Date { get; }

    /// <summary>非本月返回空串：年视图里不放上/下月的日期，避免数字重复。</summary>
    public string Day => IsOtherMonth ? string.Empty : Date.Day.ToString();

    public bool IsOtherMonth { get; }
    public bool IsToday { get; }
    public bool IsSelected { get; }

    /// <summary>这天有任务时画一个小圆点。</summary>
    public bool HasTasks { get; }

    /// <summary>当天的日程数。</summary>
    public int TaskCount { get; }

    /// <summary>年视图热度：0 留白，1 绿、2 黄、3 紫、4 红；非本月不着色。</summary>
    public int HeatLevel { get; }
}

/// <summary>年视图里的一个月：标题、当月任务数、42 个日期格。</summary>
public sealed class YearMonthViewModel
{
    public YearMonthViewModel(int year, int month, IReadOnlyList<YearDayViewModel> days, int count)
    {
        Year = year;
        Month = month;
        Days = days;
        CountText = count == 0 ? string.Empty : $"{count} 项";
    }

    public int Year { get; }
    public int Month { get; }
    public string Title => $"{Month} 月";
    public IReadOnlyList<YearDayViewModel> Days { get; }

    /// <summary>当月任务数，0 时不显示。</summary>
    public string CountText { get; }
}
