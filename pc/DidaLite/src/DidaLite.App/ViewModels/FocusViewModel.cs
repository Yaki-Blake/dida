using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DidaLite.App.Services;
using DidaLite.Core.Models;
using DidaLite.Data;

namespace DidaLite.App.ViewModels;

/// <summary>专注页：倒计时、秒表、闹钟共用一条 100ms 计时器。</summary>
public sealed partial class FocusViewModel : ObservableObject
{
    private readonly LocalStore _store;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _stopwatch = new();

    /// <summary>运行中的倒计时结束时刻（本地时间戳，毫秒）。</summary>
    private long _countdownEndAt;

    /// <summary>暂停时保留的剩余毫秒。</summary>
    private int _countdownRemainingMs;

    public FocusViewModel(LocalStore store, AlarmService? alarms)
    {
        _store = store;

        var ring = store.GetMeta("ring.path");
        RingName = string.IsNullOrWhiteSpace(ring) ? "系统提示音" : Path.GetFileName(ring);

        var next = DateTime.Now.AddMinutes(1);
        NewAlarmHour = next.Hour;
        NewAlarmMinute = next.Minute;
        CountdownText = FormatCountdown(CountdownHour * 3600_000 + CountdownMinute * 60_000 + CountdownSecond * 1000);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        if (alarms is not null) alarms.AlarmsChanged += (_, _) => LoadAlarms();
        LoadAlarms();
    }

    /// <summary>倒计时归零时触发，由窗口负责弹窗与响铃。</summary>
    public event EventHandler? CountdownFinished;

    public ObservableCollection<AlarmRowViewModel> Alarms { get; } = [];

    public ObservableCollection<string> Laps { get; } = [];

    // ================= 标签切换 =================

    [ObservableProperty] private string _tab = "countdown";

    public bool IsCountdownTab => Tab == "countdown";
    public bool IsStopwatchTab => Tab == "stopwatch";
    public bool IsAlarmTab => Tab == "alarm";

