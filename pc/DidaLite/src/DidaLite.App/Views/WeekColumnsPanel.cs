using System.Windows;
using System.Windows.Controls;

namespace DidaLite.App.Views;

/// <summary>
/// 课表页 7 天网格的布局面板。列宽不再等分，而是按「右侧边栏占了多少」来分配。
/// <para>
/// 规则：周六、周日两列的宽度合计正好等于右侧课程详情栏的宽度，
/// 于是边栏的左边缘刚好落在周六列的左边线上——周末那两块本来就是空的「虚假面积」，
/// 被边栏整块占掉；周一到周五则均分剩下的宽度，边栏越宽它们越窄。
/// </para>
/// <para>
/// 边栏收起（<see cref="WeekendWidth"/> 为 0）时退化成 7 列等分，与以前完全一致。
/// 表头（Rows=1）和课表主体（Rows=6）用同一个面板、同一份列宽规则，上下竖线才不会错位。
/// </para>
/// </summary>
public sealed class WeekColumnsPanel : Panel
{
    /// <summary>一周的列数：周一到周日。</summary>
    public const int DefaultColumns = 7;

    /// <summary>末尾被边栏占据的列数：周六、周日。</summary>
    public const int WeekendColumnCount = 2;

    /// <summary>
    /// 工作日每列至少留的宽度，取 96 与左侧节次列同宽。
    /// 视口据此限制边栏能拖多宽（见 <see cref="MaxWeekendWidth"/>），
    /// 面板本身则严格照下发的宽度执行，保证「边栏正好盖住周末、绝不压到周五」
    /// 这条不会两头各算各的。
    /// </summary>
    public const double MinWeekdayColumnWidth = 96;

    /// <summary>行数：表头 1 行、课表主体 6 个节次段。</summary>
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(int), typeof(WeekColumnsPanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>列数：周一到周日共 7 列。</summary>
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(WeekColumnsPanel),
        new FrameworkPropertyMetadata(DefaultColumns, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>
    /// 末尾两列（周六 / 周日）合计占的宽度，直接取右侧课程详情栏的宽度。
    /// 0 表示边栏收起，7 列等分。
    /// </summary>
    public static readonly DependencyProperty WeekendWidthProperty = DependencyProperty.Register(
        nameof(WeekendWidth), typeof(double), typeof(WeekColumnsPanel),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public int Rows
    {
        get => (int)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public double WeekendWidth
    {
        get => (double)GetValue(WeekendWidthProperty);
        set => SetValue(WeekendWidthProperty, value);
    }

    /// <summary>
    /// 给定 7 天网格的可用宽度，算出周末两列最多能被边栏占掉多少
    /// （留下 5 个工作日列的最低宽度）。拖动边栏时用它当上限。
    /// </summary>
    public static double MaxWeekendWidth(double availableGridWidth)
    {
        if (double.IsNaN(availableGridWidth) || availableGridWidth <= 0) return 0;

        var weekdayFloor = (DefaultColumns - WeekendColumnCount) * MinWeekdayColumnWidth;
        return Math.Max(0, availableGridWidth - weekdayFloor);
    }

    /// <summary>
    /// 算出每一列的宽度（索引 0 = 周一）。
    /// 边栏收起时 7 列等分；展开时周六周日吃下边栏宽度，周一到周五分剩下的。
    /// </summary>
    private double[] ResolveWidths(double availableWidth)
    {
        var columns = Math.Max(1, Columns);
        var widths = new double[columns];

        if (double.IsNaN(availableWidth) || availableWidth <= 0) return widths;

        // 拿不到真实可用宽（横向无限）时给个够用的估数，别让格子塌成 0 宽
        if (double.IsInfinity(availableWidth))
            availableWidth = columns * MinWeekdayColumnWidth * 2;

        // 边栏宽度优先满足：它必须正好覆盖周末两列，宁可让工作日更窄也不退让，
        // 否则边栏会骑到周五身上。真正防止工作日被压没的是拖动上限。
        var weekendWidth = WeekendWidth > 0
            ? Math.Min(WeekendWidth, availableWidth)
            : 0;

        if (weekendWidth <= 0)
        {
            Array.Fill(widths, availableWidth / columns);
            return widths;
        }

        var weekdayColumns = columns - WeekendColumnCount;
        var weekdayWidth = Math.Max(0, (availableWidth - weekendWidth) / weekdayColumns);
        for (var i = 0; i < weekdayColumns; i++) widths[i] = weekdayWidth;

        var weekendEach = weekendWidth / WeekendColumnCount;
        for (var i = weekdayColumns; i < columns; i++) widths[i] = weekendEach;

        return widths;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Math.Max(1, Columns);
        var rows = Math.Max(1, Rows);
        var widths = ResolveWidths(availableSize.Width);

        // 表头那一行是 Height=Auto，父容器会用无穷高来量它；
        // 这时行高必须从子元素的真实高度取，否则会算成 0、整条表头消失。
        var unboundedHeight = double.IsInfinity(availableSize.Height);
        var rowHeight = unboundedHeight ? double.PositiveInfinity : Math.Max(0, availableSize.Height / rows);

        var index = 0;
        var tallest = 0d;
        foreach (UIElement child in InternalChildren)
        {
            var column = Math.Clamp(index % columns, 0, columns - 1);
            child.Measure(new Size(Math.Max(0, widths[column]), rowHeight));
            tallest = Math.Max(tallest, child.DesiredSize.Height);
            index++;
        }

        // 宽度严格等于各列之和：边栏占掉周末两列后，工作日网格剩下的宽度就在这里定死。
        return new Size(
            widths.Sum(),
            unboundedHeight ? tallest * rows : Math.Max(0, availableSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Math.Max(1, Columns);
        var rows = Math.Max(1, Rows);
        var widths = ResolveWidths(finalSize.Width);
        var rowHeight = finalSize.Height <= 0 ? 0 : finalSize.Height / rows;

        // 列起点：第 0 列贴左，每列紧接上一列，中间不留缝
        var left = new double[columns];
        var offset = 0d;
        for (var c = 0; c < columns; c++)
        {
            left[c] = offset;
            offset += Math.Max(0, widths[c]);
        }

        var index = 0;
        foreach (UIElement child in InternalChildren)
        {
            var column = Math.Clamp(index % columns, 0, columns - 1);
            var row = index / columns;
            child.Arrange(new Rect(
                left[column],
                row * rowHeight,
                Math.Max(0, widths[column]),
                Math.Max(0, rowHeight)));
            index++;
        }

        return finalSize;
    }
}
