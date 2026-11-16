using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DidaLite.App.Services;

namespace DidaLite.App.ViewModels;

/// <summary>周视图里的一格：某天某节次；教务系统同格可放多门课。</summary>
public sealed class ScheduleCell
{
    public int Slot { get; init; }
    public int Weekday { get; init; }
    public string Date { get; init; } = string.Empty;
    public IReadOnlyList<ScheduleEvent> Items { get; init; } = [];
}

/// <summary>表头的一天。</summary>
public sealed class ScheduleDayHeader
{
    public int Weekday { get; init; }
    public string Name { get; init; } = string.Empty;
    public string DateText { get; init; } = string.Empty;
    public bool IsToday { get; init; }
}

/// <summary>左侧节次列的一行。</summary>
public sealed class SchedulePeriodRow
{
    public string Name { get; init; } = string.Empty;
    public string Time { get; init; } = string.Empty;
}

/// <summary>日视图里的一节课；把拼好的文案放在这里，XAML 只做简单绑定。</summary>
public sealed class ScheduleDayItem
{
    public ScheduleEvent Course { get; init; } = new();
    public string TimeText => $"{Course.Start} – {Course.End}";
    public string PeriodText => $"第 {Course.Period} 节";
    public string MetaText => $"{Course.Room} · {Course.Teacher}";
    public string WeeksText => Course.WeeksExpr;
}

