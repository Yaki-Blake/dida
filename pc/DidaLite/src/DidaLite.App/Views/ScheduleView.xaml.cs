using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DidaLite.App.Services;
using DidaLite.App.ViewModels;
using Microsoft.Win32;

namespace DidaLite.App.Views;

/// <summary>课表页：第一次切到「课表」时才创建。</summary>
public partial class ScheduleView : UserControl
{
    private CancellationTokenSource? _syncCts;

    /// <summary>详情栏与课表之间那条拖动条的命中区宽度，和主界面左侧栏一致；视觉线仍是 2px。</summary>
    private const double DetailSplitterWidth = 10;

    /// <summary>
    /// 详情栏能拖到的最窄 / 最宽（逻辑像素）。
    /// 下限 240 = 20×2 内边距 + 68 标签列 + 最长的一行值（「2026年11月11日 周三」约 129），
    /// 刚好放得下且不折行；默认 300 与主界面右侧详情（DetailWidth）一致，内容舒展。
    /// </summary>
    private const double DetailMinWidth = 240;
    private const double DetailMaxWidth = 880;

    /// <summary>默认宽度：与主界面右侧详情一致；用户拖过之后这里记为上次的宽度。</summary>
    private const double DetailDefaultWidth = 300;
    private double _detailWidth = DetailDefaultWidth;

    /// <summary>本次拖动中的目标宽度，按增量累加后夹在允许范围内。</summary>
    private double _detailDragWidth;

    /// <summary>
    /// 一次「空白点击」的按下位置。详情栏开着时，点网格空白处就收起它；
    /// 按下和抬起必须都落在空白处、位移很小才算点击，拖动滚动不能误伤。
    /// </summary>
    private Point _blankClickStart;
    private bool _blankClickArmed;

    /// <summary>
    /// 周六、周日两列合计占的宽度（= 右侧详情栏的宽度），由 XAML 绑到两个
    /// <see cref="WeekColumnsPanel"/> 的 WeekendWidth 上。
    /// 做成依赖属性是为了让面板重建（切周、导入课表）后绑定依然成立。
    /// </summary>
    public static readonly DependencyProperty WeekendColumnWidthProperty =
        DependencyProperty.Register(
            nameof(WeekendColumnWidth), typeof(double), typeof(ScheduleView),
            new FrameworkPropertyMetadata(0d,
                FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double WeekendColumnWidth
    {
        get => (double)GetValue(WeekendColumnWidthProperty);
        set => SetValue(WeekendColumnWidthProperty, value);
    }

    public ScheduleView()
    {
        InitializeComponent();

        // 窗口尺寸变化时重算能给出的最大宽度；ClampDetailWidth 内部会把新的周末列宽下发
        RootLayout.SizeChanged += (_, _) => ClampDetailWidth();

        // 点课表网格的空白处收起右侧详情栏。挂在 WeekScroll 上：它盖住整个课表主体，
        // 网格下方那段没行的空白也算。详情卡片与那条拖动分隔线都是 WeekScroll 的
        // 兄弟覆盖层，点它们走的是另一条路由路径，不会冒泡到这里，
        // 所以不会出现「点自己的面板反而把它关掉」。
        WeekScroll.PreviewMouseLeftButtonDown += OnGridBlankDown;
        WeekScroll.PreviewMouseLeftButtonUp += OnGridBlankUp;
    }

    /// <summary>详情开着时在网格里按下：先确认按的是空白处，再记下位置。</summary>
    private void OnGridBlankDown(object sender, MouseButtonEventArgs e)
    {
        _blankClickArmed = false;
        if ((DataContext as ScheduleViewModel)?.IsDetailOpen != true) return;
        if (e.OriginalSource is not DependencyObject source) return;
        if (IsInteractiveClickTarget(source)) return;
        if (IsDetailChrome(source)) return;

        _blankClickStart = e.GetPosition(this);
        _blankClickArmed = true;
    }

    /// <summary>
    /// 抬起时按下点都在空白处、位移不超过 4px 才算一次点击，才收起。
    /// 这样按住空白处拖动滚动、或按在空白处却拖到课程卡上抬起，都不会误关面板。
    /// 位移判定抽成 <see cref="IsBlankClick"/>，这样它能被单元测试直接覆盖
    /// （合成事件拿不到真实光标坐标，离屏测不了这一段）。
    /// </summary>
    private void OnGridBlankUp(object sender, MouseButtonEventArgs e)
    {
        if (!_blankClickArmed) return;
        _blankClickArmed = false;

        if ((DataContext as ScheduleViewModel)?.IsDetailOpen != true) return;
        if (e.OriginalSource is not DependencyObject source) return;
        if (IsInteractiveClickTarget(source)) return;
        if (IsDetailChrome(source)) return;
        if (!IsBlankClick(_blankClickStart, e.GetPosition(this))) return;

        // 与 Esc 同一条路径：未点保存的备注由 CloseDetail 自己落盘
        (DataContext as ScheduleViewModel)?.CloseDetailCommand.Execute(null);
    }

    /// <summary>按下点到抬起点的位移在容差内才算「点了一下」，超了就是拖动。</summary>
    internal const double BlankClickSlop = 4;

    /// <summary>纯判定：位移不超过 <see cref="BlankClickSlop"/> 像素才算点击。</summary>
    internal static bool IsBlankClick(Point down, Point up)
        => (up - down).Length <= BlankClickSlop;

    /// <summary>
    /// 课程卡片、按钮、输入框、滚动条这些可交互的东西不算「空白」。
    /// 卡片是 Border + 鼠标事件，没有 Button 基类，和主界面一样用 Cursor=Hand 识别。
    /// </summary>
    private static bool IsInteractiveClickTarget(DependencyObject source)
    {
        for (DependencyObject? node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or TextBoxBase or ComboBox or PasswordBox
                or ScrollBar or Slider or ListBoxItem or TreeViewItem or TabItem)
                return true;

            if (node is FrameworkElement fe && fe.Cursor == Cursors.Hand)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 命中的是不是详情栏自己的那层皮（面板本体或左边那条拖动条）。
    /// 现在两者都是 WeekScroll 的兄弟节点，点它们本来就到不了这里的处理器；
    /// 这层判断是为了将来有人把面板挪进滚动区时，仍然不会「点自己的面板把它关掉」。
    /// </summary>
    private bool IsDetailChrome(DependencyObject source)
    {
        for (DependencyObject? node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, CourseDetailCard) || ReferenceEquals(node, DetailSplitter))
                return true;
        }

        return false;
    }

    /// <summary>详情展开瞬间按上次的宽度恢复（没拖过就是默认 300）。</summary>
    private void CourseDetailCard_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (CourseDetailCard.Visibility == Visibility.Visible) ClampDetailWidth();

        // 收起时也要重算：边栏让出来的周末两列要还给 7 天网格
        SyncWeekColumns();
    }

