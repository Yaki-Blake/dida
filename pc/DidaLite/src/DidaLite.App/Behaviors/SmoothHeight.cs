using System.Windows;
using System.Windows.Media.Animation;

namespace DidaLite.App.Behaviors;

/// <summary>
/// 把高度的瞬时跳变改成一次缓动过渡：值变化时从当前实际高度动画到新值，
/// 收起 / 展开时行高与条目区高度因此能平滑推进，而不是瞬间跳动。
/// </summary>
public static class SmoothHeight
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value",
        typeof(double),
        typeof(SmoothHeight),
        new PropertyMetadata(double.NaN, OnValueChanged));

    public static double GetValue(DependencyObject element) => (double)element.GetValue(ValueProperty);

    public static void SetValue(DependencyObject element, double value) => element.SetValue(ValueProperty, value);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if (e.NewValue is not double to || double.IsNaN(to)) return;

        // 首次布局前不播动画，直接给值，避免整月格子一起从 0 长出来
        if (!element.IsLoaded)
        {
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.Height = to;
            return;
        }

        var from = element.ActualHeight > 0 ? element.ActualHeight : to;
        if (Math.Abs(from - to) < 0.5)
        {
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.Height = to;
            return;
        }

        // 先落 base 值，动画结束后（FillBehavior.Stop）正好停在新高度上，不会回弹
        element.BeginAnimation(FrameworkElement.HeightProperty, null);
        element.Height = to;

        element.BeginAnimation(
            FrameworkElement.HeightProperty,
            new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
    }
}
