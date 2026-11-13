using Microsoft.Win32;

namespace DidaLite.App.Services;

/// <summary>开机自启：写当前用户的 Run 键，登录后带 --tray 静默启动、只进托盘。</summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DidaLite";

    /// <summary>当前用户是否已登记开机自启。</summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
    }

    /// <summary>登记开机自启，指向本次运行的 exe。</summary>
    public static void Enable()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key?.SetValue(ValueName, $"\"{path}\" --tray");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