    /// <summary>边栏宽度每一次变化（拖动、夹紧、恢复记忆值）都同步给网格。</summary>
    private void CourseDetailCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) SyncWeekColumns();
    }

    private void DetailSplitter_DragStarted(object sender, DragStartedEventArgs e)
        => _detailDragWidth = CourseDetailCard.ActualWidth;

    /// <summary>详情栏在右边：鼠标左移（HorizontalChange 为负）时变宽。</summary>
    private void DetailSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _detailDragWidth -= e.HorizontalChange;
        _detailWidth = Math.Clamp(_detailDragWidth, DetailMinWidth, GetDetailMaxWidth());
        _detailDragWidth = _detailWidth;

        if (Math.Abs(CourseDetailCard.Width - _detailWidth) < 0.01) return;
        CourseDetailCard.Width = _detailWidth;
    }

    /// <summary>
    /// 详情栏右极限：它占掉的是周末两列，所以上限跟着「网格可用宽 − 工作日保底」走；
    /// 再加一条硬上限，避免超宽屏上把侧边栏拉到荒唐的宽度。
    /// </summary>
    private double GetDetailMaxWidth()
    {
        var gridWidth = GetWeekGridWidth();
        var weekendLimit = WeekColumnsPanel.MaxWeekendWidth(gridWidth);

        // 网格还没量出宽度时不能按 0 算，否则边栏会被压成下限、一展开就觉得「卡住了」
        var available = gridWidth > 0
            ? weekendLimit
            : RootLayout.ActualWidth - DetailSplitterWidth - TimetableColumn.MinWidth;

        return Math.Max(DetailMinWidth, Math.Min(DetailMaxWidth, available));
    }

    /// <summary>7 天网格（不含左侧节次列）当前可用的宽度。</summary>
    private double GetWeekGridWidth()
    {
        // 表头与格子共用同一套列宽，取其中任意一个的实际宽度即可
        if (CellGrid.ActualWidth > 0) return CellGrid.ActualWidth;
        if (HeaderRow.ActualWidth > 0) return HeaderRow.ActualWidth;

        var width = WeekGrid.ActualWidth - PeriodColumn.ActualWidth;
        return Math.Max(0, width);
    }

    /// <summary>按上次的宽度恢复（默认 300）；只在窗口放不下时夹紧，不改记忆值。</summary>
    private void ClampDetailWidth()
    {
        if (CourseDetailCard.Visibility != Visibility.Visible)
        {
            SyncWeekColumns();
            return;
        }

        var width = Math.Clamp(_detailWidth, DetailMinWidth, GetDetailMaxWidth());
        if (Math.Abs(CourseDetailCard.Width - width) >= 0.5) CourseDetailCard.Width = width;

        SyncWeekColumns();
    }

    /// <summary>
    /// 把详情栏的占位下发给表头与课表主体：周末两列合起来正好等于边栏宽度，
    /// 于是被边栏整块占掉，周一到周五均分剩下的宽度。
    /// 收起时置 0，面板退回 7 列等分。
    /// </summary>
    private void SyncWeekColumns()
    {
        var weekendWidth = CourseDetailCard.Visibility == Visibility.Visible
            ? Math.Max(0, CourseDetailCard.ActualWidth)
            : 0;

        if (Math.Abs(WeekendColumnWidth - weekendWidth) < 0.01) return;
        WeekendColumnWidth = weekendWidth;
    }

    /// <summary>选本地 .ics / .json 导入；落盘并刷新由 ScheduleImporter 负责。</summary>
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入课表",
            Filter = "课表文件 (*.ics;*.json;*.js)|*.ics;*.json;*.js|" +
                     "日历文件 (*.ics)|*.ics|JSON 数据 (*.json;*.js)|*.json;*.js|" +
                     "所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true) return;

        var result = ScheduleImporter.ImportFile(dialog.FileName);
        (DataContext as ScheduleViewModel)?.Refresh();

        // 成功同样不弹窗：结果落在按钮上；只有失败才用对话框说明原因
        if (!result.Ok)
        {
            MessageBox.Show(result.Message + "\n\n来源：" + result.Source, "导入课表",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (sender is Button button)
            FlashButton(button, $"已导入 {result.Events} 节",
                result.Message + "\n来源：" + result.Source);
    }

    /// <summary>
    /// 一键同步：每次都用系统默认浏览器打开本机的「同步助手」页（教务课表页由助手里进），
    /// 页面里的书签或油猴脚本把课表 POST 回本机 5080 后自动完成。
    /// 同步过程中再点一次按钮即取消；不接触账号密码，也不读浏览器 Cookie 库。
    /// </summary>
    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (_syncCts is not null)
        {
            _syncCts.Cancel();
            SyncButton.Content = "正在取消…";
            return;
        }

        var button = (Button)sender;
        _syncCts = new CancellationTokenSource();
        var progress = new Progress<string>(message =>
        {
            button.Content = ShortState(message);
            button.ToolTip = message + "\n（点击可取消）";
        });

        SyncResult? result = null;
        try
        {
            result = await JwxtSyncService.SyncAsync(progress, _syncCts.Token);
            (DataContext as ScheduleViewModel)?.Refresh();

            // 成功不再弹系统 MessageBox：结果直接落在按钮上，几秒后自动复原。
            // 失败仍用对话框——错误信息必须让人看见，不能吞掉。
            if (!result.Ok)
            {
                MessageBox.Show(result.Message, "一键同步",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _syncCts.Dispose();
            _syncCts = null;

            if (result is { Ok: true })
            {
                // 先把默认文案放回去，FlashButton 4 秒后还原的就是「一键同步」
                ResetSyncButton(button);
                FlashButton(button, $"已同步 {result.Events} 节",
                    result.Message + "\n（点击可再次同步）");
            }
            else
            {
                ResetSyncButton(button);
            }
        }
    }

    /// <summary>成功后的即时反馈：按钮暂时显示结果，4 秒后还原成调用前的文案。</summary>
    private void FlashButton(Button button, string text, string toolTip)
    {
        var content = button.Content;
        var tip = button.ToolTip;

        button.Content = text;
        button.ToolTip = toolTip;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // 期间又发起了一次同步就不要抢它的状态文案
            if (_syncCts is not null) return;
            button.Content = content;
            button.ToolTip = tip;
        };
        timer.Start();
    }

    private static void ResetSyncButton(Button button)
    {
        button.Content = "一键同步";
        button.ToolTip = "从教务系统同步课表";
    }

    private static string ShortState(string message)
    {
        if (message.StartsWith("正在启动", StringComparison.Ordinal) ||
            message.StartsWith("正在打开", StringComparison.Ordinal) ||
            message.StartsWith("正在用默认浏览器", StringComparison.Ordinal)) return "打开中…";
        if (message.StartsWith("已取到", StringComparison.Ordinal)) return "解析中…";
        if (message.StartsWith("同步成功", StringComparison.Ordinal)) return "同步完成";
        if (message.StartsWith("已打开", StringComparison.Ordinal) ||
            message.StartsWith("已用默认浏览器", StringComparison.Ordinal)) return "等待同步…";
        return "等待登录…";
    }

    /// <summary>清空，方便反复体验「从零导入」的全流程。</summary>
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确定清空课表数据吗？\n课程备注会保留；原始文件已备份在 _backup-课表数据-20261115。",
                "清空课表", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var result = ScheduleImporter.Clear();
        (DataContext as ScheduleViewModel)?.Refresh();
        MessageBox.Show(result.Message, "清空课表", MessageBoxButton.OK,
            result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    /// <summary>点周视图课程卡片 → 打开这一节课的详情。</summary>
    private void CourseCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScheduleEvent course })
            (DataContext as ScheduleViewModel)?.OpenCourseCommand.Execute(course);
    }

    /// <summary>Esc 收起详情面板；关闭时未点保存的备注也会自动落盘。</summary>
    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || (DataContext as ScheduleViewModel)?.IsDetailOpen != true) return;
        (DataContext as ScheduleViewModel)?.CloseDetailCommand.Execute(null);
        e.Handled = true;
    }
}
