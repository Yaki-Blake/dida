using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DidaLite.App.Services;

/// <summary>
/// 把窗口叫到最前。WPF 的 Activate() 只在本进程仍握有前台权限时才真正生效，
/// 从托盘、全局快捷键、第二次启动叫回来的窗口往往只在任务栏闪一下，
/// 用户看到的就是「点了没反应、窗口还是压在后面」。这里按 Windows 的前台锁规则逐级加码。
/// 类名不能叫 Foreground：会和 Window.Foreground（画刷属性）撞名。
/// </summary>
public static class WindowFront
{
    private const int SwRestore = 9;
    private const int AsfwAny = -1;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;

    private static readonly IntPtr Topmost = new(-1);
    private static readonly IntPtr NotTopmost = new(-2);

    /// <summary>显示并激活窗口，必要时绕过前台锁。</summary>
    public static void BringToFront(Window window)
    {
        // 隐藏时句柄一般已经存在（Show 过或有 EnsureHandle），先拿句柄，
        // 尽量在 Show 之前就把它从最小化里弄出来，否则屏幕上会先冒出一个最小化的窗口再被还原
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            window.Show();
            hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
        }

        // 关键：先把 Win32 的「最小化」解开，再谈前台。
        // 手动点最小化时 MainWindow.OnStateChanged 会顺手 Hide()，WPF 记的 WindowState
        // 是 Minimized，单靠 Show() 只是把最小化的窗口重新显示出来——窗口依旧缩着；
        // 更麻烦的是最小化的窗口照样能被 SetForegroundWindow 设成前台，
        // 于是「是否已在前台」的判定会通过，还原那一步永远轮不到，用户看到的就是快捷键没反应。
        // SW_RESTORE 交给系统自己还原，它记得最小化之前是普通还是最大化。
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        }

        if (!window.IsVisible) window.Show();

        // 先走一遍 WPF 的正常路径：冷启动、进程本来就在前台时一次就够，不会有任何额外动作
        window.Activate();
        if (IsForeground(hwnd) && !IsIconic(hwnd)) return;

        // 1) 短暂置顶再取消：无论前台锁怎么判，都先把窗口抬到其他窗口之上
        SetWindowPos(hwnd, Topmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        SetWindowPos(hwnd, NotTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);

        // 2) 借当前前台线程的输入队列，绕开「只有前台进程才能设置前台窗口」的限制
        var current = GetCurrentThreadId();
        var foreground = GetForegroundWindow();
        var other = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        if (other != 0 && other != current && AttachThreadInput(current, other, true))
        {
            SetForegroundWindow(hwnd);
            AttachThreadInput(current, other, false);
        }
        else
        {
            SetForegroundWindow(hwnd);
        }

        // 3) 还没拿到焦点：让系统自己走一遍还原流程，最后用 WPF 收尾同步 IsActive
        // 只对真被最小化的窗口还原，已经最大化的窗口不能被这一步打回普通大小
        if (!IsForeground(hwnd) || IsIconic(hwnd))
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }

        window.Activate();
    }

    /// <summary>
    /// 第二次启动时调用：把自己刚拿到的前台权限让给整个会话。
    /// 否则已运行的那份进程没有资格把窗口设成前台，只能眼睁睁在任务栏闪。
    /// </summary>
    public static void YieldForegroundRight() => AllowSetForegroundWindow(AsfwAny);

    /// <summary>窗口当前是不是系统认定的前台窗口。</summary>
    public static bool IsForeground(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && GetForegroundWindow() == hwnd;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
