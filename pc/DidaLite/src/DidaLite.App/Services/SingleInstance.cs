using System.Threading;

namespace DidaLite.App.Services;

/// <summary>
/// 单实例：程序常驻托盘，重复启动时只把已有窗口叫出来，不再开一份新的。
/// 这样「再点一次图标」是毫秒级的，而不是重新走一遍冷启动。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\DidaLite.App.SingleInstance";
    private const string EventName = @"Local\DidaLite.App.ShowWindow";

    private static Mutex? _mutex;
    private static EventWaitHandle? _wakeEvent;

    /// <summary>抢到单实例锁返回 true；已有实例在跑返回 false。</summary>
    public static bool TryTake()
    {
        _mutex = new Mutex(false, MutexName);
        try
        {
            return _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // 上一个进程没来得及释放就退出了，直接接管
            return true;
        }
    }

    /// <summary>
    /// 改完同步方式要重启自己：先把单实例锁放掉，否则新进程一启动就被判成「已有实例」然后退出。
    /// 必须由抢锁的那个线程（UI 线程）调用。
    /// </summary>
    public static void Release()
    {
        if (_mutex is null) return;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 不在持有锁的线程上，或本来就没持有：重启流程里不值得为此中断
        }
        finally
        {
            _mutex.Dispose();
            _mutex = null;
        }
    }

    /// <summary>叫醒已有实例，让它把窗口显示出来。</summary>
    public static void WakeExisting()
    {
        if (EventWaitHandle.TryOpenExisting(EventName, out var handle))
        {
            using (handle) handle.Set();
        }
    }

    /// <summary>首个实例在后台等唤醒信号，收到后在 UI 线程执行回调。</summary>
    public static void Listen(Action onWake)
    {
        _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);

        var thread = new Thread(() =>
        {
            while (true)
            {
                _wakeEvent.WaitOne();
                onWake();
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstance"
        };
        thread.Start();
    }
}
