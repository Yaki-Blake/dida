using System.Windows;
using System.Windows.Input;
using DidaLite.App.Services;
using DidaLite.App.ViewModels;
using DidaLite.Data.Sync;

namespace DidaLite.App.Views;

/// <summary>
/// 设置窗口：地址、账号、同步、快捷键、外观都在这里。
/// 打开时是模态的；快捷键录制需要窗口自己拿到按键，所以捕获逻辑放在这个窗口。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly Func<ModifierKeys, Key, bool> _applyHotkey;
    private readonly Func<bool, bool> _setHotkeyEnabled;
    private readonly Action<string> _saveAddress;
    private readonly UserStore? _users = UserStore.Create();

    private bool _capturingHotkey;

    /// <summary>录制前的有效快捷键，Esc 或关窗时用它恢复。</summary>
    private string _effectiveHotkey;

    public SettingsWindow(MainViewModel vm, Func<ModifierKeys, Key, bool> applyHotkey,
        Func<bool, bool> setHotkeyEnabled, Action<string> saveAddress)
    {
        InitializeComponent();

        _vm = vm;
        _applyHotkey = applyHotkey;
        _setHotkeyEnabled = setHotkeyEnabled;
        _saveAddress = saveAddress;
        _effectiveHotkey = vm.HotkeyText;

        DataContext = vm;
        AddressBox.Text = vm.SyncAddress;

        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_users is null)
        {
            UserStatus.Text = ".env 缺少 DIDA_DB_CONNECTION，无法读取账号";
            return;
        }

        try
        {
            var name = await _users.GetNameAsync();
            UserNameBox.Text = name ?? string.Empty;

            if (name is null)
            {
                UserStatus.Text = "还没有账号，设置后手机才能登录";
                UserNameBox.Focus();
            }
            else
            {
                UserStatus.Text = string.Empty;
            }
        }
        catch (Exception ex)
        {
            UserStatus.Text = $"读取失败：{ex.Message}（MySQL 启动了吗？）";
        }
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AddressBox.Text)) return;

        Clipboard.SetText(AddressBox.Text);
        AddressStatus.Text = "已复制";
    }

    private void SaveAddress_Click(object sender, RoutedEventArgs e)
    {
        var baseUrl = AddressBox.Text.Trim().TrimEnd('/');
        if (baseUrl.EndsWith("/sync", StringComparison.OrdinalIgnoreCase)) baseUrl = baseUrl[..^5];

        if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            AddressStatus.Text = "要以 http:// 开头，如 http://192.168.1.5:5080";
            return;
        }

        _saveAddress(baseUrl);
        AddressBox.Text = baseUrl + "/sync";
        AddressStatus.Text = "已保存";
    }

    private async void SaveUser_Click(object sender, RoutedEventArgs e)
    {
        if (_users is null)
        {
            UserStatus.Text = ".env 缺少 DIDA_DB_CONNECTION，账号存不进数据库";
            return;
        }

        var name = UserNameBox.Text.Trim();
        if (name.Length == 0)
        {
            UserStatus.Text = "请填写用户名";
            return;
        }

        var password = PasswordBox.Password;
        if (password.Length < 6)
        {
            UserStatus.Text = "密码至少 6 位";
            return;
        }

        UserStatus.Text = "正在保存…";
        try
        {
            var cleared = await _users.SaveAsync(name, password);
            PasswordBox.Clear();
            UserStatus.Text = cleared
                ? "已保存；手机端用这个用户名和密码登录（改过密码，旧登录已失效）"
                : "已保存；密码没变，手机端不用重新登录";
        }
        catch (Exception ex)
        {
            UserStatus.Text = $"保存失败：{ex.Message}（MySQL 启动了吗？）";
        }
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingHotkey) return;

        _capturingHotkey = true;
        _effectiveHotkey = _vm.HotkeyText;
        _vm.HotkeyText = "请按组合键…";
    }

    /// <summary>
    /// 「关闭 / 开启」：关掉后全局热键彻底不响应；再点一下按原来的组合恢复。
    /// 正在录制时先退出录制，免得两三处状态打架。
    /// </summary>
    private async void HotkeyToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_capturingHotkey)
        {
            _capturingHotkey = false;
            _vm.HotkeyText = _effectiveHotkey;
        }

        // 关着就开、开着就关
        if (_setHotkeyEnabled(_vm.IsHotkeyOff))
        {
            _effectiveHotkey = _vm.HotkeyText;
            return;
        }

        // 开启失败通常是组合被别的程序占用了：按「已关闭」收尾，别留下半开状态
        _vm.HotkeyText = "该组合已被占用";
        await Task.Delay(1500);
        _vm.HotkeyText = HotkeyService.Disabled;
        _effectiveHotkey = _vm.HotkeyText;
    }

    /// <summary>录制快捷键：Esc 取消，只按修饰键时继续等，组合可用就立即生效。</summary>
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            _capturingHotkey = false;
            _vm.HotkeyText = _effectiveHotkey;
            return;
        }

        // Alt 组合在 WPF 里报成 System，真实按键放在 SystemKey 里
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        // 不带修饰键会把单个字母变成全局热键，等于废掉系统里这个键
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None) return;

        if (!_applyHotkey(modifiers, key))
        {
            _capturingHotkey = false;
            _vm.HotkeyText = "该组合已被占用";
            await Task.Delay(1500);
            if (!_capturingHotkey) _vm.HotkeyText = _effectiveHotkey;
            return;
        }

        _capturingHotkey = false;
        _effectiveHotkey = _vm.HotkeyText;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_capturingHotkey) _vm.HotkeyText = _effectiveHotkey;

        base.OnClosed(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