/// <summary>
/// 「课表」页：按教学周展开 7 天 × 6 个节次段的网格，也能切到单日列表。
/// 数据直接来自 ScheduleStore，与 5080 上的 /api/schedule 是同一份内存数据。
/// </summary>
public sealed partial class ScheduleViewModel : ObservableObject
{
    private static readonly string[] DayNames = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];

    /// <summary>佛山校区连排作息；与生成 ICS 时用的时刻表一致。</summary>
    private static readonly SchedulePeriodRow[] PeriodRows =
    [
        new() { Name = "第 1-2 节", Time = "08:30–10:00" },
        new() { Name = "第 3-4 节", Time = "10:20–11:50" },
        new() { Name = "第 5-6 节", Time = "14:00–15:30" },
        new() { Name = "第 7-8 节", Time = "15:50–17:20" },
        new() { Name = "第 9-10 节", Time = "18:30–20:00" },
        new() { Name = "第 11-12 节", Time = "20:20–21:50" },
    ];

    private readonly ScheduleStore _store = ScheduleStore.Instance;

    public ObservableCollection<ScheduleCell> Cells { get; } = [];
    public ObservableCollection<ScheduleDayHeader> Headers { get; } = [];
    public ObservableCollection<ScheduleDayItem> DayItems { get; } = [];

    public IReadOnlyList<SchedulePeriodRow> Periods => PeriodRows;
    public IReadOnlyList<string> DayLabels => DayNames;
    public IReadOnlyList<int> Weeks { get; } = Enumerable.Range(1, 20).ToArray();
    public int MaxWeek => Weeks[^1];

    public string Term => _store.Data.Term;
    public string Week1Monday => _store.Data.Week1Monday;
    public int EventsCount => _store.Events.Count;
    public string Source => _store.Data.Source;
    public bool IsEmpty => _store.Events.Count == 0;
    public bool HasData => !IsEmpty;

    /// <summary>导入 / 清空后由主界面显式调用，或由 Store 的事件自动触发。</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(Term));
        OnPropertyChanged(nameof(Week1Monday));
        OnPropertyChanged(nameof(EventsCount));
        OnPropertyChanged(nameof(Source));
        if (IsEmpty) IsDetailOpen = false;   // 课表被清空时收起详情，避免停留在已不存在的课程上
        Rebuild();
    }

    private void OnStoreDataChanged()
    {
        // HTTP 导入发生在后台线程，ObservableCollection 必须回 UI 线程改。
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Refresh();
        else dispatcher.Invoke(Refresh);
    }

    [ObservableProperty] private int _week = 1;
    [ObservableProperty] private int _selectedDay = 1;
    [ObservableProperty] private bool _isWeekTab = true;
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private string _dayTitle = string.Empty;
    [ObservableProperty] private string _daySummary = string.Empty;

    // 课程详情面板：字段全部拼成字符串，XAML 只做绑定
    [ObservableProperty] private bool _isDetailOpen;
    [ObservableProperty] private string _detailName = string.Empty;
    [ObservableProperty] private string _detailTime = string.Empty;
    [ObservableProperty] private string _detailDate = string.Empty;
    [ObservableProperty] private string _detailClock = string.Empty;
    [ObservableProperty] private string _detailPeriod = string.Empty;
    [ObservableProperty] private string _detailPeriodName = string.Empty;
    [ObservableProperty] private string _detailWeekName = string.Empty;
    [ObservableProperty] private string _detailRoom = string.Empty;
    [ObservableProperty] private string _detailTeacher = string.Empty;
    [ObservableProperty] private string _detailWeeks = string.Empty;
    [ObservableProperty] private string _noteText = string.Empty;
    [ObservableProperty] private string _noteHint = string.Empty;
    [ObservableProperty] private bool _hasNote;

    private string _detailCourseName = string.Empty;
    private string _savedNote = string.Empty;

    public bool IsDayTab => !IsWeekTab;

    /// <summary>星期下拉用的 0 基索引，绑定的是 SelectedDay（1 基）。</summary>
    public int SelectedDayIndex
    {
        get => SelectedDay - 1;
        set => SelectedDay = Math.Clamp(value + 1, 1, 7);
    }

    public ScheduleViewModel()
    {
        Week = Math.Clamp(_store.CurrentWeek, 1, MaxWeek);
        SelectedDay = TodayWeekday();
        ScheduleStore.DataChanged += OnStoreDataChanged;
    }

    partial void OnIsWeekTabChanged(bool value) => OnPropertyChanged(nameof(IsDayTab));

    partial void OnSelectedDayChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedDayIndex));
        RebuildDay();
    }

    partial void OnWeekChanged(int value)
    {
        PreviousWeekCommand.NotifyCanExecuteChanged();
        NextWeekCommand.NotifyCanExecuteChanged();
        Rebuild();
    }

    private static int TodayWeekday() => ((int)DateTime.Now.DayOfWeek + 6) % 7 + 1;

    /// <summary>按当前教学周重建网格与表头；数据只在切周时过滤一次。</summary>
    public void Rebuild()
    {
        var monday = _store.Week1.AddDays((Week - 1) * 7);
        var today = DateOnly.FromDateTime(DateTime.Now);

        var weekEvents = _store.Events.Where(e => e.Week == Week).ToList();
        SummaryText = $"第 {Week} 教学周 · {weekEvents.Count} 节课 · " +
                      $"{weekEvents.Select(e => e.Name).Distinct().Count()} 门课";

        Headers.Clear();
        for (var d = 1; d <= 7; d++)
        {
            var date = monday.AddDays(d - 1);
            Headers.Add(new ScheduleDayHeader
            {
                Weekday = d,
                Name = DayNames[d - 1],
                DateText = date.ToString("M/d", CultureInfo.InvariantCulture),
                IsToday = date == today,
            });
        }

        Cells.Clear();
        for (var slot = 1; slot <= PeriodRows.Length; slot++)
        {
            for (var d = 1; d <= 7; d++)
            {
                var date = monday.AddDays(d - 1);
                var items = weekEvents
                    .Where(e => e.Weekday == d && Math.Clamp(e.Slot, 1, PeriodRows.Length) == slot)
                    .OrderBy(e => e.Start, StringComparer.Ordinal)
                    .ToList();

                Cells.Add(new ScheduleCell
                {
                    Slot = slot,
                    Weekday = d,
                    Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Items = items,
                });
            }
        }

        RebuildDay();
    }

    private void RebuildDay()
    {
        var monday = _store.Week1.AddDays((Week - 1) * 7);
        var date = monday.AddDays(SelectedDay - 1);

        DayTitle = $"{date.ToString("yyyy年M月d日", CultureInfo.GetCultureInfo("zh-CN"))} · {DayNames[SelectedDay - 1]}";

        var items = _store.Events
            .Where(e => e.Week == Week && e.Weekday == SelectedDay)
            .OrderBy(e => e.Start, StringComparer.Ordinal)
            .ToList();

        DayItems.Clear();
        foreach (var item in items) DayItems.Add(new ScheduleDayItem { Course = item });

        DaySummary = items.Count == 0 ? "今天没课" : $"{items.Count} 节课";
    }

    [RelayCommand(CanExecute = nameof(CanPreviousWeek))]
    private void PreviousWeek() => Week--;

    private bool CanPreviousWeek() => Week > 1;

    [RelayCommand(CanExecute = nameof(CanNextWeek))]
    private void NextWeek() => Week++;

    private bool CanNextWeek() => Week < MaxWeek;

    /// <summary>回到本周，并把日视图选到今天。</summary>
    [RelayCommand]
    private void GoCurrentWeek()
    {
        Week = Math.Clamp(_store.CurrentWeek, 1, MaxWeek);
        SelectedDay = TodayWeekday();
        Rebuild();
    }

    [RelayCommand]
    private void ShowWeekTab() => IsWeekTab = true;

    [RelayCommand]
    private void ShowDayTab() => IsWeekTab = false;

    /// <summary>
    /// 从日视图的课程带跳过来：把课表页切到这门课所在的教学周与星期，
    /// 并展开它的详情，点一下就能看全名称 / 时间 / 地点 / 老师与备注。
    /// </summary>
    public void FocusCourse(ScheduleEvent? course)
    {
        if (course is null) return;

        Week = Math.Clamp(course.Week, 1, MaxWeek);
        SelectedDay = Math.Clamp(course.Weekday, 1, 7);
        IsWeekTab = false;
        Rebuild();
        OpenCourse(course);
    }

    /// <summary>点课程卡片：展开名称 / 时间 / 地点 / 老师，并载入这门课的备注。</summary>
    [RelayCommand]
    private void OpenCourse(ScheduleEvent? course)
    {
        if (course is null) return;

        _detailCourseName = course.Name;
        DetailName = course.Name;

        var date = DateOnly.TryParseExact(course.Date, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy年M月d日", CultureInfo.GetCultureInfo("zh-CN"))
            : course.Date;
        var day = course.Weekday is >= 1 and <= 7 ? DayNames[course.Weekday - 1] : string.Empty;
        DetailDate = $"{date} {day}".Trim();
        DetailClock = $"{course.Start} – {course.End}";
        DetailTime = $"{DetailDate} · {DetailClock}";
        DetailPeriodName = $"第 {course.Period} 节";
        DetailWeekName = $"第 {course.Week} 教学周";
        DetailPeriod = $"{DetailPeriodName} · {DetailWeekName}";
        DetailRoom = string.IsNullOrWhiteSpace(course.Room) ? "未填写" : course.Room;
        DetailTeacher = string.IsNullOrWhiteSpace(course.Teacher) ? "未填写" : course.Teacher;
        DetailWeeks = string.IsNullOrWhiteSpace(course.WeeksExpr) ? "全学期" : course.WeeksExpr;

        _savedNote = ScheduleNotes.Get(course.Name);
        NoteText = _savedNote;
        HasNote = !string.IsNullOrWhiteSpace(_savedNote);
        NoteHint = "备注按课程名保存，这门课的每一次课都会看到；导入或清空课表不会删除。";
        IsDetailOpen = true;
    }

    /// <summary>保存备注；空文本即删除。返回后刷新网格，让卡片上的「已备注」标记同步。</summary>
    [RelayCommand]
    private void SaveNote()
    {
        if (string.IsNullOrWhiteSpace(_detailCourseName)) return;

        if (!ScheduleNotes.Set(_detailCourseName, NoteText))
        {
            NoteHint = "保存失败：写不进 schedule_notes.json，请检查程序目录是否只读。";
            return;
        }

        _savedNote = NoteText ?? string.Empty;
        HasNote = !string.IsNullOrWhiteSpace(_savedNote);
        NoteHint = HasNote ? "已保存 · 同一门课的其它节次也会看到" : "备注已清空";
        ScheduleNotes.ApplyTo(_store.Events);
        Rebuild();
    }

    [RelayCommand]
    private void ClearNote()
    {
        NoteText = string.Empty;
        SaveNote();
    }

    /// <summary>直接关掉面板时，未点保存的改动也自动落盘，避免误丢。</summary>
    [RelayCommand]
    private void CloseDetail()
    {
        if (IsDetailOpen && !string.Equals(NoteText ?? string.Empty, _savedNote, StringComparison.Ordinal))
            SaveNote();
        IsDetailOpen = false;
    }
}
