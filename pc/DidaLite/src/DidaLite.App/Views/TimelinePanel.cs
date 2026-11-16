using System.Windows;
using System.Windows.Controls;
using DidaLite.App.ViewModels;

namespace DidaLite.App.Views;

/// <summary>
/// 把时间轴上的日程块按 Top / Height / Lane 摆到对应位置；
/// 块宽跟着标题、时间、地点走，重叠的块紧挨着往右排，不硬撑满整行。
/// </summary>
public sealed class TimelinePanel : Panel
{
    /// <summary>紧挨着的日程块之间留一点缝，避免粘成一整块。</summary>
    private const double LaneGap = 4;

    /// <summary>再短的标题也留一点宽度，不然一行字会缩成一枚小块。</summary>
    private const double MinBlockWidth = 96;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;

        foreach (UIElement child in InternalChildren)
        {
            if (child is not FrameworkElement { DataContext: TimelineBlockViewModel block })
            {
                child.Measure(new Size(0, 0));
                continue;
            }

            // 按整列宽度量，让 DesiredSize 就是内容真正想要的宽度
            child.Measure(new Size(width, block.Height));
        }

        var bottom = Math.Max(BlocksHeight(), height);
        return new Size(width, bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var blocks = new List<(FrameworkElement Child, TimelineBlockViewModel Block)>();
        foreach (UIElement child in InternalChildren)
        {
            if (child is FrameworkElement fe && fe.DataContext is TimelineBlockViewModel block)
            {
                blocks.Add((fe, block));
            }
            else
            {
                child.Arrange(new Rect(0, 0, 0, 0));
            }
        }

        foreach (var (child, block) in blocks)
        {
            var width = Math.Min(
                Math.Max(MinBlockWidth, child.DesiredSize.Width),
                Math.Max(0, finalSize.Width));

            // 同一段时间里并排的块：各栏宽度取该栏最宽的那块，按顺序紧挨着排
            var x = 0d;
            for (var lane = 0; lane < block.Lane; lane++)
            {
                x += LaneWidth(blocks, block, lane) + LaneGap;
            }

            if (x + width > finalSize.Width) width = Math.Max(0, finalSize.Width - x);
            child.Arrange(new Rect(x, block.Top, width, block.Height));
        }

        return finalSize;
    }

    /// <summary>同一簇里某一栏的实际宽度（该栏所有重叠块里最宽的那块）。</summary>
    private static double LaneWidth(
        List<(FrameworkElement Child, TimelineBlockViewModel Block)> blocks,
        TimelineBlockViewModel self,
        int lane)
    {
        var width = 0d;
        foreach (var (child, other) in blocks)
        {
            if (other.Lane == lane && Overlaps(self, other))
            {
                width = Math.Max(width, Math.Max(MinBlockWidth, child.DesiredSize.Width));
            }
        }

        return width;
    }

    private static bool Overlaps(TimelineBlockViewModel a, TimelineBlockViewModel b) =>
        a.Top < b.Top + b.Height && b.Top < a.Top + a.Height;

    private double BlocksHeight()
    {
        var bottom = 0d;
        foreach (UIElement child in InternalChildren)
        {
            if (child is FrameworkElement { DataContext: TimelineBlockViewModel block })
            {
                bottom = Math.Max(bottom, block.Top + block.Height);
            }
        }

        return bottom;
    }
}
