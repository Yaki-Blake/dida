using System.Windows;
using System.Windows.Media;

namespace DidaLite.App.Behaviors;

/// <summary>
/// 几个行为都要在同一个元素上挂 RenderTransform 时的小工具：各自取自己那一层，
/// 没有就包进 TransformGroup 补一层，谁也不覆盖谁（模板里的变换是冻结的，得先换成可写副本）。
/// </summary>
internal static class TransformKit
{
    /// <summary>取位移层，没有就补一层。</summary>
    internal static TranslateTransform EnsureTranslate(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform direct && !direct.IsFrozen) return direct;

        if (Find<TranslateTransform>(element) is { } existing) return existing;

        var slide = new TranslateTransform();
        GroupOf(element).Children.Add(slide);
        return slide;
    }

    /// <summary>取缩放层，没有就补一层；缩放原点统一放在中心。</summary>
    internal static ScaleTransform EnsureScale(FrameworkElement element)
    {
        if (element.RenderTransform is ScaleTransform direct && !direct.IsFrozen) return direct;

        if (Find<ScaleTransform>(element) is { } existing) return existing;

        element.RenderTransformOrigin = new Point(0.5, 0.5);

        var scale = new ScaleTransform(1, 1);
        GroupOf(element).Children.Add(scale);
        return scale;
    }

    /// <summary>已经挂着的位移层；没有就返回空，用来做「复位」而不新建。</summary>
    internal static TranslateTransform? TranslateOf(FrameworkElement element) => Find<TranslateTransform>(element);

    private static T? Find<T>(FrameworkElement element) where T : Transform
    {
        switch (element.RenderTransform)
        {
            case T direct:
                return direct;
            case TransformGroup group:
                foreach (var child in group.Children)
                    if (child is T found) return found;
                return null;
            default:
                return null;
        }
    }

    /// <summary>可写的 TransformGroup：原来的变换（可能是冻结的）先复制进来，再往上加层。</summary>
    private static TransformGroup GroupOf(FrameworkElement element)
    {
        var current = element.RenderTransform;

        if (current is TransformGroup group && !group.IsFrozen) return group;

        var wrap = new TransformGroup();
        if (current is not null && !current.Value.IsIdentity)
            wrap.Children.Add(current.IsFrozen ? current.CloneCurrentValue() : current);

        element.RenderTransform = wrap;
        return wrap;
    }
}
