using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DidaLite.App.Services;
using DidaLite.Core.Models;
using DidaLite.Core.Reminders;
using DidaLite.Data;
using DidaLite.Data.Sync;

namespace DidaLite.App.ViewModels;

public enum ViewMode
{
    Today,
    Day,
    Week,
    Month,
    Year,
    All,
    Done,
    Focus,
    Schedule
}

/// <summary>列表里的一个分组（周视图下即一天）。</summary>
public sealed class TaskGroup
{
    public string Header { get; init; } = string.Empty;
    public string HeaderBrushKey { get; init; } = "text-secondary";
    public IReadOnlyList<TaskViewModel> Items { get; init; } = Array.Empty<TaskViewModel>();
    public bool IsEmpty => Items.Count == 0;

    /// <summary>空分组时显示的占位文案，空字符串表示不显示。</summary>
    public string EmptyHint { get; init; } = string.Empty;
}

public sealed partial class MainViewModel : ObservableObject
{
    private static readonly CultureInfo Zh = CultureInfo.GetCultureInfo("zh-CN");

    /// <summary>重复规则编码，顺序与 RepeatLabels 一一对应。</summary>
    private static readonly string[] RuleCodes =
    [
        RepeatRules.None,
        RepeatRules.Daily,
        RepeatRules.Weekly,
        RepeatRules.Monthly,
        RepeatRules.Yearly,
        RepeatRules.Weekday,
        RepeatRules.Every(2),
        RepeatRules.Every(7),
        RepeatRules.Every(30)
    ];

    private readonly LocalStore _store;
    private readonly SyncClient? _sync;
    private readonly DispatcherTimer _undoTimer;
    private readonly DispatcherTimer _nowTimer;

    private List<TaskItem> _all = [];
    private TaskViewModel? _pendingUndo;

    /// <summary>一键完成的撤销快照：按 id 把时间与完成状态写回去。</summary>
    private List<(string Id, long? StartAt, long? DueAt, long? RemindAt)>? _bulkUndo;

    private long? _undoDueAt;
    private long? _undoStartAt;
    private long? _undoRemindAt;
    private bool _undoWasAdvance;

    /// <summary>月视图当前加载的「日期 → 当天全部任务」，展开 / 收起时按需重新截取。</summary>
    private Dictionary<DateTime, List<TaskItem>> _monthByDate = [];

    /// <summary>已展开的周序号（0–5），窗口尺寸变化重建月格时保留展开状态。</summary>
    private readonly HashSet<int> _expandedWeeks = [];

    /// <summary>点了同步但电脑上还没有同步账号：由窗口层打开设置。</summary>
    public event Action? AccountRequired;

    public MainViewModel(LocalStore store, AlarmService? alarms = null, SyncClient? sync = null)
    {
        _store = store;
        _sync = sync;
        SyncText = "未同步";
        IsDark = string.Equals(store.GetMeta("theme"), "dark", StringComparison.OrdinalIgnoreCase);
        Focus = new FocusViewModel(store, alarms);
        Schedule = new ScheduleViewModel();

        _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer.Stop();
            IsUndoVisible = false;
            _pendingUndo = null;
            _bulkUndo = null;
            _undoWasAdvance = false;
            Load();
        };

        // 时间轴上的「现在」红线每分钟挪一格；不在日视图时几乎不做事
        _nowTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _nowTimer.Tick += (_, _) => UpdateTimelineNow();
        _nowTimer.Start();

