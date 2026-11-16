using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DidaLite.App.Services;
using DidaLite.App.ViewModels;
using DidaLite.Core;
using DidaLite.Data;

namespace DidaLite.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly LocalStore _store;
    private readonly AlarmService _alarms;
    private ReminderService? _reminders;
    private TrayService? _tray;
    private HotkeyService? _hotkey;
    private SyncServer? _sync;
    private SettingsWindow? _settings;

    /// <summary>当前真正生效的快捷键文本，取消录制时用它恢复显示。</summary>
    private string _hotkeyText = HotkeyService.Default;

    /// <summary>关掉之前用的组合，设置里点「开启」时原样恢复。</summary>
    private string _hotkeyBeforeOff = HotkeyService.Default;

    /// <summary>按快捷键时窗口是否本来就在最前面（决定松手要不要收起）。</summary>
    private bool _hotkeyWindowWasActive;

    private bool _exiting;

    /// <summary>日 / 周 / 月 / 专注切换动画进行中，期间忽略再次点击。</summary>
    private bool _segmentAnimating;

    /// <summary>删除动画进行中，期间忽略重复点击。</summary>
    private bool _deleting;

    /// <summary>等待 Enter 确认删除的任务。</summary>
    private TaskViewModel? _pendingDelete;

    /// <summary>提醒 / 托盘 / 同步只启动一次（开机自启时窗口不显示，Loaded 不会触发）。</summary>
    private bool _servicesStarted;

    private MonthView? _monthView;
    private YearView? _yearView;
    private FocusView? _focusView;
    private ScheduleView? _scheduleView;

    /// <summary>绿色小圆点的直径（逻辑像素）。</summary>
    private const double SegmentDotSize = 8;

    /// <summary>头部左侧标题与右侧按钮组之间至少留的空隙。</summary>
    private const double HeaderGap = 16;

    /// <summary>侧栏与主区之间那条拖动条的命中区宽度（视觉线仍是 2px）。</summary>
    private const double SidebarSplitterWidth = 10;

    /// <summary>已经排了一次宽度重算，避免同一轮布局里反复排队。</summary>
    private bool _columnLimitPending;

    /// <summary>
    /// 侧栏「想要」的宽度：默认值来自 Tokens，用户拖动时更新。
    /// 显示宽度只在窗口放不下时临时压缩，空间回来就恢复到这个值。
    /// </summary>
    private double _sidebarDesiredWidth;

    public MainWindow()
    {
        InitializeComponent();

        // 侧栏默认宽度来自 Tokens（SidebarColumnWidth）；记下这个「想要的值」，
        // 之后只在窗口放不下时临时压缩，不会再把它永久改小
        _sidebarDesiredWidth = SidebarColumn.Width.Value;

        // .env 先读进来：同步客户端和同步服务都要用
        EnvFile.Load();

        _store = new LocalStore();
        _store.Initialize();

        ThemeService.Apply(string.Equals(_store.GetMeta("theme"), "dark", StringComparison.OrdinalIgnoreCase));

        // 铃声文件放在本地 meta，启动就加载，闹钟/倒计时/任务提醒共用同一套响铃
        SoundService.CustomPath = _store.GetMeta("ring.path");

        _alarms = new AlarmService(_store);
        _vm = new MainViewModel(_store, _alarms, SyncClient.Create(_store));
        DataContext = _vm;

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.AccountRequired += OnAccountRequired;
        _vm.Focus.CountdownFinished += OnCountdownFinished;

        InputBindings.Add(new KeyBinding(_vm.SyncCommand, Key.R, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(_vm.NewTaskCommand, Key.N, ModifierKeys.Control));

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        PreviewKeyDown += OnPreviewKeyDown;

        // 窗口变窄或右侧详情打开后，已拖宽的侧栏要收回，不能把主区挤没
        SizeChanged += (_, _) => ClampSidebarWidth();
        DetailPanel.IsVisibleChanged += (_, _) => UpdateMainColumnMinWidth();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => StartServices();

    /// <summary>开机自启：不显示窗口，但提醒、闹钟、托盘、快捷键、同步照常跑。</summary>
    public void StartHidden()
    {
        // 不 Show 也要有窗口句柄，全局快捷键和托盘消息才有落点
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        StartServices();
    }

    /// <summary>提醒、闹钟、托盘、快捷键、同步统一在这里启动，只跑一次。</summary>
    private void StartServices()
    {
        if (_servicesStarted) return;
        _servicesStarted = true;

        // 先把列表和日历填上：首帧就能看到内容，不用等后台服务
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _vm.Load();
            UpdateMainColumnMinWidth();
        }));

        _reminders = new ReminderService(_store, _vm.Load);
        _reminders.Start();

        _alarms.Start();

        // 托盘图标、全局快捷键等首帧之后再装；内置同步服务直接丢后台线程，不占界面线程
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            _tray = new TrayService(RestoreToFront, ExitApp);
            _hotkey = new HotkeyService(this, OnHotkeyPressed, OnHotkeyReleased);
            ApplySavedHotkey();
        }));

        _ = Task.Run(StartSyncServer);
    }

    /// <summary>内嵌同步 API 启动并同步一次；起不来只是同步不可用，待办与提醒照常。</summary>
    private void StartSyncServer()
    {
        // Kestrel 冷启动要一两百毫秒，放到后台线程，界面先能点再说
        _sync = SyncServer.Start(_store);

        // 手机端停用 / 删除闹钟后会走这里：电脑端「闹钟」列表立刻刷新
        if (_sync is not null)
        {
            _sync.AlarmsChanged += (_, _) =>
                Dispatcher.BeginInvoke(new Action(() => _alarms.NotifyChanged()));
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_sync?.LanUrl is { } lanUrl) _vm.SyncAddress = lanUrl;

            // 启动同步一次，把另一端刚改的内容带回来（计划书 4.4）
            if (_sync is not null) _vm.SyncCommand.Execute(null);
        }));
    }

    /// <summary>头部左右两组内容自身宽度变化（文字变了、按钮显隐）时，重算侧栏右极限。</summary>
    private void Header_SizeChanged(object sender, SizeChangedEventArgs e) => ScheduleColumnLimitUpdate();

    private void ScheduleColumnLimitUpdate()
    {
        if (_columnLimitPending) return;
        _columnLimitPending = true;

        // 布局进行中不直接测量，等这一轮排完再算
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _columnLimitPending = false;
            UpdateMainColumnMinWidth();
        }));
    }

    /// <summary>
    /// 主区最小宽度：侧栏优先。先让侧栏拿到想要的宽度，剩下的给主区；
    /// 主区放不下完整头部时，头部靠副标题省略号让位，最多压到 <see cref="HeaderWidthPolicy.HeaderTrimFloor"/>。
    /// 头部宽度会随标题、副标题、按钮显隐变化，所以每次头部尺寸变化都重算。
    /// </summary>
    private void UpdateMainColumnMinWidth()
    {
        if (MainColumn is null || HeaderBar is null || HeaderLeft is null || HeaderRight is null) return;

        var rootWidth = RootGrid?.ActualWidth ?? ActualWidth;

        // 首帧还没量出真实宽度（0 或极小）时就别算：这时可用宽度是负数，
        // 一算就会把主区下限顶到上限、侧栏被永久按到 180，窗口再宽也回不来
        if (!HeaderWidthPolicy.IsMeasured(rootWidth, MinWidth)) return;

        var margin = HeaderBar.Margin;
        var headerNeed = Math.Ceiling(
            MeasureContentWidth(HeaderLeft) + HeaderGap + MeasureContentWidth(HeaderRight)
            + margin.Left + margin.Right);

        var available = rootWidth - SidebarSplitterWidth - DetailWidth;
        var target = HeaderWidthPolicy.MainMinWidth(available, headerNeed, _sidebarDesiredWidth);

        if (Math.Abs(MainColumn.MinWidth - target) >= 0.5) MainColumn.MinWidth = target;

        ClampSidebarWidth();
    }

    /// <summary>右侧详情面板占的宽度（没显示时为 0）。</summary>
    private double DetailWidth
        => DetailPanel is { Visibility: Visibility.Visible } ? DetailPanel.Width : 0;

    private void SidebarSplitter_DragStarted(object sender, DragStartedEventArgs e)
        => _sidebarDesiredWidth = SidebarColumn.ActualWidth;

    private void SidebarSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var min = SidebarColumn.MinWidth;
        var max = GetSidebarMaxWidth();

        // 拖动改的是「想要的宽度」，窗口再窄也不会把这个值抹掉
        _sidebarDesiredWidth = HeaderWidthPolicy.Clamp(_sidebarDesiredWidth + e.HorizontalChange, min, max);

        if (Math.Abs(SidebarColumn.Width.Value - _sidebarDesiredWidth) >= 0.01)
            SidebarColumn.Width = new GridLength(_sidebarDesiredWidth);

        // 侧栏变宽后主区要相应收窄，头部跟着走省略号
        ScheduleColumnLimitUpdate();
    }

    /// <summary>
    /// 侧栏能拖到的最大宽度：主区留够它压缩后的下限；
    /// 右侧详情面板可见时还要再让出它的宽度。
    /// </summary>
    private double GetSidebarMaxWidth()
    {
        if (RootGrid is null) return SidebarColumn.MinWidth;

        var available = RootGrid.ActualWidth - SidebarSplitterWidth - DetailWidth;
        return HeaderWidthPolicy.SidebarMaxWidth(available, SidebarColumn.MinWidth);
    }

    /// <summary>
    /// 窗口变窄、详情打开时只压缩显示宽度；空间回来后恢复到用户想要的宽度。
    /// </summary>
    private void ClampSidebarWidth()
    {
        if (SidebarColumn is null) return;

        var rootWidth = RootGrid?.ActualWidth ?? ActualWidth;
        if (!HeaderWidthPolicy.IsMeasured(rootWidth, MinWidth)) return;

        var width = HeaderWidthPolicy.Clamp(_sidebarDesiredWidth, SidebarColumn.MinWidth, GetSidebarMaxWidth());
        if (Math.Abs(SidebarColumn.Width.Value - width) < 0.5) return;

        SidebarColumn.Width = new GridLength(width);
    }

    /// <summary>按内容真实宽度测量：给无限约束，StackPanel 才不会被单元格宽度压缩后再读。</summary>
    private static double MeasureContentWidth(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = element.DesiredSize.Width;
        element.InvalidateMeasure();
        return width;
    }

    /// <summary>
    /// 把主界面叫到前台：全局快捷键、托盘双击、重复启动都走这一处。
    /// 不能只用 Activate()——从托盘或第二次启动叫回来时进程没有前台权限，窗口只会闪一下。
    /// </summary>
    public void RestoreToFront() => WindowFront.BringToFront(this);

    /// <summary>最小化直接收进托盘：任务栏不留图标，只能从托盘或快捷键叫回来。</summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) Hide();
    }

    /// <summary>
    /// 按下快捷键：本来就在前台就直接收起，不等松手；
    /// 收在托盘里则先叫到前台，松手时再按长短按决定是留是收。
    /// </summary>
    private void OnHotkeyPressed()
    {
        // 收在托盘里时 IsActive 可能还留着 true，必须连可见性一起判断
        _hotkeyWindowWasActive = IsVisible && IsActive && WindowState == WindowState.Normal;

        if (_hotkeyWindowWasActive)
        {
            // 等松手才收的话，按住 Alt 的那几百毫秒窗口一动不动，手感就是「按了没反应」
            Hide();
            return;
        }

        RestoreToFront();
    }

    /// <summary>松开快捷键：长按是「按住看一眼」，松手收回；窗口本来就是打开的不再重复收起。</summary>
    private void OnHotkeyReleased(bool longHold)
    {
        if (!_hotkeyWindowWasActive && longHold) Hide();
    }

    /// <summary>启动时读回上次设的快捷键；关掉过就保持关闭，没设过或存坏了用默认的 Alt+D。</summary>
    private void ApplySavedHotkey()
    {
        var saved = _store.GetMeta("hotkey");

        // 在设置里点过「关闭」：这次启动也不注册，界面照实显示「已关闭」
        if (HotkeyService.IsOff(saved))
        {
            _hotkey?.Disable();
            _hotkeyText = HotkeyService.Disabled;
            _vm.HotkeyText = _hotkeyText;
            _vm.IsHotkeyOff = true;
            return;
        }

        if (!HotkeyService.TryParse(saved, out var modifiers, out var key))
            HotkeyService.TryParse(HotkeyService.Default, out modifiers, out key);

        _hotkeyText = HotkeyService.Format(modifiers, key);
        _hotkeyBeforeOff = _hotkeyText;
        _vm.HotkeyText = _hotkeyText;
        _vm.IsHotkeyOff = false;
        _hotkey?.Apply(modifiers, key);
    }

    /// <summary>设置窗口里录到组合键：注册成功才算数，返回 false 让那边提示被占用。</summary>
    private bool TryApplyHotkey(ModifierKeys modifiers, Key key)
    {
        if (_hotkey is null || !_hotkey.Apply(modifiers, key)) return false;

        _hotkeyText = HotkeyService.Format(modifiers, key);
        _hotkeyBeforeOff = _hotkeyText;
        _vm.HotkeyText = _hotkeyText;
        _vm.IsHotkeyOff = false;
        _store.SetMeta("hotkey", _hotkeyText);
        return true;
    }

    /// <summary>
    /// 设置里的「关闭 / 开启」：关闭时注销热键并把「off」写进 meta，
    /// 下次启动也保持关闭；开启时按关闭前的组合重新注册，失败就维持关闭。
    /// </summary>
    private bool SetHotkeyEnabled(bool enabled)
    {
        if (!enabled)
        {
            // 只有本来是开着的才记下组合，避免连点两次把「已关闭」记进去
            if (!_vm.IsHotkeyOff) _hotkeyBeforeOff = _hotkeyText;

            _hotkey?.Disable();
            _hotkeyText = HotkeyService.Disabled;
            _vm.HotkeyText = _hotkeyText;
            _vm.IsHotkeyOff = true;
            _store.SetMeta("hotkey", HotkeyService.OffMarker);
            return true;
        }

        if (_hotkey is null) return false;
        if (!HotkeyService.TryParse(_hotkeyBeforeOff, out var modifiers, out var key)) return false;
        if (!_hotkey.Apply(modifiers, key)) return false;

        _hotkeyText = HotkeyService.Format(modifiers, key);
        _vm.HotkeyText = _hotkeyText;
        _vm.IsHotkeyOff = false;
        _store.SetMeta("hotkey", _hotkeyText);
        return true;
    }

    /// <summary>设置里改了服务器地址：写回 .env，并更新手机端该填的地址。</summary>
    private void SaveServerAddress(string baseUrl)
    {
        EnvFile.Set("DIDA_LAN_ADDR", baseUrl);
        _sync?.UpdateLanUrl(baseUrl);
        _vm.SyncAddress = baseUrl.TrimEnd('/') + "/sync";
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettings();

    /// <summary>
    /// 点日视图课程带上的课：跳到课表页，把那一周那一天和这门课的详情一起带过去。
    /// </summary>
    private void DayCourse_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DayCourseBlockViewModel block }) return;

        _vm.Schedule.FocusCourse(block.Course);
        _vm.Mode = ViewMode.Schedule;
        e.Handled = true;
    }

    /// <summary>同步时发现没设账号：直接弹设置，用户不用自己去找入口。</summary>
    private void OnAccountRequired() => Dispatcher.BeginInvoke(ShowSettings);

    private void ShowSettings()
    {
        if (_settings is { IsVisible: true })
        {
            _settings.Activate();
            return;
        }

        _settings = new SettingsWindow(_vm, TryApplyHotkey, SetHotkeyEnabled, SaveServerAddress) { Owner = this };
        _settings.Closed += (_, _) => _settings = null;
        _settings.ShowDialog();
    }

    /// <summary>时间框失焦：补全成 HH:mm，并保证截止不早于起始。</summary>
    private void TimeBox_LostFocus(object sender, RoutedEventArgs e) => _vm.NormalizeEditTimes();

    /// <summary>关窗不退出：隐藏到托盘，闹钟与提醒继续工作。</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;

        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// 托盘菜单点「退出」：此时还在窗口消息回调里，
    /// 直接停服务、释放托盘、关窗口容易和消息循环互相等。
    /// 先排到 WPF 队列尾，等菜单收起、回调返回后再真正退出。
    /// </summary>
    private void ExitApp() => Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ShutdownApp));

    private void ShutdownApp()
    {
        if (_exiting) return;
        _exiting = true;

        _reminders?.Stop();
        _alarms.Stop();
        _hotkey?.Dispose();
        _sync?.Dispose();
        _tray?.Dispose();
        Close();
        Application.Current.Shutdown();
    }

    /// <summary>倒计时归零：响铃 + 右下角小窗（不带「稍后」）。</summary>
    private void OnCountdownFinished(object? sender, EventArgs e)
    {
        SoundService.Play();
        new AlarmRingWindow("倒计时", "00:00", "倒计时结束", false).Show();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsDetailVisible):
                if (_vm.IsDetailVisible)
                {
                    AnimateDetailIn();
                    FocusTitle();
                }
                else
                {
                    ResetDetail();
                }

                break;

            // 月 / 年 / 专注首次切过去才建，启动时不用为三个不可见的界面买单
            case nameof(MainViewModel.IsMonthView) when _vm.IsMonthView:
                EnsureView(ref _monthView, MonthHost, () => new MonthView());
                break;

            case nameof(MainViewModel.IsYearView) when _vm.IsYearView:
                EnsureView(ref _yearView, YearHost, () => new YearView());
                break;

            case nameof(MainViewModel.IsFocusView) when _vm.IsFocusView:
                EnsureView(ref _focusView, FocusHost, () => new FocusView());
                break;

            case nameof(MainViewModel.IsScheduleView) when _vm.IsScheduleView:
                EnsureView(ref _scheduleView, ScheduleHost,
                    () => new ScheduleView { DataContext = _vm.Schedule });
                break;
        }
    }

    /// <summary>按需创建视图并塞进占位容器；已经建过就不再重建。</summary>
    private static void EnsureView<T>(ref T? field, ContentControl host, Func<T> create)
        where T : UserControl
    {
        if (field is not null) return;

        field = create();
        host.Content = field;
    }

    private void FocusTitle()
    {
        if (!_vm.IsNewTask) return;

        Dispatcher.BeginInvoke(() =>
        {
            TitleBox.Focus();
            Keyboard.Focus(TitleBox);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void AnimateDetailIn()
    {
        DetailPanel.Opacity = 0;
        DetailSlide.X = 24;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        DetailPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = ease
        });

        DetailSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
    }

    private void ResetDetail()
    {
        DetailPanel.BeginAnimation(OpacityProperty, null);
        DetailSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        DetailPanel.Opacity = 0;
        DetailSlide.X = 24;
    }

    /// <summary>
    /// 删除：删除按钮先散成粒子 → 右侧面板收起与列表行消散几乎同时进行 → 下方日程果冻回弹。
    /// 动画播完再真正写墓碑，避免列表提前重建导致行找不到。
    /// </summary>
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsNewTask)
        {
            _vm.DeleteSelectedCommand.Execute(null);
            return;
        }

        if (_vm.SelectedTask is { } target) await DeleteTaskAsync(target);
    }

    /// <summary>删除一条日程：删除按钮散成粒子 → 面板收起与列表行消散 → 写墓碑 → 下方日程果冻回弹。</summary>
    private async Task DeleteTaskAsync(TaskViewModel target)
    {
        if (_deleting) return;

        _deleting = true;
        DeleteButton.IsEnabled = false;

        try
        {
            // 1) 删除按钮自己先碎掉（键盘删除时面板可能还没画出来，跳过这一段）
            if (DeleteButton.IsVisible)
            {
                var buttonCenter = DeleteButton.TranslatePoint(
                    new Point(DeleteButton.ActualWidth / 2, DeleteButton.ActualHeight / 2), FxLayer);
                Burst(buttonCenter, 14, 34, 300, "danger");
                DeleteButton.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150)));
            }

            // 2) 先记住那一行的位置，等按钮粒子起势后再让它消散
            var row = FindTaskRow(target);
            var rowTop = row is null ? double.MaxValue : row.TranslatePoint(new Point(0, 0), FxLayer).Y;

            await Task.Delay(70);

            if (row is not null)
            {
                var origin = row.TranslatePoint(
                    new Point(row.ActualWidth * 0.35, row.ActualHeight / 2), FxLayer);

                Burst(origin, 20, Math.Max(row.ActualWidth * 0.45, 80), 380, "text-tertiary");
                row.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260)));
                row.IsHitTestVisible = false;
            }

            // 3) 面板收起与主面板消散几乎同时推进
            SlideDetailOut();
            await Task.Delay(260);

            // 4) 真正删除并重建列表，再让下方日程果冻回弹
            _vm.DeleteSelectedCommand.Execute(null);

            if (rowTop < double.MaxValue)
            {
                UpdateLayout();
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Render);
                JellyBelow(rowTop);
            }
        }
        finally
        {
            _deleting = false;
            DeleteButton.IsEnabled = true;
            DeleteButton.BeginAnimation(OpacityProperty, null);
            DeleteButton.Opacity = 1;
        }
    }

    /// <summary>右侧面板向右滑出并淡掉；真正收起由删除命令改 IsDetailVisible 完成。</summary>
    private void SlideDetailOut()
    {
        var duration = TimeSpan.FromMilliseconds(180);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

        DetailPanel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(DetailPanel.Opacity, 0, duration) { EasingFunction = ease });
        DetailSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(DetailSlide.X, 24, duration) { EasingFunction = ease });
    }

    /// <summary>删除点下方（含补位那一行）的日程从下方弹回原位，形成果冻回弹。</summary>
    private void JellyBelow(double top)
    {
        foreach (var row in CollectTaskRows())
        {
            if (row.TranslatePoint(new Point(0, 0), FxLayer).Y < top - 1) continue;

            var lift = Math.Max(row.ActualHeight, 24);
            var shift = new TranslateTransform(0, 0);
            row.RenderTransform = shift;

            shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(lift, 0, TimeSpan.FromMilliseconds(520))
                {
                    EasingFunction = new ElasticEase
                    {
                        Oscillations = 3,
                        Springiness = 6,
                        EasingMode = EasingMode.EaseOut
                    },
                    FillBehavior = FillBehavior.Stop
                });
        }
    }

    /// <summary>按模型 id 在列表里找到那一行；列表尚未渲染时返回空。</summary>
    private Button? FindTaskRow(TaskViewModel target)
    {
        foreach (var row in CollectTaskRows())
        {
            if (row.DataContext is TaskViewModel vm && vm.Model.Id == target.Model.Id) return row;
        }

        return null;
    }

    /// <summary>收集当前可见列表里的所有日程行（分组的嵌套 ItemsControl 一并遍历）。</summary>
    private List<Button> CollectTaskRows()
    {
        var rows = new List<Button>();

        foreach (var list in new FrameworkElement[] { GroupsList, WeekList })
        {
            if (!list.IsVisible) continue;
            CollectRowButtons(list, rows);
        }

        return rows;
    }

    private static void CollectRowButtons(DependencyObject parent, ICollection<Button> rows)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button button && button.DataContext is TaskViewModel) rows.Add(button);
            CollectRowButtons(child, rows);
        }
    }

    /// <summary>从一点散出一簇小圆点，边飞边淡，播完统一清掉。</summary>
    private void Burst(Point center, int count, double spread, int milliseconds, string brushKey)
    {
        var brush = Application.Current?.TryFindResource(brushKey) as Brush ?? Brushes.Gray;
        var dots = new List<Ellipse>();

        for (var i = 0; i < count; i++)
        {
            var size = 3 + Random.Shared.NextDouble() * 5;
            var left = center.X - size / 2;
            var top = center.Y - size / 2;

            var dot = new Ellipse { Width = size, Height = size, Fill = brush };
            Canvas.SetLeft(dot, left);
            Canvas.SetTop(dot, top);
            FxLayer.Children.Add(dot);
            dots.Add(dot);

            var angle = Random.Shared.NextDouble() * Math.PI * 2;
            var radius = spread * (0.35 + Random.Shared.NextDouble() * 0.8);
            var dx = Math.Cos(angle) * radius;
            var dy = Math.Sin(angle) * radius - 6;

            var duration = TimeSpan.FromMilliseconds(milliseconds * (0.65 + Random.Shared.NextDouble() * 0.5));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            dot.BeginAnimation(Canvas.LeftProperty,
                new DoubleAnimation(left, left + dx, duration) { EasingFunction = ease });
            dot.BeginAnimation(Canvas.TopProperty,
                new DoubleAnimation(top, top + dy, duration) { EasingFunction = ease });
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, duration));
        }

        // 一次定时器清场，避免每颗粒子各起一个 timer
        var cleanup = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds * 1.3 + 80)
        };

        cleanup.Tick += (_, _) =>
        {
            cleanup.Stop();
            foreach (var dot in dots) FxLayer.Children.Remove(dot);
        };

        cleanup.Start();
    }

    // ---------------- 键盘操作 ----------------

    /// <summary>
    /// 鼠标在左面板时：方向键 / WASD 换日期（左右一天、上下按周）。
    /// 鼠标在主面板时：上下选日程、Enter 完成、Delete 删除（需 Enter 确认）。
    /// 输入框里打字时一律不抢键。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox or PasswordBox) return;

        // 删除确认条开着时只认 Enter / Esc，避免误触别的快捷键
        if (ConfirmBar.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = ConfirmDeleteAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelDelete();
            }

            return;
        }

        if (DetailPanel.IsMouseOver) return;

        if (ContentHost.IsMouseOver)
        {
            HandleMainKeys(e);
        }
        else if (SidebarPanel.IsMouseOver)
        {
            HandleSidebarKeys(e);
        }
    }

    /// <summary>左面板：A/← 前一天，D/→ 后一天，W/↑ 前一周，S/↓ 后一周。</summary>
    private void HandleSidebarKeys(KeyEventArgs e)
    {
        var days = e.Key switch
        {
            Key.A or Key.Left => -1,
            Key.D or Key.Right => 1,
            Key.W or Key.Up => -7,
            Key.S or Key.Down => 7,
            _ => 0
        };

        if (days == 0) return;

        e.Handled = true;
        _vm.MoveSelectedDate(days);
    }

    /// <summary>主面板：上下移动选中行、Enter 完成、Delete 请求删除。</summary>
    private void HandleMainKeys(KeyEventArgs e)
    {
        if (_vm.Mode is ViewMode.Month or ViewMode.Year or ViewMode.Focus or ViewMode.Schedule) return;

        if (e.Key is Key.Up or Key.W)
        {
            e.Handled = true;
            MoveSelection(-1);
        }
        else if (e.Key is Key.Down or Key.S)
        {
            e.Handled = true;
            MoveSelection(1);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CompleteSelection();
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            RequestDeleteSelection();
        }
    }

    /// <summary>按视觉顺序上下移动选中行，选中即打开右侧详情。</summary>
    private void MoveSelection(int delta)
    {
        var rows = CollectTaskRows();
        if (rows.Count == 0) return;

        var selectedId = _vm.SelectedTask?.Model.Id;
        var index = -1;

        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].DataContext is TaskViewModel vm && vm.Model.Id == selectedId)
            {
                index = i;
                break;
            }
        }

        var next = index < 0
            ? (delta > 0 ? 0 : rows.Count - 1)
            : Math.Clamp(index + delta, 0, rows.Count - 1);

        rows[next].BringIntoView();

        if (rows[next].DataContext is TaskViewModel target)
            _vm.SelectTaskCommand.Execute(target);
    }

    /// <summary>Enter 完成当前选中日程；重复任务会推进到下一次。</summary>
    private void CompleteSelection()
    {
        var selected = _vm.SelectedTask;
        if (selected is null || _vm.IsNewTask) return;

        var id = selected.Model.Id;

        // 用列表里那一行的视图模型去勾，界面才会立刻划线；找不到就直接勾当前选中的
        foreach (var row in CollectTaskRows())
        {
            if (row.DataContext is TaskViewModel vm && vm.Model.Id == id)
            {
                _vm.ToggleDoneCommand.Execute(vm);
                return;
            }
        }

        _vm.ToggleDoneCommand.Execute(selected);
    }

    /// <summary>Delete：先弹确认条，Enter 才真正删。</summary>
    private void RequestDeleteSelection()
    {
        var selected = _vm.SelectedTask;
        if (selected is null || _vm.IsNewTask) return;

        _pendingDelete = selected;
        ConfirmText.Text = $"删除「{selected.Title}」？";
        ConfirmBar.Visibility = Visibility.Visible;
    }

    private async Task ConfirmDeleteAsync()
    {
        var target = _pendingDelete;
        CancelDelete();
        if (target is null) return;

        // 确认期间选中可能被鼠标改过，先切回要删的那条再走删除动画
        _vm.SelectTaskCommand.Execute(target);
        await DeleteTaskAsync(target);
    }

    private void CancelDelete()
    {
        _pendingDelete = null;
        ConfirmBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>日 / 周 / 月 / 专注切换：绿块缩成小圆点，圆点飞到目标按钮后再展开成整块。</summary>
    private async void Segment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _segmentAnimating) return;

        var target = (button.CommandParameter as string) switch
        {
            "week" => ViewMode.Week,
            "month" => ViewMode.Month,
            "year" => ViewMode.Year,
            "focus" => ViewMode.Focus,
            "schedule" => ViewMode.Schedule,
            _ => ViewMode.Day
        };

        if (_vm.Mode == target) return;

        _segmentAnimating = true;
        var old = CurrentSegmentButton();

        try
        {
            var dir = SegmentIndex(target) >= SegmentIndex(_vm.Mode) ? 1 : -1;
            var from = SegmentCenter(old);
            var to = SegmentCenter(button);

            // 旧页面淡出、绿块压成药丸再缩成圆点，两件事同时进行
            var outTask = SlideContentAsync(0, -16 * dir, 1, 0, 70);
            await CollapseToDotAsync(old);

            HideSegmentBlock(old);
            ShowSegmentDotAt(from);

            await outTask;

            // 内容已完全透明，这时切视图并把布局跑完，避免新页面的首次测量卡在动画里
            switch (target)
            {
                case ViewMode.Week:
                    _vm.ShowWeekSegmentCommand.Execute(null);
                    break;
                case ViewMode.Month:
                    _vm.ShowMonthSegmentCommand.Execute(null);
                    break;
                case ViewMode.Year:
                    _vm.ShowYearSegmentCommand.Execute(null);
                    break;
                case ViewMode.Focus:
                    _vm.ShowFocusSegmentCommand.Execute(null);
                    break;
                case ViewMode.Schedule:
                    _vm.ShowScheduleSegmentCommand.Execute(null);
                    break;
                default:
                    _vm.ShowDaySegmentCommand.Execute(null);
                    break;
            }

            UpdateLayout();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);

            // 圆点起飞、新页面滑入、目标绿块展开，三段重叠推进一直到落地
            FadeInSegmentText(old);
            var flyTask = FlySegmentDotAsync(from, to, 60);
            await Task.WhenAll(
                flyTask,
                SlideContentAsync(16 * dir, 0, 0, 1, 80),
                ExpandFromDotAsync(button));
        }
        finally
        {
            SegmentDot.Visibility = Visibility.Collapsed;
            ShowSegmentText(old);

            // 无论成功与否都把内容层恢复到正常位置，避免停成透明或偏移
            ContentSlide.BeginAnimation(TranslateTransform.XProperty, null);
            ContentSlide.X = 0;
            ContentHost.BeginAnimation(OpacityProperty, null);
            ContentHost.Opacity = 1;
            _segmentAnimating = false;
        }
    }

    /// <summary>圆点在分段条里的中心坐标。</summary>
    private Point SegmentCenter(Button? button)
    {
        if (button is null || button.ActualWidth <= 0)
            return new Point(SegmentDotLayer.ActualWidth / 2, SegmentDotLayer.ActualHeight / 2);

        return button.TranslatePoint(
            new Point(button.ActualWidth / 2, button.ActualHeight / 2), SegmentDotLayer);
    }

    /// <summary>圆点在旧按钮中心就位：先静止显示，等视图切换完再起飞。</summary>
    private void ShowSegmentDotAt(Point center)
    {
        Canvas.SetLeft(SegmentDot, center.X - SegmentDotSize / 2);
        Canvas.SetTop(SegmentDot, center.Y - SegmentDotSize / 2);
        SegmentDotScale.ScaleX = 1;
        SegmentDotScale.ScaleY = 1;
        SegmentDot.Visibility = Visibility.Visible;
    }

    /// <summary>绿色小圆点从旧按钮滑到目标按钮，到位后由目标按钮的绿块接管。</summary>
    private async Task FlySegmentDotAsync(Point from, Point to, int milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var startX = from.X - SegmentDotSize / 2;
        var startY = from.Y - SegmentDotSize / 2;
        var endX = to.X - SegmentDotSize / 2;
        var endY = to.Y - SegmentDotSize / 2;

        Canvas.SetLeft(SegmentDot, startX);
        Canvas.SetTop(SegmentDot, startY);
        SegmentDot.Visibility = Visibility.Visible;

        // 起飞瞬间横向拉长、纵向压扁，落点再回成正圆，避免像一张贴纸在平移
        SegmentDotScale.ScaleX = 1.35;
        SegmentDotScale.ScaleY = 0.7;
        var settle = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        SegmentDotScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1.35, 1, duration) { EasingFunction = settle });
        SegmentDotScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.7, 1, duration) { EasingFunction = settle });

        SegmentDot.BeginAnimation(Canvas.LeftProperty,
            new DoubleAnimation(startX, endX, duration) { EasingFunction = ease });
        SegmentDot.BeginAnimation(Canvas.TopProperty,
            new DoubleAnimation(startY, endY, duration) { EasingFunction = ease });

        await Task.Delay(duration);

        SegmentDot.BeginAnimation(Canvas.LeftProperty, null);
        SegmentDot.BeginAnimation(Canvas.TopProperty, null);
        SegmentDotScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SegmentDotScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SegmentDotScale.ScaleX = 1;
        SegmentDotScale.ScaleY = 1;
        Canvas.SetLeft(SegmentDot, endX);
        Canvas.SetTop(SegmentDot, endY);
        SegmentDot.Visibility = Visibility.Collapsed;
    }

    /// <summary>分段顺序，用于决定翻页方向。</summary>
    private static int SegmentIndex(ViewMode mode) => mode switch
    {
        // 课表按钮排在「日」左边，索引取 -1 才能让翻页方向正确
        ViewMode.Schedule => -1,
        ViewMode.Week => 1,
        ViewMode.Month => 2,
        ViewMode.Year => 3,
        ViewMode.Focus => 4,
        _ => 0
    };

    /// <summary>内容区水平翻页：位移加淡入淡出。</summary>
    private async Task SlideContentAsync(double fromX, double toX, double fromOpacity, double toOpacity, int milliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var ease = new SineEase
        {
            EasingMode = toOpacity >= fromOpacity ? EasingMode.EaseOut : EasingMode.EaseIn
        };

        ContentSlide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(fromX, toX, duration) { EasingFunction = ease });
        ContentHost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = ease });

        await Task.Delay(duration);

        ContentSlide.BeginAnimation(TranslateTransform.XProperty, null);
        ContentSlide.X = toX;
        ContentHost.BeginAnimation(OpacityProperty, null);
        ContentHost.Opacity = toOpacity;
    }

    private Button CurrentSegmentButton() => _vm.Mode switch
    {
        ViewMode.Schedule => ScheduleSegmentButton,
        ViewMode.Week => WeekSegmentButton,
        ViewMode.Month => MonthSegmentButton,
        ViewMode.Year => YearSegmentButton,
        ViewMode.Focus => FocusSegmentButton,
        _ => DaySegmentButton
    };

    /// <summary>取按钮模板里的绿色块与文字层；模板尚未应用时返回空，让动画直接跳过。</summary>
    private static (Border? Border, ScaleTransform? Block, FrameworkElement? Text) SegmentParts(Button button)
    {
        button.ApplyTemplate();

        Border? border;
        FrameworkElement? text;
        try
        {
            border = button.Template.FindName("bd", button) as Border;
            text = button.Template.FindName("txt", button) as FrameworkElement;
        }
        catch (InvalidOperationException)
        {
            return (null, null, null);
        }

        if (border is null) return (null, null, text);

        // 模板内联的变换会在模板密封时被冻结，冻结对象无法做动画，换成可写实例
        if (border.RenderTransform is not ScaleTransform scale || scale.IsFrozen)
        {
            scale = new ScaleTransform(1, 1);
            border.RenderTransform = scale;
        }

        return (border, scale, text);
    }

    /// <summary>绿块压成药丸、再缩成绿色小圆点，停在圆点大小交给飞行点接管；文字同步淡出。</summary>
    private static async Task CollapseToDotAsync(Button? button)
    {
        if (button is null) return;

        var (border, block, text) = SegmentParts(button);
        if (border is null || block is null) return;

        var (sx, sy) = DotScale(border);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };

        // 先变成胶囊：缩到 8×8 时正好是正圆
        border.CornerRadius = new CornerRadius(border.ActualHeight / 2);

        var duration = TimeSpan.FromMilliseconds(45);
        block.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, sx, duration) { EasingFunction = ease });
        block.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, sy, duration) { EasingFunction = ease });
        text?.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0, duration) { EasingFunction = ease });

        await Task.Delay(duration);

        block.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        block.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        block.ScaleX = sx;
        block.ScaleY = sy;
    }

    /// <summary>把旧按钮的绿块彻底藏起来（文字也保持隐藏），圆点由飞行层继续显示。</summary>
    private static void HideSegmentBlock(Button? button)
    {
        if (button is null) return;

        var (border, block, text) = SegmentParts(button);
        if (border is null || block is null) return;

        block.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        block.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        block.ScaleX = 0;
        block.ScaleY = 0;
        border.ClearValue(Border.CornerRadiusProperty);

        if (text is not null)
        {
            text.BeginAnimation(OpacityProperty, null);
            text.Opacity = 0;
        }
    }

    /// <summary>恢复按钮文字（绿块已消失时显示成普通灰字）。</summary>
    private static void ShowSegmentText(Button? button)
    {
        if (button is null) return;

        var (_, _, text) = SegmentParts(button);
        if (text is null) return;

        text.BeginAnimation(OpacityProperty, null);
        text.Opacity = 1;
    }

    /// <summary>旧按钮文字淡回来，避免文字在一帧内突然冒出来。</summary>
    private static void FadeInSegmentText(Button? button)
    {
        if (button is null) return;

        var (_, _, text) = SegmentParts(button);
        if (text is null) return;

        text.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(80))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
    }

    /// <summary>落到目标按钮的圆点展开成整块，文字同时淡入。</summary>
    private static async Task ExpandFromDotAsync(Button button)
    {
        var (border, block, text) = SegmentParts(button);
        if (border is null || block is null) return;

        var (sx, sy) = DotScale(border);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        block.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        block.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        block.ScaleX = sx;
        block.ScaleY = sy;
        border.CornerRadius = new CornerRadius(border.ActualHeight / 2);

        if (text is not null)
        {
            text.BeginAnimation(OpacityProperty, null);
            text.Opacity = 0;
        }

        var duration = TimeSpan.FromMilliseconds(60);
        block.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(sx, 1, duration) { EasingFunction = ease });
        block.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(sy, 1, duration) { EasingFunction = ease });
        text?.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, duration) { EasingFunction = ease });

        await Task.Delay(duration);

        block.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        block.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        block.ScaleX = 1;
        block.ScaleY = 1;
        border.ClearValue(Border.CornerRadiusProperty);

        if (text is not null)
        {
            text.BeginAnimation(OpacityProperty, null);
            text.Opacity = 1;
        }
    }

    /// <summary>绿块缩到小圆点所需的缩放比例（渲染变换不影响布局尺寸，可直接取实际宽高）。</summary>
    private static (double X, double Y) DotScale(Border block)
    {
        return (SegmentDotSize / Math.Max(block.ActualWidth, 1),
                SegmentDotSize / Math.Max(block.ActualHeight, 1));
    }
}
