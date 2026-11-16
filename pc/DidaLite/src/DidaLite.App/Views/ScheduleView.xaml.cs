using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    /// 刚好放得下且不折行；默认 252 再收一档，展开时课表 7 天不被挤得太窄，
    /// 同时仍留 12px 向左的拖动余量，不会一打开就卡在极限上。
    /// </summary>
    private const double DetailMinWidth = 240;
    private const double DetailMaxWidth = 880;

    /// <summary>用户拖出来的宽度，收起再展开仍沿用；窗口不够宽时只压缩显示，不丢设定值。</summary>
    private double _detailWidth = 252;

    /// <summary>本次拖动中的目标宽度，按增量累加后夹在允许范围内。</summary>
    private double _detailDragWidth;

    public ScheduleView()
    {
        InitializeComponent();

        // 窗口变窄或右侧主界面详情打开时，收回过宽的详情栏，别把 7 天网格挤没
        RootLayout.SizeChanged += (_, _) => ClampDetailWidth();
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

    /// <summary>详情栏右极限：课表主体至少留下 TimetableColumn.MinWidth，再加一条硬上限。</summary>
    private double GetDetailMaxWidth()
    {
        var available = RootLayout.ActualWidth - DetailSplitterWidth - TimetableColumn.MinWidth;
        return Math.Max(DetailMinWidth, Math.Min(DetailMaxWidth, available));
    }

    /// <summary>窗口变窄时只压缩当前显示宽度；重新变宽会按用户拖过的值恢复。</summary>
    private void ClampDetailWidth()
    {
        if (CourseDetailCard.Visibility != Visibility.Visible) return;

        var width = Math.Clamp(_detailWidth, DetailMinWidth, GetDetailMaxWidth());
        if (Math.Abs(CourseDetailCard.Width - width) < 0.5) return;
        CourseDetailCard.Width = width;
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
        MessageBox.Show(result.Message + "\n\n来源：" + result.Source, "导入课表",
            MessageBoxButton.OK, result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
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

        try
        {
            var result = await JwxtSyncService.SyncAsync(progress, _syncCts.Token);
            (DataContext as ScheduleViewModel)?.Refresh();
            MessageBox.Show(result.Message, "一键同步",
                MessageBoxButton.OK, result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            _syncCts.Dispose();
            _syncCts = null;
            button.Content = "一键同步";
            button.ToolTip = "打开本机的「同步助手」页：从助手里进教务课表页，点一下书签即可同步（点击可取消）";
        }
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

    /// <summary>点日视图一行 → 打开详情；行的数据项是 ScheduleDayItem。</summary>
    private void DayRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScheduleDayItem item })
            (DataContext as ScheduleViewModel)?.OpenCourseCommand.Execute(item.Course);
    }

    /// <summary>Esc 收起详情面板；关闭时未点保存的备注也会自动落盘。</summary>
    private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || (DataContext as ScheduleViewModel)?.IsDetailOpen != true) return;
        (DataContext as ScheduleViewModel)?.CloseDetailCommand.Execute(null);
        e.Handled = true;
    }
}
