using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DidaLite.App.Behaviors;

/// <summary>
/// 给按钮加一层「按下回弹」的轻量反馈：按下时轻微缩小，松开后用弹性曲线弹回。
/// 只做视觉，不改命令；用附加属性按需开启，避免影响所有按钮。
/// </summary>
public static class PressFeedback
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(PressFeedback),
        new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) =>
        element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Button button) return;

        if ((bool)e.NewValue)
        {
            button.PreviewMouseLeftButtonDown += OnDown;
            button.PreviewMouseLeftButtonUp += OnRelease;
            button.MouseLeave += OnRelease;
            button.Unloaded += OnUnloaded;
            button.RenderTransformOrigin = new Point(0.5, 0.5);
            button.RenderTransform = new ScaleTransform(1, 1);
        }
        else
        {
            button.PreviewMouseLeftButtonDown -= OnDown;
            button.PreviewMouseLeftButtonUp -= OnRelease;
            button.MouseLeave -= OnRelease;
            button.Unloaded -= OnUnloaded;
        }
    }

    private static ScaleTransform? ScaleOf(Button b) => b.RenderTransform as ScaleTransform;

    private static void OnDown(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || !b.IsEnabled) return;
        var scale = ScaleOf(b);
        if (scale is null) return;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(90))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(90))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.HoldEnd });
    }

    private static void OnRelease(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        var scale = ScaleOf(b);
        if (scale is null || scale.ScaleX >= 0.999) return;
        var ease = new ElasticEase
        {
            Oscillations = 1,
            Springiness = 5,
            EasingMode = EasingMode.EaseOut,
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        scale.ScaleX = 1;
        scale.ScaleY = 1;
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        b.PreviewMouseLeftButtonDown -= OnDown;
        b.PreviewMouseLeftButtonUp -= OnRelease;
        b.MouseLeave -= OnRelease;
        b.Unloaded -= OnUnloaded;
    }
}
