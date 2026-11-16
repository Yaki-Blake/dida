using System.Windows;
using System.Windows.Controls;
using DidaLite.App.ViewModels;

namespace DidaLite.App.Views;

/// <summary>
/// 日视图左侧的课程带：每节课按开始 / 结束时刻纵向摆放，横向一律铺满整条带子。
/// 课程带不做左右分栏——它是一条朝时间轴渐渐淡去的沙岸，重叠的课直接压在一起即可。
/// </summary>
public sealed class CourseLanePanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;

        var bottom = 0d;
        foreach (UIElement child in InternalChildren)
        {
            if (child is FrameworkElement { DataContext: DayCourseBlockViewModel block })
            {
                child.Measure(new Size(width, Math.Max(0, block.Height)));
                bottom = Math.Max(bottom, block.Top + block.Height);
            }
            else
            {
                child.Measure(new Size(0, 0));
            }
        }

        return new Size(width, Math.Max(bottom, height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            if (child is FrameworkElement { DataContext: DayCourseBlockViewModel block })
            {
                var top = block.Top;
                var height = Math.Max(0, Math.Min(block.Height, finalSize.Height - top));
                child.Arrange(new Rect(0, top, finalSize.Width, height));
            }
            else
            {
                child.Arrange(new Rect(0, 0, 0, 0));
            }
        }

        return finalSize;
    }
}
