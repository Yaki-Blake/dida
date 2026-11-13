using System.Windows.Threading;
using DidaLite.App.Views;
using DidaLite.Core.Models;
using DidaLite.Core.Reminders;
using DidaLite.Data;

namespace DidaLite.App.Services;

/// <summary>
/// 本地提醒调度：每 20 秒扫描一次，到点弹窗 + 响铃。
/// 完全依赖本地库，断网也能正常工作。
/// </summary>
public sealed class ReminderService
{
    private readonly LocalStore _store;
    private readonly Action _onDataChanged;
    private readonly DispatcherTimer _timer;
    private readonly Queue<string> _queue = new();
    private ReminderWindow? _window;
    private bool _scanning;

    public ReminderService(LocalStore store, Action onDataChanged)
    {
        _store = store;
        _onDataChanged = onDataChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += (_, _) => Scan();
    }

    public void Start()
    {
        Scan();          // 启动即补发漏掉的提醒
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Scan()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (var task in _store.GetAll())
            {
                if (!ReminderPlanner.ShouldFire(task, now)) continue;
                if (_queue.Contains(task.Id)) continue;
                if (_window?.Task.Id == task.Id) continue;
                _queue.Enqueue(task.Id);
            }

            ShowNext();
        }
        finally
        {
            _scanning = false;
        }
    }

    private void ShowNext()
    {
        if (_window is not null || _queue.Count == 0) return;

        var id = _queue.Dequeue();
        var task = _store.GetAll().FirstOrDefault(t => t.Id == id);
        if (task is null || task.Deleted || task.Done)
        {
            ShowNext();
            return;
        }

        _window = new ReminderWindow(task);
        _window.ClosedAction += OnReminderClosed;
        _window.Closed += (_, _) =>
        {
            _window = null;
            ShowNext();
        };
        _window.Show();
    }

    private void OnReminderClosed(object? sender, ReminderAction action)
    {
        if (sender is not ReminderWindow window) return;

        var task = window.Task;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        switch (action)
        {
            case ReminderAction.Complete:
                if (RepeatRules.IsRepeating(task.RepeatRule))
                {
                    // 重复任务：完成本次即推进到下一次，保持活跃
                    if (!ReminderPlanner.AdvanceToNextOccurrence(task, now))
                    {
                        task.Done = true;
                        task.RemindedAt = now;
                    }
                }
                else
                {
                    task.Done = true;
                    task.RemindedAt = now;
                }
                break;

            case ReminderAction.SnoozeTenMinutes:
                task.RemindAt = now + 10 * 60 * 1000;
                task.RemindedAt = null;
                break;

            case ReminderAction.SnoozeOneDay:
                task.RemindAt = now + 24 * 60 * 60 * 1000;
                task.RemindedAt = null;
                break;

            default:
                task.RemindedAt = now;
                break;
        }

        task.Touch();
        _store.Upsert(task);
        _onDataChanged();
    }
}
