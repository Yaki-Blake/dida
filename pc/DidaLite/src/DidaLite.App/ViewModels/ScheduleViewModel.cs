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

/// <summary>
/// 「课表」页：按教学周展开 7 天 × 6 个节次段的网格。
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

    public IReadOnlyList<SchedulePeriodRow> Periods => PeriodRows;
    public IReadOnlyList<int> Weeks { get; } = Enumerable.Range(1, 20).ToArray();
    public int MaxWeek => Weeks[^1];

    public string Term => _store.Data.Term;
    public string Week1Monday => _store.Data.Week1Monday;
    public int EventsCount => _store.Events.Count;

    /// <summary>已经上过的节次数：结束时刻早于现在的都算。</summary>
    public int AttendedCount
    {
        get
        {
            var now = DateTime.Now;
            return _store.Events.Count(e => EndAt(e) is { } end && end <= now);
        }
    }

    private static DateTime? EndAt(ScheduleEvent e)
    {
        // 兼容 "10:00" 与 "10:00:00" 两种写法
        if (!DateTime.TryParse($"{e.Date} {e.End}", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var end))
            return null;

        return end;
    }
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

    /// <summary>单次备注（只对这一天的这一次课）：正文与保存状态提示。</summary>
    [ObservableProperty] private string _onceNoteText = string.Empty;
    [ObservableProperty] private string _onceNoteHint = string.Empty;

    /// <summary>这门课有没有长期备注，用于在详情面板上标一句「本课已有学期备注」。</summary>
    [ObservableProperty] private bool _hasLongNoteForCourse;

    /// <summary>详情面板顶部那张卡的显示文案：有备注就显示正文，没有就给一句提示。</summary>
    [ObservableProperty] private string _longNoteDisplay = string.Empty;

    /// <summary>单次备注卡的显示文案，同上。</summary>
    [ObservableProperty] private string _onceNoteDisplay = string.Empty;

    /// <summary>是否处于编辑态：点卡片进入，保存 / 取消退出。</summary>
    [ObservableProperty] private bool _isEditingLongNote;
    [ObservableProperty] private bool _isEditingOnceNote;

    /// <summary>两张卡上「没有备注」的占位提示是否显示。</summary>
    [ObservableProperty] private bool _longNoteIsEmpty = true;
    [ObservableProperty] private bool _onceNoteIsEmpty = true;

    private string _detailCourseName = string.Empty;
    private string _savedNote = string.Empty;

    /// <summary>当前详情对应的那一次课，单次备注就挂在它上面。</summary>
    private ScheduleEvent? _detailEvent = null!;
    private string _onceKey = string.Empty;
    private string _savedOnceNote = string.Empty;

    public ScheduleViewModel()
    {
        Week = Math.Clamp(_store.CurrentWeek, 1, MaxWeek);
        ScheduleStore.DataChanged += OnStoreDataChanged;
    }

    partial void OnWeekChanged(int value)
    {
        PreviousWeekCommand.NotifyCanExecuteChanged();
        NextWeekCommand.NotifyCanExecuteChanged();
        Rebuild();
    }

    /// <summary>按当前教学周重建网格与表头；数据只在切周时过滤一次。</summary>
    public void Rebuild()
    {
        var monday = _store.Week1.AddDays((Week - 1) * 7);
        var today = DateOnly.FromDateTime(DateTime.Now);

        var weekEvents = _store.Events.Where(e => e.Week == Week).ToList();

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
    }

    [RelayCommand(CanExecute = nameof(CanPreviousWeek))]
    private void PreviousWeek() => Week--;

    private bool CanPreviousWeek() => Week > 1;

    [RelayCommand(CanExecute = nameof(CanNextWeek))]
    private void NextWeek() => Week++;

    private bool CanNextWeek() => Week < MaxWeek;

    /// <summary>回到本周。</summary>
    [RelayCommand]
    private void GoCurrentWeek()
    {
        Week = Math.Clamp(_store.CurrentWeek, 1, MaxWeek);
        Rebuild();
    }

    /// <summary>点课程卡片：展开名称 / 时间 / 地点 / 老师，并载入这门课的长期备注与这一次课的单次备注。</summary>
    [RelayCommand]
    private void OpenCourse(ScheduleEvent? course)
    {
        if (course is null) return;

        _detailEvent = course;
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

        // 长期备注：按课程名，这门课每次上课都是同一份
        _savedNote = ScheduleNotes.GetLong(course.Name);
        NoteText = _savedNote;
        HasNote = !string.IsNullOrWhiteSpace(_savedNote);
        HasLongNoteForCourse = HasNote;
        NoteHint = string.Empty;
        RefreshLongNoteDisplay();

        // 单次备注：按「日期 + 节次 + 课程名」，只属于这一天这一次
        _onceKey = ScheduleNotes.OnceKey(course);
        _savedOnceNote = ScheduleNotes.GetOnce(_onceKey);
        OnceNoteText = _savedOnceNote;
        OnceNoteHint = string.Empty;
        RefreshOnceNoteDisplay();

        // 每次打开都从「看」开始：想看就能直接读到，想改再点卡片
        IsEditingLongNote = false;
        IsEditingOnceNote = false;

        IsDetailOpen = true;
    }

    /// <summary>把长期备注的正文同步到只读展示区（空的时候给一句占位）。</summary>
    private void RefreshLongNoteDisplay()
    {
        LongNoteIsEmpty = string.IsNullOrWhiteSpace(NoteText);
        LongNoteDisplay = LongNoteIsEmpty ? "还没有长期备注 · 点这里写" : NoteText;
    }

    /// <summary>把单次备注的正文同步到只读展示区。</summary>
    private void RefreshOnceNoteDisplay()
    {
        OnceNoteIsEmpty = string.IsNullOrWhiteSpace(OnceNoteText);
        OnceNoteDisplay = OnceNoteIsEmpty ? "还没有单次备注 · 点这里写" : OnceNoteText;
    }

    /// <summary>点长期备注卡：进入编辑态。</summary>
    [RelayCommand]
    private void EditLongNote()
    {
        IsEditingLongNote = true;
        IsEditingOnceNote = false;
    }

    /// <summary>点单次备注卡：进入编辑态。</summary>
    [RelayCommand]
    private void EditOnceNote()
    {
        IsEditingOnceNote = true;
        IsEditingLongNote = false;
    }

    /// <summary>放弃编辑，回到只读展示（未保存的改动回滚成上次落盘的内容）。</summary>
    [RelayCommand]
    private void CancelLongNote()
    {
        NoteText = _savedNote;
        NoteHint = string.Empty;
        RefreshLongNoteDisplay();
        IsEditingLongNote = false;
    }

    [RelayCommand]
    private void CancelOnceNote()
    {
        OnceNoteText = _savedOnceNote;
        OnceNoteHint = string.Empty;
        RefreshOnceNoteDisplay();
        IsEditingOnceNote = false;
    }

    /// <summary>保存长期备注；空文本即删除。返回后刷新网格，让卡片上的备注圆点同步。</summary>
    [RelayCommand]
    private void SaveNote() => SaveLongNote(silent: false);

    /// <summary>保存单次备注；空文本即删除。</summary>
    [RelayCommand]
    private void SaveOnceNote() => SaveOnceNoteCore(silent: false);

    private void SaveLongNote(bool silent)
    {
        if (string.IsNullOrWhiteSpace(_detailCourseName)) return;

        if (!ScheduleNotes.SetLong(_detailCourseName, NoteText))
        {
            NoteHint = "保存失败：写不进 schedule_notes.json，请检查程序目录是否只读。";
            return;
        }

        _savedNote = NoteText ?? string.Empty;
        HasNote = !string.IsNullOrWhiteSpace(_savedNote);
        HasLongNoteForCourse = HasNote;
        RefreshLongNoteDisplay();
        if (!silent)
        {
            NoteHint = HasNote ? "已保存" : "已清空";
            IsEditingLongNote = false;   // 保存后回到只读，眼睛立刻能读到结果
        }
        ScheduleNotes.ApplyTo(_store.Events);
        Rebuild();
    }

    private void SaveOnceNoteCore(bool silent)
    {
        if (string.IsNullOrWhiteSpace(_onceKey)) return;

        if (!ScheduleNotes.SetOnce(_onceKey, OnceNoteText))
        {
            OnceNoteHint = "保存失败：写不进 schedule_notes.json，请检查程序目录是否只读。";
            return;
        }

        _savedOnceNote = OnceNoteText ?? string.Empty;
        RefreshOnceNoteDisplay();
        if (!silent)
        {
            OnceNoteHint = string.IsNullOrWhiteSpace(_savedOnceNote) ? "已清空" : "已保存";
            IsEditingOnceNote = false;
        }
        ScheduleNotes.ApplyTo(_store.Events);
        Rebuild();
    }

    [RelayCommand]
    private void ClearNote()
    {
        NoteText = string.Empty;
        SaveLongNote(silent: false);
    }

    [RelayCommand]
    private void ClearOnceNote()
    {
        OnceNoteText = string.Empty;
        SaveOnceNoteCore(silent: false);
    }

    /// <summary>直接关掉面板时，两份备注里未点保存的改动都自动落盘，避免误丢。</summary>
    [RelayCommand]
    private void CloseDetail()
    {
        if (IsDetailOpen)
        {
            if (!string.Equals(NoteText ?? string.Empty, _savedNote, StringComparison.Ordinal))
                SaveLongNote(silent: true);
            if (!string.Equals(OnceNoteText ?? string.Empty, _savedOnceNote, StringComparison.Ordinal))
                SaveOnceNoteCore(silent: true);
        }

        IsDetailOpen = false;
    }
}
