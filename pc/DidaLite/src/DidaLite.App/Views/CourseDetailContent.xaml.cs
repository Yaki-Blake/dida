using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

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

        // 输入框一露出来就把焦点交过去：点一下备注卡就能直接打字，
        // 不用再点一次输入框（那一下点击在用户眼里就是「第一次根本没生效」）。
        LongNoteBox.IsVisibleChanged += (_, e) => FocusEditor(LongNoteBox, e.NewValue);
        OnceNoteBox.IsVisibleChanged += (_, e) => FocusEditor(OnceNoteBox, e.NewValue);
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
    /// 编辑框刚显示出来就把键盘焦点交给它，光标落到正文末尾。
    /// <para>
    /// 必须排到 Input 优先级再执行：IsVisibleChanged 触发时输入框刚被设为可见、
    /// 还没参与这一轮布局，此刻 Focus() 会被「不可见元素不可获得焦点」拒掉。
    /// 用 Keyboard.Focus 而不是只调 Focus()，是因为宿主（课表页卡片 / 主界面右侧面板）
    /// 自己也是从点击里拿到的焦点，只设逻辑焦点可能被宿主的键盘焦点抢回去。
    /// </para>
    /// </summary>
    private static void FocusEditor(TextBox box, object? newValue)
    {
        if (newValue is not true) return;

        box.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!box.IsVisible) return;

            box.Focus();
            Keyboard.Focus(box);

            // 继续写而不是覆盖：有内容时光标停在末尾
            box.CaretIndex = box.Text?.Length ?? 0;
            box.ScrollToEnd();
        }));
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