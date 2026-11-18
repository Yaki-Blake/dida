using System.Windows;
using System.Windows.Input;
using DidaLite.App.Services;
using DidaLite.App.ViewModels;
using DidaLite.Core;
using DidaLite.Data.Sync;

namespace DidaLite.App.Views;

/// <summary>
/// 设置窗口：服务器登录、同步、快捷键、外观都在这里。
/// 打开时是模态的；快捷键录制需要窗口自己拿到按键，所以捕获逻辑放在这个窗口。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly Func<ModifierKeys, Key, bool> _applyHotkey;
    private readonly Func<bool, bool> _setHotkeyEnabled;

    private bool _capturingHotkey;

    /// <summary>录制前的有效快捷键，Esc 或关窗时用它恢复。</summary>
    private string _effectiveHotkey;

    public SettingsWindow(MainViewModel vm, Func<ModifierKeys, Key, bool> applyHotkey,
        Func<bool, bool> setHotkeyEnabled)
    {
        InitializeComponent();

        _vm = vm;
        _applyHotkey = applyHotkey;
        _setHotkeyEnabled = setHotkeyEnabled;
        _effectiveHotkey = vm.HotkeyText;

        DataContext = vm;

        // 地址和用户名回显；密码不回显（.env 里有明文，但没必要摊在界面上）
        RemoteUrlBox.Text = MainViewModel.RemoteUrl;
        RemoteUserBox.Text = MainViewModel.RemoteUserName;

        RefreshLoginUi();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>
    /// 是否已登录：远程模式下账号和密码都在就算已登录。
    /// 密码不回显，所以用 .env 里是否有密码来判断，而不是看密码框。
    /// </summary>
    private bool IsLoggedIn =>
        _vm.IsRemoteSync
        && MainViewModel.RemoteUserName.Length > 0
        && !string.IsNullOrWhiteSpace(EnvFile.Get(RemoteConfig.PasswordKey));

    /// <summary>
    /// 按登录态切换界面：已登录时只看得到服务器地址和账号，密码框收起、按钮变「登出」。
    /// </summary>
    private void RefreshLoginUi()
    {
        var loggedIn = IsLoggedIn;

        RemoteUrlBox.IsReadOnly = loggedIn;
        RemoteUserBox.IsReadOnly = loggedIn;
        PasswordLabel.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        RemotePasswordBox.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        LoginButton.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        LogoutButton.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;

        if (loggedIn)
        {
            RemoteUrlBox.Text = MainViewModel.RemoteUrl;
            RemoteUserBox.Text = MainViewModel.RemoteUserName;
            RemotePasswordBox.Password = string.Empty;
            ModeStatus.Text = $"已登录为 {MainViewModel.RemoteUserName}，可直接同步；换账号请先登出。";
        }
        else
        {
            RemotePasswordBox.Password = string.Empty;
            ModeStatus.Text = ModeStatus.Text.Contains("登出") ? "已登出，请重新登录。" : ModeStatus.Text;
        }
    }

    /// <summary>
    /// 「登录」：拿界面上现填的地址和账号真发一次 /login，成功才写 .env。
    /// 把这步做实的好处是——地址填成网站首页、服务器没起、密码不对，
    /// 都能当场看到具体原因，而不是等后台同步默默失败。
    /// 配置只在启动时读，所以登录成功后问一句要不要重启。
    /// </summary>
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        var raw = RemoteUrlBox.Text.Trim();
        if (raw.Length == 0)
        {
            ModeStatus.Text = "请填服务器地址";
            RemoteUrlBox.Focus();
            return;
        }

        var url = RemoteConfig.Normalize(raw);
        // Normalize 会把「只写了 IP:端口」补成 http://，但补不出主机名，这种要挡住
        if ((!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            || url.EndsWith("://", StringComparison.Ordinal))
        {
            ModeStatus.Text = "地址要以 http:// 或 https:// 开头，并带上主机名";
            RemoteUrlBox.Focus();
            return;
        }

        var name = RemoteUserBox.Text.Trim();
        if (name.Length == 0)
        {
            ModeStatus.Text = "请填用户名";
            RemoteUserBox.Focus();
            return;
        }

        var password = RemotePasswordBox.Password;
        if (password.Length == 0)
        {
            ModeStatus.Text = "请填密码";
            RemotePasswordBox.Focus();
            return;
        }

        var cfg = new RemoteConfig(url, name, password);

        LoginButton.IsEnabled = false;
        ModeStatus.Text = $"正在连接 {url} …";
        try
        {
            var result = await SyncClient.TestLoginAsync(cfg);

            if (!result.Ok)
            {
                ModeStatus.Text = "登录失败：" + result.Message;
                return;
            }

            EnvFile.Set(RemoteConfig.UrlKey, cfg.BaseUrl);
            EnvFile.Set(RemoteConfig.UserKey, cfg.Name);
            EnvFile.Set(RemoteConfig.PasswordKey, cfg.Password);

            // 让运行中的同步客户端立即用上新账号，无需重启
            _vm.RefreshSyncConfig();

            RemoteUrlBox.Text = cfg.BaseUrl;
            ModeStatus.Text = $"登录成功，已连 {cfg.BaseUrl}";
            RefreshLoginUi();
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 「登出」：清掉账号和密码，保留服务器地址。
    /// 同时清掉缓存 token 并让同步客户端退回未登录态，立即生效、无需重启。
    /// </summary>
    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        var url = MainViewModel.RemoteUrl;

        EnvFile.Set(RemoteConfig.UserKey, string.Empty);
        EnvFile.Set(RemoteConfig.PasswordKey, string.Empty);

        _vm.ClearSyncToken();
        _vm.RefreshSyncConfig();

        RemoteUrlBox.Text = url;   // 服务器地址不清空
        RemoteUserBox.Text = string.Empty;
        RefreshLoginUi();
        ModeStatus.Text = "已登出，服务器地址已保留；填账号密码即可重新登录。";
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
