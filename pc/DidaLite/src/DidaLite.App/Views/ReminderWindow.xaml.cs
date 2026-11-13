using System.Windows;
using DidaLite.App.Services;
using DidaLite.Core.Models;

namespace DidaLite.App.Views;

public enum ReminderAction
{
    Complete,
    SnoozeTenMinutes,
    SnoozeOneDay,
    Dismiss
}

public partial class ReminderWindow : Window
{
    private readonly TaskItem _task;

    public ReminderWindow(TaskItem task)
    {
        InitializeComponent();
        _task = task;

        TitleText.Text = task.Title;
        DueText.Text = task.EffectiveRemindAt is long ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("M月d日 HH:mm")
            : "未设置时间";

        if (!string.IsNullOrWhiteSpace(task.Note))
        {
            NoteText.Text = task.Note;
            NoteText.Visibility = Visibility.Visible;
        }

        Loaded += OnLoaded;
        Closed += (_, _) => ClosedAction?.Invoke(this, Action ?? ReminderAction.Dismiss);
    }

    public event EventHandler<ReminderAction>? ClosedAction;

    public ReminderAction? Action { get; private set; }

    public TaskItem Task => _task;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 8;
        Top = area.Bottom - ActualHeight - 8;

        Activate();
        SoundService.Play();
    }

    private void OnComplete(object sender, RoutedEventArgs e) => Finish(ReminderAction.Complete);

    private void OnSnoozeTen(object sender, RoutedEventArgs e) => Finish(ReminderAction.SnoozeTenMinutes);

    private void OnSnoozeDay(object sender, RoutedEventArgs e) => Finish(ReminderAction.SnoozeOneDay);

    private void OnDismiss(object sender, RoutedEventArgs e) => Finish(ReminderAction.Dismiss);

    private void Finish(ReminderAction action)
    {
        Action = action;
        Close();
    }
}
