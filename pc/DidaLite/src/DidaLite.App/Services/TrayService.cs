using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DidaLite.App.Services;

/// <summary>
/// 系统托盘：直接用 Shell_NotifyIcon，不引入 WinForms。
/// 托盘很少用到，但 WinForms 程序集要十几兆、冷启动还要额外加载，这里省掉。
/// </summary>
public sealed class TrayService : IDisposable
{
    private const int WmNull = 0x0000;
    private const int WmCommand = 0x0111;
    private const int WmApp = 0x8000;
    private const int TrayCallback = WmApp + 1;

    private const int WmRButtonUp = 0x0205;
    private const int WmLButtonDblClk = 0x0203;

    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmBottomAlign = 0x0020;

    private const int IdiApplication = 32512;
    private const int CommandShow = 1;
    private const int CommandExit = 2;

    private readonly Action _show;
    private readonly Action _exit;
    private readonly HwndSource _source;
    private readonly IntPtr _icon;
    private readonly IntPtr _menu;
    private readonly uint _taskbarCreated;

    private bool _iconAdded;

    public TrayService(Action show, Action exit)
    {
        _show = show;
        _exit = exit;

        // 消息窗口只在后台收托盘回调，不显示
        _source = new HwndSource(new HwndSourceParameters("DidaLiteTray")
        {
            WindowStyle = 0,
            Width = 0,
            Height = 0
        });

        _source.AddHook(WindowProc);

        _icon = LoadAppIcon();
        _menu = CreateTrayMenu();
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        AddIcon();
    }

    /// <summary>把图标登记到通知区。</summary>
    private void AddIcon()
    {
        var data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _source.Handle,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = TrayCallback,
            hIcon = _icon,
            szTip = "滴答 · DidaLite"
        };

        _iconAdded = Shell_NotifyIcon(NimAdd, ref data);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 资源管理器重启后图标会消失，收到广播就重新登记
        if (_taskbarCreated != 0 && (uint)msg == _taskbarCreated)
        {
            AddIcon();
            return IntPtr.Zero;
        }

        if (msg == TrayCallback)
        {
            switch ((int)lParam)
            {
                case WmLButtonDblClk:
                    _show();
                    handled = true;
                    break;
                case WmRButtonUp:
                    ShowMenu();
                    handled = true;
                    break;
            }

            return IntPtr.Zero;
        }

        if (msg == WmCommand)
        {
            switch ((int)(wParam.ToInt64() & 0xFFFF))
            {
                case CommandShow:
                    _show();
                    handled = true;
                    break;
                case CommandExit:
                    _exit();
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        GetCursorPos(out var point);

        // 菜单弹出前要先把窗口拿到前台，否则点别处菜单不消失
        SetForegroundWindow(_source.Handle);
        TrackPopupMenu(_menu, TpmRightButton | TpmBottomAlign, point.X, point.Y, 0, _source.Handle, IntPtr.Zero);
        PostMessage(_source.Handle, WmNull, IntPtr.Zero, IntPtr.Zero);
    }

    private static IntPtr LoadAppIcon()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path)
            && ExtractIconEx(path, 0, out _, out var small, 1) > 0
            && small != IntPtr.Zero)
        {
            return small;
        }

        return LoadIcon(IntPtr.Zero, IdiApplication);
    }

    private static IntPtr CreateTrayMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, MfString, CommandShow, "显示主界面");
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString, CommandExit, "退出");
        return menu;
    }

    public void Dispose()
    {
        if (_iconAdded)
        {
            var data = new NotifyIconData
            {
                cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
                hWnd = _source.Handle,
                uID = 1
            };

            Shell_NotifyIcon(NimDelete, ref data);
            _iconAdded = false;
        }

        _source.RemoveHook(WindowProc);
        _source.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, int icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
}
