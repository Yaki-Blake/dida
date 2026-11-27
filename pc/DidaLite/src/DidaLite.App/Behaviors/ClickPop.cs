using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DidaLite.App.Behaviors;

/// <summary>
/// 点一下「弹」一下：先轻轻涨到 1.045，再带回弹落回原尺寸。
/// 只做视觉，不拦点击；缩放不动布局，所以卡片、格子、日程块都能放心用。
/// </summary>
public static class ClickPop
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(ClickPop),
        new PropertyMetadata(false, OnEnabledChanged));

    /// <summary>最大放大倍数：够看出来，又不至于压到旁边的日程。</summary>
    private const double Peak = 1.045;

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        // 用 Preview：Button 自己会把 MouseLeftButtonUp 标记成已处理，普通事件收不到
        if ((bool)e.NewValue) element.PreviewMouseLeftButtonUp += OnUp;
        else element.PreviewMouseLeftButtonUp -= OnUp;
    }

    private static void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || !element.IsMouseOver) return;
        if (e.OriginalSource is not DependencyObject source) return;

        // 里层自己也开了 ClickPop 时让里层弹，外层不跟着一起涨（月视图格子里的日程条就是这样）
        for (var node = source; node is not null && !ReferenceEquals(node, element);
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement inner && GetEnabled(inner)) return;
        }

        Play(element);
    }

    private static void Play(FrameworkElement element)
    {
        var scale = TransformKit.EnsureScale(element);

        var rise = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var settle = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = 1;
        scale.ScaleY = 1;

        foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            var anim = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(Peak,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70))) { EasingFunction = rise });
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(1,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300))) { EasingFunction = settle });
            scale.BeginAnimation(property, anim);
        }
    }
}
