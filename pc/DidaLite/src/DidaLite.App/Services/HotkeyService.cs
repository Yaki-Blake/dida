using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DidaLite.App.Services;

/// <summary>全局快捷键：窗口收进托盘、不在前台时也能把主界面叫出来。</summary>
public sealed class HotkeyService : IDisposable
{
    /// <summary>默认快捷键。</summary>
    public const string Default = "Alt+D";

    /// <summary>设置里关掉快捷键后，写进 meta 的标记。</summary>
    public const string OffMarker = "off";

    /// <summary>关掉之后界面上显示的文字。</summary>
    public const string Disabled = "已关闭";

    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x4444;

    /// <summary>按住超过这个时长就算「长按窥探」，松手收回；短于它按切换处理。</summary>
    private const int LongPressMs = 350;

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;

    private readonly Window _window;
    private readonly Action _pressed;
    private readonly Action<bool> _released;
    private readonly DispatcherTimer _holdTimer;
    private readonly Stopwatch _holdWatch = new();

    private HwndSource? _source;
    private ModifierKeys _modifiers;
    private Key _key;
    private bool _holding;

    public HotkeyService(Window window, Action pressed, Action<bool> released)
    {
        _window = window;
        _pressed = pressed;
        _released = released;

        // 热键只有按下消息，按住还会不断重复；这里轮询按键是否仍被按着，用来判断松手。
        // 用 Input 优先级 + 一帧的间隔：默认的 Background 优先级会被布局/渲染排队压后，
        // 松手到真正收起之间就会空出几十毫秒，手感上就是「按完了要迟一下才收」。
        _holdTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _holdTimer.Tick += (_, _) => CheckRelease();

        // 窗口句柄已经建好就直接挂消息钩子，否则等 SourceInitialized
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Hook();
        else window.SourceInitialized += (_, _) => Hook();
    }

    /// <summary>热键是否注册成功；被其他程序占用时会失败。</summary>
    public bool IsRegistered { get; private set; }

    /// <summary>换成新的组合：先撤掉旧的，注册失败再把旧的恢复回去。</summary>
    public bool Apply(ModifierKeys modifiers, Key key)
    {
        var (oldModifiers, oldKey) = (_modifiers, _key);

        Unregister();
        _modifiers = modifiers;
        _key = key;
        if (Register()) return true;

        // 新组合被别人占用：退回原来的，别让用户彻底失去热键
        _modifiers = oldModifiers;
        _key = oldKey;
        if (oldKey != Key.None) Register();
        return false;
    }

    /// <summary>
    /// 关掉快捷键：注销热键、清掉记住的组合，按住时的轮询也停下。
    /// 之后 Apply 一次就能重新启用。
    /// </summary>
    public void Disable()
    {
        _holdTimer.Stop();
        _holding = false;
        _holdWatch.Reset();

        _modifiers = ModifierKeys.None;
        _key = Key.None;
        Unregister();
    }

    /// <summary>meta 里存的是不是「已关闭」。</summary>
    public static bool IsOff(string? saved) =>
        string.Equals(saved?.Trim(), OffMarker, StringComparison.OrdinalIgnoreCase);

    private void Hook()
    {
        var handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(OnMessage);

        // 句柄建好之前调用过 Apply 的，这里补注册一次；关掉状态（Key.None）不补
        if (!IsRegistered && _key != Key.None) Register();
    }

    private bool Register()
    {
        var handle = new WindowInteropHelper(_window).Handle;
        IsRegistered = RegisterHotKey(handle, HotkeyId, ToWin32(_modifiers),
            (uint)KeyInterop.VirtualKeyFromKey(_key));
        return IsRegistered;
    }

    private void Unregister()
    {
        if (!IsRegistered) return;

        UnregisterHotKey(new WindowInteropHelper(_window).Handle, HotkeyId);
        IsRegistered = false;
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey || wParam.ToInt32() != HotkeyId) return IntPtr.Zero;

        // 按住时系统会反复发按下消息，只认第一次，避免窗口反复唤起又收起
        if (_holding) return IntPtr.Zero;

        _holding = true;
        handled = true;
        _holdWatch.Restart();
        _holdTimer.Start();
        _pressed();
        return IntPtr.Zero;
    }

    /// <summary>按键松开了：短按和长按交给窗口决定怎么收尾。</summary>
    private void CheckRelease()
    {
        if (IsComboDown()) return;

        _holdTimer.Stop();
        _holding = false;
        var held = _holdWatch.ElapsedMilliseconds;
        _holdWatch.Reset();
        _released(held >= LongPressMs);
    }

    /// <summary>快捷键里的主键和修饰键是否都还按着。</summary>
    private bool IsComboDown()
    {
        if (!IsDown(KeyInterop.VirtualKeyFromKey(_key))) return false;
        if (_modifiers.HasFlag(ModifierKeys.Control) && !IsDown(VkControl)) return false;
        if (_modifiers.HasFlag(ModifierKeys.Alt) && !IsDown(VkMenu)) return false;
        if (_modifiers.HasFlag(ModifierKeys.Shift) && !IsDown(VkShift)) return false;
        if (_modifiers.HasFlag(ModifierKeys.Windows) && !IsDown(VkLWin)) return false;
        return true;
    }

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static uint ToWin32(ModifierKeys modifiers)
    {
        uint value = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) value |= 0x0001;
        if (modifiers.HasFlag(ModifierKeys.Control)) value |= 0x0002;
        if (modifiers.HasFlag(ModifierKeys.Shift)) value |= 0x0004;
        if (modifiers.HasFlag(ModifierKeys.Windows)) value |= 0x0008;
        return value;
    }

    /// <summary>把组合键写成 "Alt+D" 这样的可读文本。</summary>
    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    /// <summary>解析 "Alt+D" 这类文本，解析不了返回 false。</summary>
    public static bool TryParse(string? text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;

        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win": modifiers |= ModifierKeys.Windows; break;
                default: return false;
            }
        }

        var name = parts[^1];
        if (name.Length == 1 && char.IsDigit(name[0]))
        {
            key = (Key)((int)Key.D0 + (name[0] - '0'));
            return true;
        }

        return Enum.TryParse(name, true, out key) && key != Key.None;
    }

    private static string KeyName(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9)
            return ((int)key - (int)Key.D0).ToString();

        return key.ToString();
    }

    public void Dispose()
    {
        _holdTimer.Stop();

        // 关掉过快捷键时 IsRegistered 已经是 false，但钩子还挂着，这里一并摘掉
        _source?.RemoveHook(OnMessage);
        _source = null;

        Unregister();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
