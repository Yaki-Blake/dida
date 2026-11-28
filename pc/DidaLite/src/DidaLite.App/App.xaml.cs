using System.IO;
using System.Threading;
using System.Windows;
using DidaLite.App.Behaviors;
using DidaLite.App.Services;
using DidaLite.App.Views;

namespace DidaLite.App;

public partial class App : Application
{
    /// <summary>
    /// 未处理异常落盘。界面闪退时用户说不清发生了什么，日志是唯一线索；
    /// 写在数据目录而不是程序目录，装到只读位置也能记。
    /// </summary>
    public App()
    {
        DispatcherUnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash(e.Exception);
            e.SetObserved();
        };
    }

    internal static string CrashLogPath => Path.Combine(EnvDir, "crash.log");

    private static string EnvDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DidaLite");

    private static void LogCrash(Exception? ex)
    {
        if (ex is null) return;

        try
        {
            Directory.CreateDirectory(EnvDir);
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // 记录失败不能再抛：本来就在异常路径上
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局滚轮自动滚动：装在 ScrollViewer 类型上，窗口建出来之前先挂好，
        // 之后所有页面（含设置、详情这些独立窗口）自动生效
        MiddleDragScroll.Install();

        // 托盘里已经有一份在跑：叫它把窗口显示出来，这一份立刻退出，
        // 不必再走一遍建窗、读库、布局。
        if (!SingleInstance.TryTake())
        {
            // 刚被双击启动的这份手里有前台权限，先让给整个会话，
            // 已在运行的那份才有资格把窗口真正提到最前，而不是只在任务栏闪
            WindowFront.YieldForegroundRight();
            SingleInstance.WakeExisting();

            // 这一份马上要退出，稍等一瞬，让已有实例在权限还有效时完成置前
            Thread.Sleep(60);
            Shutdown(0);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;

        // 开机自启带 --tray：只把提醒、托盘、同步跑起来，不弹窗口
        if (Array.Exists(e.Args, arg => string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase)))
        {
            window.StartHidden();
        }
        else
        {
            window.Show();

            // 正常双击启动时系统会把窗口放到前台，但被启动器/安全软件接手时会掉到后面，
            // 这里补一次前置，用户双击就一定能看到窗口
            WindowFront.BringToFront(window);

            // 冷启动要等首帧渲染，上面那次置前可能落在窗口还不可见的时刻；
            // 内容真正画出来后再补一次，确保窗口是「看得见且在最前」
            window.ContentRendered += (_, _) =>
            {
                if (window.IsVisible) WindowFront.BringToFront(window);
            };
        }

        SingleInstance.Listen(BringToFront);
    }

    /// <summary>又一次双击图标：把已经在跑的主窗口重新显示出来。</summary>
    private void BringToFront()
    {
        Dispatcher.Invoke(() =>
        {
            if (MainWindow is Views.MainWindow window) window.RestoreToFront();
        });
    }
}
