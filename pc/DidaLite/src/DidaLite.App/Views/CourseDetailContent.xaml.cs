using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DidaLite.App.Views;

/// <summary>
/// 课程详情的内容本体（时间 / 节次 / 地点 / 老师 / 周次 / 备注）。
/// 课表页右侧卡片和主界面日视图右侧面板共用这一份，关闭动作由宿主通过 <see cref="CloseCommand"/> 传进来。
/// </summary>
public partial class CourseDetailContent : UserControl
{
    public static readonly DependencyProperty CloseCommandProperty = DependencyProperty.Register(
        nameof(CloseCommand),
        typeof(ICommand),
        typeof(CourseDetailContent),
        new PropertyMetadata(null));

    public CourseDetailContent()
    {
        InitializeComponent();

        // 用 AddHandler(handledEventsToo: true) 而不是 XAML 的 MouseLeftButtonUp="..."：
        // 卡片内部的 TextBlock / ScrollViewer 等可能把鼠标事件标记为已处理，
        // 普通的冒泡订阅就收不到——表现正是「点卡片下半部分（文本/空白交界）没反应」。
        // handledEventsToo 让处理器无视 Handled 标记，卡片任意位置都能进编辑态。
        LongNoteCard.AddHandler(MouseLeftButtonUpEvent,
            new MouseButtonEventHandler(LongNoteCard_Click), handledEventsToo: true);
        OnceNoteCard.AddHandler(MouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnceNoteCard_Click), handledEventsToo: true);
    }

    /// <summary>点「关闭」时执行：课表页收自己的卡片，主界面收右侧面板。</summary>
    public ICommand? CloseCommand
    {
        get => (ICommand?)GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    /// <summary>
    /// 点备注卡任意位置进入编辑态。
    /// <para>
    /// 两点都要满足，否则会出现「点卡片下半部分没反应」：
    /// ① 不能靠 Border 的 InputBindings（MouseBinding）——Border 默认 Focusable=false，
    ///    输入管线不保证把 LeftClick 解析成对 InputBindings 的查询；
    /// ② 订阅时要 handledEventsToo: true——卡片内部的 TextBlock / ScrollViewer / TextBox
    ///    会把鼠标事件标记为已处理，普通冒泡订阅在那些位置收不到。
    /// </para>
    /// </summary>
    private void LongNoteCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideEditingControls(e.OriginalSource as DependencyObject)) return;
        if (DataContext is ViewModels.ScheduleViewModel { IsEditingLongNote: false } vm)
            vm.EditLongNoteCommand.Execute(null);
    }

    private void OnceNoteCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideEditingControls(e.OriginalSource as DependencyObject)) return;
        if (DataContext is ViewModels.ScheduleViewModel { IsEditingOnceNote: false } vm)
            vm.EditOnceNoteCommand.Execute(null);
    }

    /// <summary>
    /// 点在编辑态的输入框或按钮上时不要重新进入编辑（否则点输入框会被立刻重置，光标丢失）。
    /// </summary>
    private static bool IsInsideEditingControls(DependencyObject? source)
    {
        for (var cur = source; cur is not null;)
        {
            if (cur is TextBox or Button) return true;
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }
        return false;
    }
}
