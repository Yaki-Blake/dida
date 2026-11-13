using System.Windows;
using DidaLite.Core.Models;

namespace DidaLite.App.Views;

public partial class AlarmRingWindow : Window
{
    public AlarmRingWindow(AlarmItem alarm)
        : this("闹钟", alarm.TimeText, alarm.Label, true)
    {
    }

    /// <summary>闹钟与倒计时共用的小窗：标题、主时间、说明文案、是否提供「稍后」。</summary>
    public AlarmRingWindow(string title, string timeText, string? label, bool allowSnooze)
    {
        InitializeComponent();

        TitleText.Text = title;
        TimeText.Text = timeText;

        if (!string.IsNullOrWhiteSpace(label))
        {
            LabelText.Text = label;
            LabelText.Visibility = Visibility.Visible;
        }

        SnoozeButton.Visibility = allowSnooze ? Visibility.Visible : Visibility.Collapsed;

        Loaded += OnLoaded;
        MouseLeftButtonDown += (_, _) => DragMove();
    }

    /// <summary>被关闭时应执行的后续动作。</summary>
    public event EventHandler<bool>? ClosedAction;

    /// <summary>true 表示用户选择了「稍后 9 分钟」。</summary>
    public bool Snoozed { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 8;
        Top = area.Bottom - ActualHeight - 8;
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => Finish(false);

    private void OnSnooze(object sender, RoutedEventArgs e) => Finish(true);

    private void Finish(bool snooze)
    {
        Snoozed = snooze;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ClosedAction?.Invoke(this, Snoozed);
    }
}
