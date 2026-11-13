using System.Windows.Threading;
using DidaLite.App.Views;
using DidaLite.Core.Models;
using DidaLite.Data;

namespace DidaLite.App.Services;

/// <summary>
/// 闹钟调度：每 10 秒扫一次本地闹钟表，到点弹窗 + 响铃。
/// 只依赖本地库与本地时间，断网、主窗口收进托盘都能正常响。
/// </summary>
public sealed class AlarmService
{
    private readonly LocalStore _store;
    private readonly DispatcherTimer _timer;
    private readonly Queue<AlarmItem> _queue = new();
    private AlarmRingWindow? _window;
    private bool _scanning;

    public AlarmService(LocalStore store)
    {
        _store = store;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => Scan();
    }

    /// <summary>闹钟数据被调度改动（例如单次闹钟响后自动停用）时通知界面刷新。</summary>
    public event EventHandler? AlarmsChanged;

    public void Start()
    {
        Scan();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    /// <summary>数据已被外部改过（手机端同步过来），让绑定闹钟列表的界面重新读一遍。</summary>
    public void NotifyChanged() => AlarmsChanged?.Invoke(this, EventArgs.Empty);

    private void Scan()
    {
        if (_scanning) return;
        _scanning = true;

        try
        {
            var now = DateTimeOffset.Now;
            var nowMs = now.ToUnixTimeMilliseconds();

            foreach (var alarm in _store.GetAlarms())
            {
                if (alarm.Deleted || !alarm.Enabled) continue;
                if (!ShouldFire(alarm, now, nowMs)) continue;

                alarm.LastFiredAt = nowMs;
                if (alarm.RepeatRule == AlarmItem.Once) alarm.Enabled = false;
                alarm.Touch();
                _store.UpsertAlarm(alarm);

                _queue.Enqueue(alarm);
            }

            if (_queue.Count > 0) AlarmsChanged?.Invoke(this, EventArgs.Empty);
            ShowNext();
        }
        finally
        {
            _scanning = false;
        }
    }

    private static bool ShouldFire(AlarmItem alarm, DateTimeOffset now, long nowMs)
    {
        if (alarm.RepeatRule == AlarmItem.Once)
        {
            if (alarm.OnceAt is not long at) return false;
            if (nowMs < at) return false;

            // 停机太久不补发，避免开机瞬间连响
            if (nowMs - at > 60 * 60 * 1000) return false;
            return alarm.LastFiredAt is not long fired || fired < at;
        }

        if (alarm.RepeatRule == AlarmItem.Weekday &&
            now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        var today = now.Date.AddHours(alarm.Hour).AddMinutes(alarm.Minute);
        if (now.DateTime < today) return false;

        if (alarm.LastFiredAt is not long last) return true;
        return DateTimeOffset.FromUnixTimeMilliseconds(last).LocalDateTime.Date < now.Date;
    }

    private void ShowNext()
    {
        if (_window is not null || _queue.Count == 0) return;

        var alarm = _queue.Dequeue();
        _window = new AlarmRingWindow(alarm);
        _window.Closed += (_, _) =>
        {
            var snoozed = _window?.Snoozed == true;
            _window = null;

            if (snoozed) Snooze(alarm);
            ShowNext();
        };

        SoundService.Play();
        _window.Show();
    }

    /// <summary>稍后 9 分钟：插入一个一次性闹钟，复用同一套调度。</summary>
    private void Snooze(AlarmItem source)
    {
        var at = DateTimeOffset.Now.AddMinutes(9);
        var alarm = new AlarmItem
        {
            Label = string.IsNullOrWhiteSpace(source.Label) ? "稍后提醒" : source.Label,
            Hour = at.Hour,
            Minute = at.Minute,
            RepeatRule = AlarmItem.Once,
            OnceAt = at.ToUnixTimeMilliseconds(),
            Enabled = true
        };
        alarm.Touch();
        _store.UpsertAlarm(alarm);
        AlarmsChanged?.Invoke(this, EventArgs.Empty);
    }
}
