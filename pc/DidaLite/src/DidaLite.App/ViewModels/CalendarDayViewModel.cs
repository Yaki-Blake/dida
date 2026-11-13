using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DidaLite.App.ViewModels;

/// <summary>迷你月历的单格。</summary>
public sealed partial class CalendarDayViewModel : ObservableObject
{
    public CalendarDayViewModel(DateTime date)
    {
        Date = date;
    }

    public DateTime Date { get; }
    public string Day => Date.Day.ToString();

    [ObservableProperty] private bool _isOtherMonth;
    [ObservableProperty] private bool _isToday;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _hasTasks;
}

/// <summary>月视图大网格的单格。</summary>
public sealed partial class MonthCellViewModel : ObservableObject
{
    public MonthCellViewModel(
        DateTime date,
        bool isOtherMonth,
        bool isToday,
        bool isSelected,
        IReadOnlyList<TaskViewModel> items,
        double contentHeight)
    {
        Date = date;
        IsOtherMonth = isOtherMonth;
        IsToday = isToday;
        IsSelected = isSelected;
        Items = items;
        ContentHeight = contentHeight;
    }

    public DateTime Date { get; }
    public string Day => Date.Day.ToString();
    public bool IsOtherMonth { get; }
    public bool IsToday { get; }
    public bool IsSelected { get; }

    /// <summary>当天全部日程；收起时靠条目区的裁剪高度只露出前几条。</summary>
    public IReadOnlyList<TaskViewModel> Items { get; }

    /// <summary>条目区可见高度，展开 / 收起时由动画过渡。</summary>
    [ObservableProperty] private double _contentHeight;
}

/// <summary>月视图的一整行（7 天）。左侧「展 / 收」控制这一行是否展开。</summary>
public sealed partial class MonthWeekViewModel : ObservableObject
{
    public MonthWeekViewModel(int index) => Index = index;

    public int Index { get; }
    public ObservableCollection<MonthCellViewModel> Cells { get; } = [];

    /// <summary>该周任意一天超过显示上限时，左侧才出现「展」。</summary>
    [ObservableProperty] private bool _canExpand;

    [ObservableProperty] private bool _isExpanded;

    /// <summary>这一周任意一天最多的条目数，决定展开后的行高。</summary>
    public int MaxCount { get; set; }

    /// <summary>整行高度（含日期行），展开 / 收起时由动画过渡。</summary>
    [ObservableProperty] private double _rowHeight;

    public string ToggleText => IsExpanded ? "收" : "展";

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ToggleText));
}