    partial void OnTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsCountdownTab));
        OnPropertyChanged(nameof(IsStopwatchTab));
        OnPropertyChanged(nameof(IsAlarmTab));
    }

    [RelayCommand]
    private void ShowTab(string? tab)
    {
        if (!string.IsNullOrEmpty(tab)) Tab = tab;
    }

    // ================= 倒计时 =================

    [ObservableProperty] private int _countdownHour;
    [ObservableProperty] private int _countdownMinute = 25;
    [ObservableProperty] private int _countdownSecond;
    [ObservableProperty] private string _countdownText = "25:00";
    [ObservableProperty] private bool _isCountdownRunning;
    [ObservableProperty] private bool _isCountdownPaused;

    public bool IsCountdownEditing => !IsCountdownRunning && !IsCountdownPaused;

    public string CountdownButtonText => IsCountdownRunning ? "暂停" : IsCountdownPaused ? "继续" : "开始";

    partial void OnIsCountdownRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCountdownEditing));
        OnPropertyChanged(nameof(CountdownButtonText));
    }

    partial void OnIsCountdownPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCountdownEditing));
        OnPropertyChanged(nameof(CountdownButtonText));
    }

    partial void OnCountdownHourChanged(int value) => RefreshEditingCountdown();
    partial void OnCountdownMinuteChanged(int value) => RefreshEditingCountdown();
    partial void OnCountdownSecondChanged(int value) => RefreshEditingCountdown();

    [RelayCommand]
    private void ToggleCountdown()
    {
        if (IsCountdownRunning)
        {
            _countdownRemainingMs = (int)Math.Max(0, _countdownEndAt - DateTimeOffset.Now.ToUnixTimeMilliseconds());
            IsCountdownRunning = false;
            IsCountdownPaused = true;
            return;
        }

        if (IsCountdownPaused)
        {
            _countdownEndAt = DateTimeOffset.Now.ToUnixTimeMilliseconds() + _countdownRemainingMs;
            IsCountdownPaused = false;
            IsCountdownRunning = true;
            return;
        }

        var total = CountdownHour * 3600_000 + CountdownMinute * 60_000 + CountdownSecond * 1000;
        if (total <= 0) return;

        _countdownRemainingMs = total;
        _countdownEndAt = DateTimeOffset.Now.ToUnixTimeMilliseconds() + total;
        IsCountdownRunning = true;
        CountdownText = FormatCountdown(total);
    }

    [RelayCommand]
    private void ResetCountdown()
    {
        IsCountdownRunning = false;
        IsCountdownPaused = false;
        _countdownRemainingMs = 0;
        RefreshEditingCountdown();
    }

    [RelayCommand]
    private void SetCountdownPreset(string? minutes)
    {
        if (!int.TryParse(minutes, out var m)) return;

        IsCountdownRunning = false;
        IsCountdownPaused = false;
        CountdownHour = m / 60;
        CountdownMinute = m % 60;
        CountdownSecond = 0;
    }

    private void RefreshEditingCountdown()
    {
        if (!IsCountdownEditing) return;
        CountdownText = FormatCountdown(CountdownHour * 3600_000 + CountdownMinute * 60_000 + CountdownSecond * 1000);
    }

    // ================= 秒表 =================

    [ObservableProperty] private string _stopwatchText = "00:00.0";
    [ObservableProperty] private bool _isStopwatchRunning;

    public string StopwatchButtonText => IsStopwatchRunning ? "暂停" : "开始";

    partial void OnIsStopwatchRunningChanged(bool value) => OnPropertyChanged(nameof(StopwatchButtonText));

    [RelayCommand]
    private void ToggleStopwatch()
    {
        if (_stopwatch.IsRunning)
        {
            _stopwatch.Stop();
            IsStopwatchRunning = false;
            return;
        }

        _stopwatch.Start();
        IsStopwatchRunning = true;
    }

    [RelayCommand]
    private void Lap()
    {
        if (_stopwatch.ElapsedMilliseconds == 0) return;

        var text = FormatStopwatch(_stopwatch.Elapsed);
        StopwatchText = text;
        Laps.Insert(0, $"第 {Laps.Count + 1} 次    {text}");
    }

    [RelayCommand]
    private void ResetStopwatch()
    {
        _stopwatch.Reset();
        IsStopwatchRunning = false;
        StopwatchText = "00:00.0";
        Laps.Clear();
    }

    // ================= 闹钟 =================

    [ObservableProperty] private int _newAlarmHour;
    [ObservableProperty] private int _newAlarmMinute;
    [ObservableProperty] private string _newAlarmLabel = string.Empty;
    [ObservableProperty] private string _newAlarmRepeat = AlarmItem.Daily;

    public bool IsOnceRepeat => NewAlarmRepeat == AlarmItem.Once;
    public bool IsDailyRepeat => NewAlarmRepeat == AlarmItem.Daily;
    public bool IsWeekdayRepeat => NewAlarmRepeat == AlarmItem.Weekday;

    partial void OnNewAlarmRepeatChanged(string value)
    {
        OnPropertyChanged(nameof(IsOnceRepeat));
        OnPropertyChanged(nameof(IsDailyRepeat));
        OnPropertyChanged(nameof(IsWeekdayRepeat));
    }

    [RelayCommand]
    private void SetRepeat(string? rule)
    {
        if (!string.IsNullOrEmpty(rule)) NewAlarmRepeat = rule;
    }

    [RelayCommand]
    private void AddAlarm()
    {
        var hour = Math.Clamp(NewAlarmHour, 0, 23);
        var minute = Math.Clamp(NewAlarmMinute, 0, 59);

        var item = new AlarmItem
        {
            Label = NewAlarmLabel.Trim(),
            Hour = hour,
            Minute = minute,
            RepeatRule = NewAlarmRepeat,
            Enabled = true
        };

        if (item.RepeatRule == AlarmItem.Once)
        {
            var at = DateTime.Today.AddHours(hour).AddMinutes(minute);
            if (at <= DateTime.Now) at = at.AddDays(1);
            item.OnceAt = new DateTimeOffset(at).ToUnixTimeMilliseconds();
        }

        item.Touch();
        _store.UpsertAlarm(item);

        NewAlarmLabel = string.Empty;
        LoadAlarms();
    }

    [RelayCommand]
    private void DeleteAlarm(AlarmRowViewModel? row)
    {
        if (row is null) return;

        row.Item.Deleted = true;
        row.Item.Touch();
        _store.UpsertAlarm(row.Item);

        Alarms.Remove(row);
        OnPropertyChanged(nameof(HasAlarms));
    }

    private void LoadAlarms()
    {
        Alarms.Clear();
        foreach (var item in _store.GetAlarms().Where(a => !a.Deleted))
        {
            Alarms.Add(new AlarmRowViewModel(item, SaveAlarm));
        }

        OnPropertyChanged(nameof(HasAlarms));
    }

    private void SaveAlarm(AlarmRowViewModel row)
    {
        row.Item.Touch();
        _store.UpsertAlarm(row.Item);
    }

    public bool HasAlarms => Alarms.Count > 0;

    // ================= 铃声 =================

    [ObservableProperty] private string _ringName = "系统提示音";

    [RelayCommand]
    private void PickRing()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择铃声",
            Filter = "音频文件|*.wav;*.mp3;*.wma|所有文件|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        _store.SetMeta("ring.path", dialog.FileName);
        SoundService.CustomPath = dialog.FileName;
        RingName = Path.GetFileName(dialog.FileName);
    }

    [RelayCommand]
    private void ResetRing()
    {
        _store.SetMeta("ring.path", string.Empty);
        SoundService.CustomPath = null;
        RingName = "系统提示音";
    }

    [RelayCommand]
    private void TestRing() => SoundService.Play();

    // ================= 计时 =================

    private void Tick()
    {
        if (IsCountdownRunning)
        {
            var remaining = _countdownEndAt - DateTimeOffset.Now.ToUnixTimeMilliseconds();
            if (remaining <= 0)
            {
                IsCountdownRunning = false;
                IsCountdownPaused = false;
                CountdownText = "00:00";
                CountdownFinished?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                CountdownText = FormatCountdown((int)remaining);
            }
        }

        if (_stopwatch.IsRunning)
        {
            StopwatchText = FormatStopwatch(_stopwatch.Elapsed);
        }
    }

    private static string FormatCountdown(int ms)
    {
        var total = Math.Max(0, (int)Math.Ceiling(ms / 1000.0));
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }

    private static string FormatStopwatch(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}"
            : $"{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}";
}

/// <summary>闹钟列表里的一行，开关直接写回本地库。</summary>
public sealed partial class AlarmRowViewModel : ObservableObject
{
    private readonly Action<AlarmRowViewModel> _save;
    private readonly bool _loading;

    public AlarmRowViewModel(AlarmItem item, Action<AlarmRowViewModel> save)
    {
        Item = item;
        _save = save;

        _loading = true;
        Enabled = item.Enabled;
        _loading = false;
    }

    public AlarmItem Item { get; }

    public string TimeText => Item.TimeText;

    public string Title => string.IsNullOrWhiteSpace(Item.Label) ? "闹钟" : Item.Label;

    public string RepeatText => Item.RepeatRule switch
    {
        AlarmItem.Once => "单次",
        AlarmItem.Weekday => "工作日",
        _ => "每天"
    };

    [ObservableProperty] private bool _enabled;

    partial void OnEnabledChanged(bool value)
    {
        if (_loading) return;
        Item.Enabled = value;
        _save(this);
    }
}