        // 导入 / 清空课表后课程带要跟着出现或收起；HTTP 导入在后台线程，得回 UI 线程刷新
        ScheduleStore.DataChanged += OnScheduleDataChanged;
    }

    /// <summary>课表数据改过：课程带宽度、当天课程都要重算。</summary>
    private void OnScheduleDataChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) ApplyScheduleChanged();
        else dispatcher.Invoke(ApplyScheduleChanged);
    }

    private void ApplyScheduleChanged()
    {
        _scheduleRevision++;
        OnPropertyChanged(nameof(ShowCourseLane));
        OnPropertyChanged(nameof(CourseLaneWidth));
        Load();
        OnPropertyChanged(nameof(IsCourseLaneEmpty));
    }

    // ---------------- 集合 ----------------

    public ObservableCollection<TaskGroup> Groups { get; } = [];

    /// <summary>周视图单独一份分组，和日列表分开保存，来回切换时容器不用重建。</summary>
    public ObservableCollection<TaskGroup> WeekGroups { get; } = [];

    /// <summary>时间轴上方的分组：今天视图的「逾期」与当天的「未排时间」。</summary>
    public ObservableCollection<TaskGroup> TimelineGroups { get; } = [];

    /// <summary>时间轴上按开始时刻绝对定位的日程块。</summary>
    public ObservableCollection<TimelineBlockViewModel> TimelineBlocks { get; } = [];

    /// <summary>日视图左侧课程带上的课，和右边日程块用同一套时刻刻度。</summary>
    public ObservableCollection<DayCourseBlockViewModel> DayCourses { get; } = [];

    /// <summary>当天的全天事件：铺在时间轴右侧第一条竖带里，从轴顶一直拉到轴底。</summary>
    public ObservableCollection<TaskViewModel> AllDayItems { get; } = [];

    /// <summary>没有全天事件时整条竖带收起，日视图保持原样。</summary>
    public bool HasAllDayItems => AllDayItems.Count > 0;

    /// <summary>
    /// 全天竖带最窄放到多少：一个字的标题也不至于缩成一条缝。
    /// 宽度本身由 Auto 列按卡片内容量出来（见 MainWindow.xaml），这里只管下限。
    /// </summary>
    public double AllDayLaneMinWidth => 104;

    /// <summary>全天竖带的宽度上限：再长的标题就在这里折行，别把时间轴挤没了。</summary>
    public double AllDayLaneMaxWidth => 200;

    public ObservableCollection<MonthWeekViewModel> MonthWeeks { get; } = [];
    public ObservableCollection<CalendarDayViewModel> Days { get; } = [];
    public ObservableCollection<YearMonthViewModel> YearMonths { get; } = [];

    /// <summary>专注页（倒计时 / 秒表 / 闹钟）。</summary>
    public FocusViewModel Focus { get; }

    /// <summary>课表页（按教学周展开的网格 / 单日列表）。</summary>
    public ScheduleViewModel Schedule { get; }
    public IReadOnlyList<string> WeekDays { get; } = ["一", "二", "三", "四", "五", "六", "日"];

    public IReadOnlyList<string> RepeatLabels { get; } =
    [
        "不重复", "每天", "每周", "每月", "每年", "每个工作日", "每 2 天", "每 7 天", "每 30 天"
    ];

    // ---------------- 状态 ----------------

    [ObservableProperty] private string _monthTitle = string.Empty;
    [ObservableProperty] private string _mainTitle = string.Empty;
    [ObservableProperty] private string _mainSubtitle = string.Empty;

    [ObservableProperty] private ViewMode _mode = ViewMode.Today;
    [ObservableProperty] private DateTime _selectedDate = DateTime.Today;
    [ObservableProperty] private DateTime _displayMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    [ObservableProperty] private int _todayCount;
    [ObservableProperty] private int _allCount;
    [ObservableProperty] private int _doneCount;

    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _isUndoVisible;
    [ObservableProperty] private string _undoText = string.Empty;
    [ObservableProperty] private bool _isDark;

    /// <summary>唤出主界面的快捷键，如 Alt+D；点按钮可改。</summary>
    [ObservableProperty] private string _hotkeyText = HotkeyService.Default;

    /// <summary>快捷键是否被关掉了：设置里点「关闭」后为 true。</summary>
    [ObservableProperty] private bool _isHotkeyOff;

    /// <summary>月格固定占用：日期行高 + 格子上下内边距。</summary>
    public const double MonthCellChrome = 27;

    /// <summary>月格内一条日程的高度（含下间距）。</summary>
    public const double MonthItemHeight = 19;

    // ---------------- 日视图时间轴 ----------------

    /// <summary>时间轴一小时的像素高度。</summary>
    public double TimelineHourHeight => 56;

    /// <summary>时间轴默认从 6 点画到 23 点，有更早 / 更晚的日程再向外扩。</summary>
    [ObservableProperty] private int _timelineStartHour = 6;
    [ObservableProperty] private int _timelineEndHour = 23;

    /// <summary>「现在」红线在时间轴上的小时位置，带小数；不是今天或不在范围内时为 -1。</summary>
    [ObservableProperty] private double _timelineNowHour = -1;

    public double TimelineHeight => Math.Max(1, TimelineEndHour - TimelineStartHour) * TimelineHourHeight;

    partial void OnTimelineStartHourChanged(int value) => OnPropertyChanged(nameof(TimelineHeight));

    partial void OnTimelineEndHourChanged(int value) => OnPropertyChanged(nameof(TimelineHeight));

    /// <summary>日视图看的是哪一天：今天视图跟着真实日期走。</summary>
    private DateTime TimelineDate => Mode == ViewMode.Today ? DateTime.Today : SelectedDate.Date;

    /// <summary>课程带的宽度：刚好放下一节课的卡片。没导入课表时整条带子收起。</summary>
    private const double CourseLaneWidthPx = 148;

    /// <summary>课表里导入过课，日视图才让出左侧课程带。</summary>
    public bool ShowCourseLane => ScheduleStore.Instance.Events.Count > 0;

    /// <summary>课程带实际宽度，未导入课表时为 0。</summary>
    public double CourseLaneWidth => ShowCourseLane ? CourseLaneWidthPx : 0;

    /// <summary>导入过课表、但这天没课：课程带留一条空的沙岸，给一句淡淡的提示。</summary>
    public bool IsCourseLaneEmpty => ShowCourseLane && DayCourses.Count == 0;

    /// <summary>刷新「现在」红线：只在时间轴可见且看的是今天时才出现。</summary>
    private void UpdateTimelineNow()
    {
        TimelineNowHour = IsTimelineView && TimelineDate == DateTime.Today
            ? DateTime.Now.TimeOfDay.TotalHours
            : -1;
    }

    /// <summary>月视图每一行的最小高度，由窗口大小决定。</summary>
    [ObservableProperty] private double _monthRowHeight = 96;

    /// <summary>月视图每天最多显示几条，随行高变化。</summary>
    [ObservableProperty] private int _monthItemLimit = 3;

    /// <summary>当前月视图里是否有需要展开的周；没有就不显示「展开全部」。</summary>
    public bool HasExpandableWeek => MonthWeeks.Any(w => w.CanExpand);

    /// <summary>「展开全部 / 收起全部」按钮文案。</summary>
    public string WeekToggleAllText =>
        MonthWeeks.Any(w => w.CanExpand && !w.IsExpanded) ? "展开全部" : "收起全部";
    [ObservableProperty] private string _syncText = "未同步";

    /// <summary>手机端要填的同步地址，同步服务起来后由窗口填进来。</summary>
    [ObservableProperty] private string _syncAddress = string.Empty;

    // ---------------- 详情 / 表单 ----------------

    [ObservableProperty] private TaskViewModel? _selectedTask;
    [ObservableProperty] private bool _isDetailVisible;
    [ObservableProperty] private bool _isNewTask;

    [ObservableProperty] private string _editTitle = string.Empty;
    [ObservableProperty] private string _editNote = string.Empty;
    [ObservableProperty] private string _editLocation = string.Empty;
    [ObservableProperty] private bool _editAllDay;
    [ObservableProperty] private string _editStartDate = string.Empty;
    [ObservableProperty] private string _editStartTime = "09:00";
    [ObservableProperty] private string _editDueDate = string.Empty;
    [ObservableProperty] private string _editDueTime = "10:00";
    [ObservableProperty] private int _editRepeatIndex;

    public bool IsTimed => !EditAllDay;

    public string DetailTitle => IsNewTask ? "新建任务" : "任务详情";

    /// <summary>今天 / 某一天用天空色时间轴展示，其余列表模式走分组列表。</summary>
    public bool IsTimelineView => Mode is ViewMode.Today or ViewMode.Day;

    public bool IsListView => Mode is ViewMode.All or ViewMode.Done;
    public bool IsWeekView => Mode == ViewMode.Week;
    public bool IsMonthView => Mode == ViewMode.Month;
    public bool IsYearView => Mode == ViewMode.Year;
    public bool IsFocusView => Mode == ViewMode.Focus;
    public bool IsScheduleView => Mode == ViewMode.Schedule;

    /// <summary>周视图同样使用分组列表，只是每天一组。</summary>
    public bool IsGroupedView => IsListView || IsWeekView;

    public bool IsDaySegment => Mode is ViewMode.Today or ViewMode.Day;
    public bool IsWeekSegment => Mode == ViewMode.Week;
    public bool IsMonthSegment => Mode == ViewMode.Month;
    public bool IsYearSegment => Mode == ViewMode.Year;
    public bool IsFocusSegment => Mode == ViewMode.Focus;
    public bool IsScheduleSegment => Mode == ViewMode.Schedule;

    public bool IsTodayActive => Mode == ViewMode.Today;
    public bool IsAllActive => Mode == ViewMode.All;
    public bool IsDoneActive => Mode == ViewMode.Done;
    public bool IsScheduleActive => Mode == ViewMode.Schedule;

    partial void OnEditAllDayChanged(bool value) => OnPropertyChanged(nameof(IsTimed));

    partial void OnIsNewTaskChanged(bool value) => OnPropertyChanged(nameof(DetailTitle));

    partial void OnEditTitleChanged(string value) => SaveDetailCommand.NotifyCanExecuteChanged();

    partial void OnSelectedTaskChanged(TaskViewModel? value)
    {
        if (value is null)
        {
            IsDetailVisible = false;
            return;
        }

        var model = value.Model;

        EditTitle = model.Title;
        EditNote = model.Note ?? string.Empty;
        EditLocation = model.Location ?? string.Empty;
        EditAllDay = model.AllDay;
        EditRepeatIndex = Math.Max(0, Array.IndexOf(RuleCodes, model.RepeatRule));

        var start = ToLocal(model.StartAt);
        var due = ToLocal(model.DueAt);

        EditStartDate = start?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        EditStartTime = start?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "09:00";
        EditDueDate = due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        EditDueTime = due?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "10:00";

        IsDetailVisible = true;
        SaveDetailCommand.NotifyCanExecuteChanged();
    }

    // ---------------- 导航 ----------------

    [RelayCommand]
    private void ShowToday()
    {
        SelectedDate = DateTime.Today;
        DisplayMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        Mode = ViewMode.Today;
        Load();
    }

    [RelayCommand]
    private void ShowAll()
    {
        Mode = ViewMode.All;
        Load();
    }

    [RelayCommand]
    private void ShowDone()
    {
        Mode = ViewMode.Done;
        Load();
    }

    [RelayCommand]
    private void ShowDaySegment()
    {
        Mode = ViewMode.Day;
        Load();
    }

    [RelayCommand]
    private void ShowWeekSegment()
    {
        Mode = ViewMode.Week;
        Load();
    }

    [RelayCommand]
    private void ShowMonthSegment()
    {
        Mode = ViewMode.Month;
        Load();
    }

    [RelayCommand]
    private void ShowYearSegment()
    {
        Mode = ViewMode.Year;
        Load();
    }

    [RelayCommand]
    private void ShowFocusSegment()
    {
        Mode = ViewMode.Focus;
        Load();
    }

    /// <summary>课表分段：数据由 ScheduleViewModel 自己按教学周展开，不与任务列表混在一起。</summary>
    [RelayCommand]
    private void ShowScheduleSegment()
    {
        Mode = ViewMode.Schedule;
        Load();
    }

    [RelayCommand]
    private void SelectDay(CalendarDayViewModel? day)
    {
        if (day is null) return;

        SelectedDate = day.Date;
        DisplayMonth = new DateTime(day.Date.Year, day.Date.Month, 1);

        if (Mode is not (ViewMode.Week or ViewMode.Month)) Mode = ViewMode.Day;

        Load();
    }

    /// <summary>左面板键盘导航：按天 / 按周移动当前选中日期，主面板跟着换那一天。</summary>
    public void MoveSelectedDate(int days)
    {
        if (Mode is ViewMode.Focus or ViewMode.Schedule) return;

        SelectedDate = SelectedDate.Date.AddDays(days);
        DisplayMonth = new DateTime(SelectedDate.Year, SelectedDate.Month, 1);

        // 今天视图按一下方向键就落到具体某天；周 / 月 / 年保持自己的视图
        if (Mode is not (ViewMode.Week or ViewMode.Month or ViewMode.Year)) Mode = ViewMode.Day;

        Load();
    }

    [RelayCommand]
    private void SelectMonthCell(MonthCellViewModel? cell)
    {
        if (cell is null) return;

        SelectedDate = cell.Date;
        DisplayMonth = new DateTime(cell.Date.Year, cell.Date.Month, 1);
        Mode = ViewMode.Day;
        Load();
    }

    /// <summary>年视图里点某天：直接跳到那一天的日视图。</summary>
    [RelayCommand]
    private void SelectYearDay(YearDayViewModel? day)
    {
        if (day is null || day.IsOtherMonth) return;

        SelectedDate = day.Date;
        DisplayMonth = new DateTime(day.Date.Year, day.Date.Month, 1);
        Mode = ViewMode.Day;
        Load();
    }

    /// <summary>年视图里点月份标题：展开成该月的月视图。</summary>
    [RelayCommand]
    private void SelectYearMonth(YearMonthViewModel? month)
    {
        if (month is null) return;

        DisplayMonth = new DateTime(month.Year, month.Month, 1);
        Mode = ViewMode.Month;
        Load();
    }

    [RelayCommand]
    private void PreviousMonth()
    {
        DisplayMonth = Mode == ViewMode.Year ? DisplayMonth.AddYears(-1) : DisplayMonth.AddMonths(-1);
        Load();
    }

    [RelayCommand]
    private void NextMonth()
    {
        DisplayMonth = Mode == ViewMode.Year ? DisplayMonth.AddYears(1) : DisplayMonth.AddMonths(1);
        Load();
    }

    // ---------------- 任务操作 ----------------

    [RelayCommand]
    private void SelectTask(TaskViewModel? vm)
    {
        if (vm is null) return;
        IsNewTask = false;
        SelectedTask = vm;
    }

    [RelayCommand]
    private void CloseDetail()
    {
        IsNewTask = false;
        SelectedTask = null;
    }

    [RelayCommand]
    private void NewTask()
    {
        var now = DateTime.Now;
        var start = SelectedDate.Date == now.Date
            ? now.Date.AddHours(now.Hour + 1)
            : SelectedDate.Date.AddHours(9);

        var draft = new TaskItem
        {
            Title = string.Empty,
            StartAt = ToMs(start),
            DueAt = ToMs(start.AddHours(1)),
            RepeatRule = RepeatRules.None
        };

        IsNewTask = true;
        SelectedTask = new TaskViewModel(draft);
    }

    private bool CanSaveDetail() => !string.IsNullOrWhiteSpace(EditTitle);

    /// <summary>时间框失焦时调用：把输入补全成 HH:mm，并把早于起始的截止时刻拉齐。</summary>
    public void NormalizeEditTimes()
    {
        if (!IsTimed) return;

        var start = NormalizeTimeText(EditStartTime);
        if (start is not null) EditStartTime = start;

        var due = NormalizeTimeText(EditDueTime);
        if (due is not null) EditDueTime = due;

        SyncDueWithStart();
    }

    /// <summary>截止时间不得早于起始时间，冲突时直接拉到起始时刻（日期一同跟上）。</summary>
    private void SyncDueWithStart()
    {
        if (!IsTimed) return;

        var start = ParseLocal(EditStartDate, EditStartTime);
        var due = ParseLocal(EditDueDate, EditDueTime);
        if (start is null || due is null || due >= start) return;

        EditDueDate = start.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        EditDueTime = start.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    [RelayCommand(CanExecute = nameof(CanSaveDetail))]
    private void SaveDetail()
    {
        if (SelectedTask is null) return;

        var model = SelectedTask.Model;

        model.Title = EditTitle.Trim();
        model.Note = string.IsNullOrWhiteSpace(EditNote) ? null : EditNote.Trim();
        model.Location = string.IsNullOrWhiteSpace(EditLocation) ? null : EditLocation.Trim();
        model.AllDay = EditAllDay;
        model.RepeatRule = RuleCodes[Math.Clamp(EditRepeatIndex, 0, RuleCodes.Length - 1)];

        // 兜底补全：即使没触发失焦也按 HH:mm 解析「930」这类输入
        var start = ParseLocal(EditStartDate, NormalizeTimeText(EditStartTime) ?? EditStartTime);
        var due = ParseLocal(EditDueDate, NormalizeTimeText(EditDueTime) ?? EditDueTime);

        if (model.AllDay)
        {
            start = start?.Date;
            due = due?.Date;
        }

        // 截止不得早于起始：冲突时拉齐到起始时刻
        if (start is not null && due is not null && due < start) due = start;

        model.StartAt = start is null ? null : ToMs(start.Value);
        model.DueAt = due is null ? null : ToMs(due.Value);

        // 提醒跟着起始时间走；没有起始时间则用截止时间
        model.RemindAt = model.StartAt ?? model.DueAt;
        model.RemindedAt = null;
        model.Touch();

        _store.Upsert(model);

        if (IsNewTask)
        {
            // 新建保存后表单立即清空回默认值，方便接着录下一条
            Load();
            NewTask();
            return;
        }

        IsNewTask = false;
        Load();
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedTask is null) return;

        if (IsNewTask)
        {
            CloseDetail();
            return;
        }

        var model = SelectedTask.Model;
        model.Deleted = true;
        model.Touch();
        _store.Upsert(model);

        IsNewTask = false;
        SelectedTask = null;
        Load();
    }

    [RelayCommand]
    private void ToggleDone(TaskViewModel? vm)
    {
        if (vm is null) return;

        var model = vm.Model;

        // 重复任务勾选 = 完成本次并推进到下一次，否则每天的任务点一次就永久消失
        if (!model.Done && RepeatRules.IsRepeating(model.RepeatRule))
        {
            _undoStartAt = model.StartAt;
            _undoDueAt = model.DueAt;
            _undoRemindAt = model.RemindAt;
            _undoWasAdvance = true;

            if (ReminderPlanner.AdvanceToNextOccurrence(model, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                _store.Upsert(model);
                vm.Refresh();

                _pendingUndo = vm;
                UndoText = $"已完成本次「{vm.Title}」，下次 {vm.DueText}";
                IsUndoVisible = true;
                _undoTimer.Stop();
                _undoTimer.Start();
                return;
            }

            _undoWasAdvance = false;
        }

        model.Done = !model.Done;
        model.Touch();
        _store.Upsert(model);
        vm.Refresh();

        if (!model.Done)
        {
            Load();
            return;
        }

        _pendingUndo = vm;
        UndoText = $"已完成「{vm.Title}」";
        IsUndoVisible = true;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    /// <summary>一键完成按钮：今天 / 全部视图有逾期就先清逾期，否则清选中日。</summary>
    public bool CanBulkDone => BulkDonePlan().Items.Count > 0;

    public string BulkDoneText
    {
        get
        {
            var plan = BulkDonePlan();
            return $"{plan.Text}（{plan.Items.Count}）";
        }
    }

    private (string Text, List<TaskItem> Items) BulkDonePlan()
    {
        if (Mode is ViewMode.Done or ViewMode.Focus or ViewMode.Schedule) return (string.Empty, []);

        var active = _all.Where(t => !t.Deleted && !t.Done).ToList();

        // 今天 / 全部的列表里逾期单独成组，先让按钮清它
        if (Mode is ViewMode.Today or ViewMode.All)
        {
            var overdue = active.Where(IsOverdue).ToList();
            if (overdue.Count > 0) return ("完成逾期", overdue);
        }

        var day = active.Where(t => LocalDateOf(t) == SelectedDate.Date).ToList();
        var text = SelectedDate.Date == DateTime.Today ? "完成本日" : $"完成 {SelectedDate:M月d日}";
        return (text, day);
    }

    /// <summary>一键完成：重复任务推进到下一次，其余直接打勾；两秒内可撤销。</summary>
    [RelayCommand]
    private void CompleteListed()
    {
        var plan = BulkDonePlan();
        if (plan.Items.Count == 0) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshot = new List<(string Id, long? StartAt, long? DueAt, long? RemindAt)>(plan.Items.Count);

        foreach (var model in plan.Items)
        {
            snapshot.Add((model.Id, model.StartAt, model.DueAt, model.RemindAt));

            if (!RepeatRules.IsRepeating(model.RepeatRule)
                || !ReminderPlanner.AdvanceToNextOccurrence(model, now))
                model.Done = true;

            model.Touch();
            _store.Upsert(model);
        }

        _pendingUndo = null;
        _bulkUndo = snapshot;
        UndoText = $"已完成 {plan.Items.Count} 项";
        IsUndoVisible = true;
        _undoTimer.Stop();
        _undoTimer.Start();

        Load();
    }

    [RelayCommand]
    private void Undo()
    {
        _undoTimer.Stop();
        IsUndoVisible = false;

        if (_bulkUndo is not null)
        {
            var byId = _store.GetAll().ToDictionary(t => t.Id);

            foreach (var (id, startAt, dueAt, remindAt) in _bulkUndo)
            {
                if (!byId.TryGetValue(id, out var model)) continue;

                model.Done = false;
                model.StartAt = startAt;
                model.DueAt = dueAt;
                model.RemindAt = remindAt;
                model.Touch();
                _store.Upsert(model);
            }

            _bulkUndo = null;
        }

        if (_pendingUndo is not null)
        {
            var model = _pendingUndo.Model;

            if (_undoWasAdvance)
            {
                model.StartAt = _undoStartAt;
                model.DueAt = _undoDueAt;
                model.RemindAt = _undoRemindAt;
            }
            else
            {
                model.Done = false;
            }

            model.Touch();
            _store.Upsert(model);
            _pendingUndo = null;
        }

        _undoWasAdvance = false;
        Load();
    }

    /// <summary>设置面板里的深色开关：一改就应用并记住，重启后仍生效。</summary>
    public bool IsDarkTheme
    {
        get => IsDark;
        set
        {
            if (IsDark == value) return;

            IsDark = value;
            ThemeService.Apply(value);
            _store.SetMeta("theme", value ? "dark" : "light");
            OnPropertyChanged(nameof(IsDarkTheme));
        }
    }

    /// <summary>开机自启开关：直接读写当前用户的 Run 项。</summary>
    public bool IsAutoStart
    {
        get => AutoStartService.IsEnabled;
        set
        {
            if (value == IsAutoStart) return;

            if (value) AutoStartService.Enable();
            else AutoStartService.Disable();

            OnPropertyChanged(nameof(IsAutoStart));
        }
    }

    [RelayCommand]
    private async Task Sync()
    {
        if (_sync is null)
        {
            SyncText = "同步客户端未初始化";
            return;
        }

        if (!await HasAccountAsync())
        {
            SyncText = "还没有设置同步账号";
            AccountRequired?.Invoke();
            return;
        }

        SyncText = "正在同步…";
        try
        {
            // 一次往返：把本地改动推上去，同时把另一端的新改动拉回来
            var stats = await _sync.SyncAsync();
            SyncText = stats.Pulled == 0 && stats.Rejected == 0
                ? $"已同步 · 上传 {stats.Accepted} 条 · {DateTime.Now:HH:mm}"
                : $"已同步 · 上传 {stats.Accepted} / 拉取 {stats.Pulled} 条 · {DateTime.Now:HH:mm}";
            Load();
        }
        catch (Exception ex)
        {
            SyncText = $"同步失败：{ex.Message}";
        }
    }

    /// <summary>同步前确认电脑上已经有账号；数据库连不上时不当作「没账号」，让真正的错误显示出来。</summary>
    private static async Task<bool> HasAccountAsync()
    {
        var users = UserStore.Create();
        if (users is null) return false;

        try
        {
            return !string.IsNullOrWhiteSpace(await users.GetNameAsync());
        }
        catch
        {
            return true;
        }
    }

    // ---------------- 加载与构建 ----------------

    /// <summary>上次构建月视图时用的数据指纹与日期，一致就跳过整月重建。</summary>
    private string _monthSignature = string.Empty;
    private DateTime _monthKeyDate = DateTime.MinValue;
    private DateTime _monthKeyMonth = DateTime.MinValue;

    /// <summary>上次构建年视图时用的数据指纹、年份与选中日，一致就跳过整年重建。</summary>
    private string _yearSignature = string.Empty;
    private int _yearKeyYear = int.MinValue;
    private DateTime _yearKeyDate = DateTime.MinValue;

    /// <summary>上次构建分组列表时用的数据指纹与模式，一致就跳过分组重建。</summary>
    private string _groupsSignature = string.Empty;
    private ViewMode? _groupsKeyMode;
    private DateTime _groupsKeyDate = DateTime.MinValue;

    /// <summary>上次构建周视图时用的数据指纹与所选日期，一致就跳过重建。</summary>
    private string _weekSignature = string.Empty;
    private DateTime _weekKeyDate = DateTime.MinValue;

    /// <summary>上次构建时间轴时用的数据指纹、日期与模式，一致就跳过重建。</summary>
    private string _timelineSignature = string.Empty;
    private DateTime _timelineKeyDate = DateTime.MinValue;
    private ViewMode _timelineKeyMode = ViewMode.Day;

    /// <summary>课表数据版本：每次导入 / 清空加一，时间轴据此重建课程带。</summary>
    private int _scheduleRevision;
    private int _timelineKeySchedule;

    public void Load()
    {
        _all = _store.GetAll();
        IsDark = string.Equals(_store.GetMeta("theme"), "dark", StringComparison.OrdinalIgnoreCase);

        var selectedId = SelectedTask?.Id;
        var wasNew = IsNewTask;

        BuildDays();

        // 数据没变就不重建条目：重建会丢掉已有容器，切换分段时整页要重新测量，明显卡一下
        var signature = string.Join("|",
            _all.Count, _all.Sum(t => t.UpdatedAt), _all.Count(t => t.Deleted));

        if (Mode == ViewMode.Month)
        {
            if (signature != _monthSignature || SelectedDate.Date != _monthKeyDate || DisplayMonth != _monthKeyMonth)
            {
                BuildMonth();
                _monthSignature = signature;
                _monthKeyDate = SelectedDate.Date;
                _monthKeyMonth = DisplayMonth;
            }
        }
        else if (Mode == ViewMode.Year)
        {
            if (signature != _yearSignature
                || DisplayMonth.Year != _yearKeyYear
                || SelectedDate.Date != _yearKeyDate)
            {
                BuildYear();
                _yearSignature = signature;
                _yearKeyYear = DisplayMonth.Year;
                _yearKeyDate = SelectedDate.Date;
            }

            IsEmpty = false;
        }
        else if (Mode == ViewMode.Focus)
        {
            Groups.Clear();
            IsEmpty = false;
            _groupsKeyMode = null;
        }
        else if (Mode == ViewMode.Schedule)
        {
            // 课表数据不来自任务库，别让分组列表 / 空态来插一脚
            Groups.Clear();
            IsEmpty = false;
            _groupsKeyMode = null;
        }
        else if (Mode == ViewMode.Week)
        {
            if (signature != _weekSignature || SelectedDate.Date != _weekKeyDate)
            {
                BuildGroups(WeekGroups);
                _weekSignature = signature;
                _weekKeyDate = SelectedDate.Date;
            }
        }
        else if (Mode is ViewMode.Today or ViewMode.Day)
        {
            if (signature != _timelineSignature
                || TimelineDate != _timelineKeyDate
                || Mode != _timelineKeyMode
                || _scheduleRevision != _timelineKeySchedule)
            {
                BuildTimeline();
                _timelineSignature = signature;
                _timelineKeyDate = TimelineDate;
                _timelineKeyMode = Mode;
                _timelineKeySchedule = _scheduleRevision;
            }

            // 时间轴自己撑满，空态文案由上面的「未排时间」占位负责
            IsEmpty = false;
            UpdateTimelineNow();
        }
        else
        {
            if (signature != _groupsSignature || SelectedDate.Date != _groupsKeyDate || Mode != _groupsKeyMode)
            {
                BuildGroups(Groups);
                _groupsSignature = signature;
                _groupsKeyDate = SelectedDate.Date;
                _groupsKeyMode = Mode;
            }
        }

        Count();
        UpdateHeader();
        NotifyModeFlags();

        if (!wasNew && selectedId is not null)
        {
            var model = _all.FirstOrDefault(t => t.Id == selectedId && !t.Deleted);
            SelectedTask = model is null ? null : new TaskViewModel(model);
        }
    }

    private void NotifyModeFlags()
    {
        OnPropertyChanged(nameof(IsTimelineView));
        OnPropertyChanged(nameof(IsListView));
        OnPropertyChanged(nameof(IsWeekView));
        OnPropertyChanged(nameof(IsMonthView));
        OnPropertyChanged(nameof(IsYearView));
        OnPropertyChanged(nameof(IsFocusView));
        OnPropertyChanged(nameof(IsScheduleView));
        OnPropertyChanged(nameof(IsGroupedView));
        OnPropertyChanged(nameof(IsDaySegment));
        OnPropertyChanged(nameof(IsWeekSegment));
        OnPropertyChanged(nameof(IsMonthSegment));
        OnPropertyChanged(nameof(IsYearSegment));
        OnPropertyChanged(nameof(IsFocusSegment));
        OnPropertyChanged(nameof(IsScheduleSegment));
        OnPropertyChanged(nameof(IsTodayActive));
        OnPropertyChanged(nameof(IsAllActive));
        OnPropertyChanged(nameof(IsDoneActive));
        OnPropertyChanged(nameof(IsScheduleActive));
        OnPropertyChanged(nameof(CanBulkDone));
        OnPropertyChanged(nameof(BulkDoneText));
    }

    private void BuildDays()
    {
        MonthTitle = DisplayMonth.ToString("yyyy年M月", Zh);

        var taskDates = _all
            .Where(t => !t.Deleted)
            .Select(LocalDateOf)
            .Where(d => d is not null)
            .Select(d => d!.Value)
            .ToHashSet();

        var start = MondayOf(new DateTime(DisplayMonth.Year, DisplayMonth.Month, 1));

        // 月份没变就只改状态，不重建 42 个格子：重建会让侧栏日历整批换容器，切换视图时明显卡一下
        if (Days.Count == 42 && Days[0].Date == start)
        {
            for (var i = 0; i < 42; i++)
            {
                var day = Days[i];
                day.IsOtherMonth = day.Date.Month != DisplayMonth.Month;
                day.IsToday = day.Date == DateTime.Today;
                day.IsSelected = day.Date == SelectedDate.Date;
                day.HasTasks = taskDates.Contains(day.Date);
            }

            return;
        }

        Days.Clear();
        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            Days.Add(new CalendarDayViewModel(date)
            {
                IsOtherMonth = date.Month != DisplayMonth.Month,
                IsToday = date == DateTime.Today,
                IsSelected = date == SelectedDate.Date,
                HasTasks = taskDates.Contains(date)
            });
        }
    }

    /// <summary>年视图：12 个迷你月历，只有日期数字与「这天有任务」的小圆点。</summary>
    private void BuildYear()
    {
        var year = DisplayMonth.Year;

        // 只统计本年、未删除的任务，按本地日期归堆
        var counts = new Dictionary<DateTime, int>();
        foreach (var task in _all)
        {
            if (task.Deleted) continue;
            if (LocalDateOf(task) is not DateTime date || date.Year != year) continue;
            counts[date] = counts.TryGetValue(date, out var n) ? n + 1 : 1;
        }

        YearMonths.Clear();

        for (var month = 1; month <= 12; month++)
        {
            var start = MondayOf(new DateTime(year, month, 1));
            var days = new List<YearDayViewModel>(42);
            var total = 0;

            for (var i = 0; i < 42; i++)
            {
                var date = start.AddDays(i);
                var other = date.Month != month;
                var count = counts.TryGetValue(date, out var n) ? n : 0;
                if (!other) total += count;

                days.Add(new YearDayViewModel(
                    date,
                    isOtherMonth: other,
                    isToday: date == DateTime.Today,
                    isSelected: date == SelectedDate.Date,
                    taskCount: count));
            }

            YearMonths.Add(new YearMonthViewModel(year, month, days, total));
        }
    }

    private void BuildGroups(ObservableCollection<TaskGroup> target)
    {
        var visible = _all.Where(t => !t.Deleted).ToList();
        var active = visible.Where(t => !t.Done).ToList();
        var done = visible.Where(t => t.Done).ToList();
        var today = DateTime.Today;

        var groups = new List<TaskGroup>();

        void Add(string header, IEnumerable<TaskItem> source, string brush = "text-secondary",
            bool keepEmpty = false, string emptyHint = "")
        {
            var items = source
                .OrderBy(t => t.AnchorAt ?? long.MaxValue)
                .ThenBy(t => t.Title, StringComparer.CurrentCulture)
                .Select(t => new TaskViewModel(t, Mode == ViewMode.Week))
                .ToList();

            if (items.Count == 0 && !keepEmpty) return;

            groups.Add(new TaskGroup
            {
                Header = header,
                HeaderBrushKey = brush,
                Items = items,
                EmptyHint = emptyHint
            });
        }

        switch (Mode)
        {
            case ViewMode.Today:
                Add("逾期", active.Where(IsOverdue), "danger");
                Add("今天", active.Where(t => LocalDateOf(t) == today));
                Add("无日期", active.Where(t => LocalDateOf(t) is null));
                Add("今日已完成", done.Where(t => LocalDateOf(t) == today));
                break;

            case ViewMode.Day:
                Add(string.Empty, active.Concat(done).Where(t => LocalDateOf(t) == SelectedDate.Date), keepEmpty: true);
                break;

            case ViewMode.Week:
            {
                var monday = MondayOf(SelectedDate);
                for (var i = 0; i < 7; i++)
                {
                    var day = monday.AddDays(i);
                    Add($"{WeekName(day)} · {day:M月d日}",
                        visible.Where(t => LocalDateOf(t) == day),
                        day == today ? "accent" : "text-secondary",
                        keepEmpty: true,
                        emptyHint: "暂无安排");
                }
                break;
            }

            case ViewMode.All:
                Add("逾期", active.Where(IsOverdue), "danger");
                Add("今天", active.Where(t => LocalDateOf(t) == today));
                Add("明天", active.Where(t => LocalDateOf(t) == today.AddDays(1)));
                Add("未来 7 天", active.Where(t => LocalDateOf(t) is DateTime d && d > today.AddDays(1) && d <= today.AddDays(7)));
                Add("更晚", active.Where(t => LocalDateOf(t) is DateTime d && d > today.AddDays(7)));
                Add("无日期", active.Where(t => LocalDateOf(t) is null));
                Add("已完成", done);
                break;

            case ViewMode.Done:
                Add("已完成", done, keepEmpty: true);
                break;
        }

        target.Clear();
        foreach (var group in groups) target.Add(group);

        IsEmpty = Mode != ViewMode.Week && !groups.Any(g => g.Items.Count > 0);
    }

    /// <summary>日视图：没有具体时刻的日程列在轴上方，带时刻的按开始时间摆到天空色时间轴上。</summary>
    private void BuildTimeline()
    {
        var date = TimelineDate;
        var visible = _all.Where(t => !t.Deleted).ToList();

        TimelineGroups.Clear();

        void Add(string header, IEnumerable<TaskItem> source, string brush, string emptyHint = "")
        {
            var items = source
                .OrderBy(t => t.AnchorAt ?? long.MaxValue)
                .ThenBy(t => t.Title, StringComparer.CurrentCulture)
                .Select(t => new TaskViewModel(t))
                .ToList();

            if (items.Count == 0 && emptyHint.Length == 0) return;

            TimelineGroups.Add(new TaskGroup
            {
                Header = header,
                HeaderBrushKey = brush,
                Items = items,
                EmptyHint = emptyHint
            });
        }

        // 今天视图保留「逾期」入口，否则逾期任务在日视图里就看不到了
        if (Mode == ViewMode.Today) Add("逾期", visible.Where(t => !t.Done && IsOverdue(t)), "danger");

        var dayTasks = visible.Where(t => LocalDateOf(t) == date).ToList();

        // 全天事件不占时刻，铺在时间轴右侧那条竖带里，从轴顶拉到轴底
        AllDayItems.Clear();
        foreach (var task in dayTasks
                     .Where(t => t.AllDay)
                     .OrderBy(t => t.AnchorAt ?? long.MaxValue)
                     .ThenBy(t => t.Title, StringComparer.CurrentCulture))
        {
            AllDayItems.Add(new TaskViewModel(task));
        }

        OnPropertyChanged(nameof(HasAllDayItems));

        // 全天事件已经在竖带里了，这里只剩真没填时刻的
        Add("未排时间", dayTasks.Where(t => !t.AllDay && t.StartAt is null), "text-secondary");

        // 有开始时刻的才上时间轴；没填截止就按半小时算，跨天也保留真实时长
        var scheduled = new List<(TaskItem Task, double Start, double End)>();
        foreach (var task in dayTasks)
        {
            if (task.AllDay || ToLocal(task.StartAt) is not DateTime start) continue;

            var end = ToLocal(task.DueAt) ?? start.AddMinutes(30);
            if (end <= start) end = start.AddMinutes(30);

            scheduled.Add((task, (start - date).TotalMinutes, (end - date).TotalMinutes));
        }

        // 左边课程带：只取当天的课；时刻解析不出来就跳过，不让一条脏数据毁掉整条轴
        var courses = new List<(ScheduleEvent Course, double Start, double End)>();
        var dayKey = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var course in ScheduleStore.Instance.Events)
        {
            if (course.Date != dayKey) continue;
            if (!TryMinutes(course.Start, out var courseStart)) continue;

            var courseEnd = TryMinutes(course.End, out var parsedEnd) && parsedEnd > courseStart
                ? parsedEnd
                : courseStart + 90;

            courses.Add((course, courseStart, courseEnd));
        }

        // 默认 6 点到 23 点，有超出这个区间的日程 / 课程就把时间轴撑到能看见它
        var startHour = 6;
        var endHour = 23;

        foreach (var (_, start, end) in scheduled)
        {
            startHour = Math.Min(startHour, (int)Math.Floor(start / 60));
            endHour = Math.Max(endHour, (int)Math.Ceiling(end / 60));
        }

        foreach (var (_, start, end) in courses)
        {
            startHour = Math.Min(startHour, (int)Math.Floor(start / 60));
            endHour = Math.Max(endHour, (int)Math.Ceiling(end / 60));
        }

        TimelineStartHour = Math.Clamp(startHour, 0, 23);
        TimelineEndHour = Math.Clamp(endHour, TimelineStartHour + 1, 24);

        // 课程带：和日程块共用同一套刻度，两边的时间才对得上
        DayCourses.Clear();
        foreach (var (course, courseStart, courseEnd) in courses.OrderBy(c => c.Start))
        {
            DayCourses.Add(new DayCourseBlockViewModel(
                course,
                top: (courseStart / 60 - TimelineStartHour) * TimelineHourHeight,
                height: Math.Max(26, (courseEnd - courseStart) / 60 * TimelineHourHeight)));
        }

        OnPropertyChanged(nameof(IsCourseLaneEmpty));

        TimelineBlocks.Clear();

        // 重叠的日程并排摆：先切出互相重叠的一簇，再在簇内贪心分列，避免全轴都被压窄
        var ordered = scheduled.OrderBy(s => s.Start).ThenBy(s => s.End).ToList();
        for (var first = 0; first < ordered.Count;)
        {
            var clusterEnd = ordered[first].End;
            var last = first + 1;
            while (last < ordered.Count && ordered[last].Start < clusterEnd)
            {
                clusterEnd = Math.Max(clusterEnd, ordered[last].End);
                last++;
            }

            var laneEnds = new List<double>();
            var lanes = new int[last - first];

            for (var i = first; i < last; i++)
            {
                var lane = laneEnds.FindIndex(e => e <= ordered[i].Start);
                if (lane < 0)
                {
                    laneEnds.Add(0);
                    lane = laneEnds.Count - 1;
                }

                laneEnds[lane] = ordered[i].End;
                lanes[i - first] = lane;
            }

            for (var i = first; i < last; i++)
            {
                var (task, start, end) = ordered[i];

                TimelineBlocks.Add(new TimelineBlockViewModel(
                    new TaskViewModel(task),
                    top: (start / 60 - TimelineStartHour) * TimelineHourHeight,
                    height: Math.Max(26, (end - start) / 60 * TimelineHourHeight),
                    lane: lanes[i - first],
                    laneCount: laneEnds.Count));
            }

            first = last;
        }
    }

    /// <summary>把课表里的「08:30」换成从零点起的分钟数。</summary>
    private static bool TryMinutes(string? text, out double minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!TimeSpan.TryParse(text.Trim(), CultureInfo.InvariantCulture, out var span)) return false;
        if (span < TimeSpan.Zero || span >= TimeSpan.FromDays(1)) return false;

        minutes = span.TotalMinutes;
        return true;
    }

    private void BuildMonth()
    {
        var visible = _all.Where(t => !t.Deleted).ToList();
        var start = MondayOf(new DateTime(DisplayMonth.Year, DisplayMonth.Month, 1));

        _monthByDate = visible
            .Where(t => LocalDateOf(t) is not null)
            .GroupBy(t => LocalDateOf(t)!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.AnchorAt ?? long.MaxValue).ToList());

        MonthWeeks.Clear();

        var total = 0;
        for (var w = 0; w < 6; w++)
        {
            var week = new MonthWeekViewModel(w);
            var expanded = _expandedWeeks.Contains(w);
            var canExpand = false;
            var maxCount = 0;

            for (var d = 0; d < 7; d++)
            {
                var date = start.AddDays(w * 7 + d);
                _monthByDate.TryGetValue(date, out var dayTasks);
                var count = dayTasks?.Count ?? 0;

                if (date.Month == DisplayMonth.Month) total += count;
                if (count > MonthItemLimit) canExpand = true;
                if (count > maxCount) maxCount = count;

                week.Cells.Add(CreateMonthCell(date, dayTasks, expanded));
            }

            week.CanExpand = canExpand;
            week.MaxCount = maxCount;
            week.IsExpanded = expanded;
            week.RowHeight = RowHeightFor(week);
            MonthWeeks.Add(week);
        }

        IsEmpty = total == 0;
        NotifyWeekToggleState();
    }

    private MonthCellViewModel CreateMonthCell(DateTime date, List<TaskItem>? dayTasks, bool expanded)
    {
        dayTasks ??= [];

        var visible = expanded ? dayTasks.Count : Math.Min(dayTasks.Count, MonthItemLimit);

        return new MonthCellViewModel(
            date,
            date.Month != DisplayMonth.Month,
            date == DateTime.Today,
            date == SelectedDate.Date,
            dayTasks.Select(t => new TaskViewModel(t)).ToList(),
            visible * MonthItemHeight);
    }

    /// <summary>整行高度：收起时至少占满均分行高，展开时按当天最多的条目数撑开。</summary>
    private double RowHeightFor(MonthWeekViewModel week)
    {
        var rows = week.IsExpanded ? week.MaxCount : Math.Min(week.MaxCount, MonthItemLimit);
        return Math.Max(MonthRowHeight, MonthCellChrome + rows * MonthItemHeight);
    }

    /// <summary>按展开状态重算整行高度与每格条目区高度，交给界面做过渡动画。</summary>
    private void ApplyWeekLayout(MonthWeekViewModel week)
    {
        foreach (var cell in week.Cells)
        {
            var rows = week.IsExpanded ? cell.Items.Count : Math.Min(cell.Items.Count, MonthItemLimit);
            cell.ContentHeight = rows * MonthItemHeight;
        }

        week.RowHeight = RowHeightFor(week);
    }

    /// <summary>窗口尺寸变化后重新校准月视图行高与每日显示上限。</summary>
    public void UpdateMonthMetrics(double rowHeight, int itemLimit)
    {
        var height = Math.Max(56, Math.Round(rowHeight));
        var limit = Math.Clamp(itemLimit, 1, 12);

        if (Math.Abs(height - MonthRowHeight) < 0.5 && limit == MonthItemLimit) return;

        MonthRowHeight = height;
        MonthItemLimit = limit;

        if (!IsMonthView) return;

        // 只按新行高重排已有格子，不重建 MonthWeeks：重建等于把 42 个格子连同日程条目全部重做一遍
        var start = MondayOf(new DateTime(DisplayMonth.Year, DisplayMonth.Month, 1));
        if (MonthWeeks.Count != 6 || MonthWeeks[0].Cells.Count != 7 || MonthWeeks[0].Cells[0].Date != start)
        {
            BuildMonth();
            return;
        }

        foreach (var week in MonthWeeks)
        {
            week.CanExpand = week.Cells.Any(c => c.Items.Count > MonthItemLimit);
            ApplyWeekLayout(week);
        }
    }

    /// <summary>展开 / 收起整周的全部日程。</summary>
    [RelayCommand]
    private void ToggleWeek(MonthWeekViewModel? week)
    {
        if (week is null || !week.CanExpand) return;

        week.IsExpanded = !week.IsExpanded;

        if (week.IsExpanded) _expandedWeeks.Add(week.Index);
        else _expandedWeeks.Remove(week.Index);

        // 不重建 Cells：条目始终完整，靠条目区高度过渡来露出 / 遮住多出来的部分
        ApplyWeekLayout(week);
        NotifyWeekToggleState();
    }

    /// <summary>一键展开 / 收起月视图里所有可展开的周。</summary>
    [RelayCommand]
    private void ToggleAllWeeks()
    {
        var expand = MonthWeeks.Any(w => w.CanExpand && !w.IsExpanded);

        _expandedWeeks.Clear();
        if (expand)
        {
            foreach (var week in MonthWeeks.Where(w => w.CanExpand)) _expandedWeeks.Add(week.Index);
        }

        foreach (var week in MonthWeeks)
        {
            var target = expand && week.CanExpand;
            if (week.IsExpanded == target) continue;

            week.IsExpanded = target;
            ApplyWeekLayout(week);
        }

        NotifyWeekToggleState();
    }

    private void NotifyWeekToggleState()
    {
        OnPropertyChanged(nameof(HasExpandableWeek));
        OnPropertyChanged(nameof(WeekToggleAllText));
    }

    private void Count()
    {
        var visible = _all.Where(t => !t.Deleted).ToList();
        var active = visible.Where(t => !t.Done).ToList();

        TodayCount = active.Count(t => LocalDateOf(t) == DateTime.Today);
        AllCount = active.Count;
        DoneCount = visible.Count(t => t.Done);
    }

    private void UpdateHeader()
    {
        switch (Mode)
        {
            case ViewMode.Today:
                MainTitle = "今天";
                MainSubtitle = $"{DateTime.Today.ToString("M月d日 dddd", Zh)} · {TodayCount} 项未完成";
                break;

            case ViewMode.Day:
            {
                var count = _all.Count(t => !t.Deleted && LocalDateOf(t) == SelectedDate.Date);
                MainTitle = SelectedDate.ToString("M月d日", Zh);
                MainSubtitle = $"{SelectedDate.ToString("dddd", Zh)} · {count} 项";
                break;
            }

            case ViewMode.Week:
            {
                var monday = MondayOf(SelectedDate);
                var sunday = monday.AddDays(6);
                var count = _all.Count(t => !t.Deleted && LocalDateOf(t) is DateTime d && d >= monday && d <= sunday);
                MainTitle = $"{monday.ToString("M月d日", Zh)} – {sunday.ToString("M月d日", Zh)}";
                MainSubtitle = $"{monday.ToString("dddd", Zh)}起 · {count} 项";
                break;
            }

            case ViewMode.Month:
            {
                var count = _all.Count(t => !t.Deleted && LocalDateOf(t) is DateTime d && d.Month == DisplayMonth.Month && d.Year == DisplayMonth.Year);
                MainTitle = DisplayMonth.ToString("yyyy年M月", Zh);
                MainSubtitle = $"{count} 项";
                break;
            }

            case ViewMode.Year:
            {
                var count = _all.Count(t => !t.Deleted && LocalDateOf(t) is DateTime d && d.Year == DisplayMonth.Year);
                MainTitle = $"{DisplayMonth.Year} 年";
                MainSubtitle = $"{count} 项";
                break;
            }

            case ViewMode.All:
                MainTitle = "全部";
                MainSubtitle = $"{AllCount} 项未完成";
                break;

            case ViewMode.Done:
                MainTitle = "已完成";
                MainSubtitle = $"{DoneCount} 项";
                break;

            case ViewMode.Focus:
                MainTitle = "专注";
                MainSubtitle = "倒计时 · 秒表 · 闹钟";
                break;

            case ViewMode.Schedule:
                MainTitle = "课表";
                MainSubtitle = $"{Schedule.Term} · 第 1 周 {Schedule.Week1Monday} · 共 {Schedule.EventsCount} 节课";
                break;
        }
    }

    // ---------------- 工具 ----------------

    private static bool IsOverdue(TaskItem t) =>
        LocalDateOf(t) is DateTime d && d < DateTime.Today;

    private static DateTime? LocalDateOf(TaskItem t) =>
        t.AnchorAt is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().Date : null;

    private static DateTime? ToLocal(long? utcMs) =>
        utcMs is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().DateTime : null;

    private static long ToMs(DateTime local) => new DateTimeOffset(local).ToUnixTimeMilliseconds();

    /// <summary>
    /// 把「9」「930」「14:30」这类输入补全成 HH:mm：不足四位时按「时 + 分」右补 0，
    /// 两位数字优先当时，超出 23 时退化为「时 + 分十位」。无效输入返回 null。
    /// </summary>
    public static string? NormalizeTimeText(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return null;

        // 已经带冒号的完整时间直接规整
        if (text.Contains(':') &&
            TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= TimeSpan.Zero && parsed < TimeSpan.FromDays(1))
        {
            return $"{(int)parsed.TotalHours:00}:{parsed.Minutes:00}";
        }

        // 全角数字先转半角，再只保留数字，避免中文输入法下解析失败
        var digits = new string(text
            .Select(c => c is >= '\uFF10' and <= '\uFF19' ? (char)(c - '\uFF10' + '0') : c)
            .Where(char.IsAsciiDigit)
            .ToArray());
        if (digits.Length is 0 or > 4) return null;

        int hour, minute;
        if (digits.Length <= 2)
        {
            var whole = int.Parse(digits, CultureInfo.InvariantCulture);
            if (whole <= 23 || digits.Length == 1)
            {
                hour = whole;
                minute = 0;
            }
            else
            {
                // 「93」按 9:30 理解
                hour = digits[0] - '0';
                minute = (digits[1] - '0') * 10;
            }
        }
        else if (digits.Length == 3)
        {
            hour = digits[0] - '0';
            minute = int.Parse(digits.AsSpan(1), CultureInfo.InvariantCulture);
        }
        else
        {
            hour = int.Parse(digits.AsSpan(0, 2), CultureInfo.InvariantCulture);
            minute = int.Parse(digits.AsSpan(2, 2), CultureInfo.InvariantCulture);
        }

        if (hour is < 0 or > 23 || minute is < 0 or > 59) return null;
        return $"{hour:00}:{minute:00}";
    }

    private static DateTime? ParseLocal(string dateText, string timeText)
    {
        if (string.IsNullOrWhiteSpace(dateText)) return null;

        if (!DateTime.TryParseExact(dateText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            return null;
        }

        if (!TimeSpan.TryParse((timeText ?? string.Empty).Trim(), CultureInfo.InvariantCulture, out var time))
        {
            time = TimeSpan.FromHours(9);
        }

        if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)) time = TimeSpan.FromHours(9);

        return date.Date + time;
    }

    private static DateTime MondayOf(DateTime date) =>
        date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static string WeekName(DateTime date) => date.DayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日"
    };
}
