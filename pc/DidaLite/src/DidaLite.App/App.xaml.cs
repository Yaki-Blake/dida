using System.Windows;
using DidaLite.App.Services;
using DidaLite.App.Views;

namespace DidaLite.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 托盘里已经有一份在跑：叫它把窗口显示出来，这一份立刻退出，
        // 不必再走一遍建窗、读库、布局。
        if (!SingleInstance.TryTake())
        {
            SingleInstance.WakeExisting();
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
