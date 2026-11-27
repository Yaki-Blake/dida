using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DidaLite.App.Behaviors;

/// <summary>
/// 元素从隐藏变成可见时淡入并轻轻上浮：头部按钮这类「忽然冒出来」的东西不再硬跳出来。
/// 只做视觉，不改显隐逻辑；隐藏时直接复位，不播退场（退场本来就是瞬间消失）。
/// </summary>
public static class FadeSlide
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(FadeSlide),
        new PropertyMetadata(false, OnEnabledChanged));

    /// <summary>上浮距离：够看出「落下来」，又不至于让按钮窜一截。</summary>
    private const double RiseDistance = 6;

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        if ((bool)e.NewValue)
        {
            element.IsVisibleChanged += OnIsVisibleChanged;

            // 附加时本来就可见（例如启动就是今天视图）：照样走一次入场，避免只有后续出现才有动效
            if (element.IsVisible) Play(element);
        }
        else
        {
            element.IsVisibleChanged -= OnIsVisibleChanged;
        }
    }

    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;

        if ((bool)e.NewValue) Play(element);
        else Reset(element);
    }

    private static void Play(FrameworkElement element)
    {
        var slide = TransformKit.EnsureTranslate(element);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // 先把基值落到终点，动画用 FillBehavior.Stop，播完正好停在基值上，不回弹
        element.BeginAnimation(UIElement.OpacityProperty, null);
        slide.BeginAnimation(TranslateTransform.YProperty, null);
        element.Opacity = 1;
        slide.Y = 0;

        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            });
        slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(RiseDistance, 0, TimeSpan.FromMilliseconds(230))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop
            });
    }

    private static void Reset(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;

        if (TransformKit.TranslateOf(element) is { } slide)
        {
            slide.BeginAnimation(TranslateTransform.YProperty, null);
            slide.Y = 0;
        }
    }
}
