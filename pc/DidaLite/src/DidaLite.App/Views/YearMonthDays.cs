using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DidaLite.App.ViewModels;

namespace DidaLite.App.Views;

/// <summary>
/// 年视图一个月的 42 格。整块自绘：504 个按钮换成 12 个元素，首次切到年视图不用再逐格实例化模板。
/// </summary>
public sealed class YearMonthDays : FrameworkElement
{
    private const int Columns = 7;
    private const int Rows = 6;

    /// <summary>鼠标停在第几格；-1 表示不在格子上。</summary>
    private int _hover = -1;

    private static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(IReadOnlyList<YearDayViewModel>), typeof(YearMonthDays),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty DayCommandProperty = DependencyProperty.Register(
        nameof(DayCommand), typeof(ICommand), typeof(YearMonthDays));

    /// <summary>格子边长，与 Tokens.xaml 的 YearCalendarCell 对齐。</summary>
    private static double CellSize =>
        Application.Current?.TryFindResource("YearCalendarCell") is double size ? size : 24;

    /// <summary>日期字体，与 Tokens.xaml 的 AppFont 对齐。</summary>
    private static FontFamily AppFont =>
        Application.Current?.TryFindResource("AppFont") as FontFamily ?? new FontFamily("Microsoft YaHei UI");

    /// <summary>日期字号，与 Tokens.xaml 的 FontSize11 对齐。</summary>
    private static double FontSizeValue =>
        Application.Current?.TryFindResource("FontSize11") is double size ? size : 11;

    public IReadOnlyList<YearDayViewModel>? Days
    {
        get => (IReadOnlyList<YearDayViewModel>?)GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    public ICommand? DayCommand
    {
        get => (ICommand?)GetValue(DayCommandProperty);
        set => SetValue(DayCommandProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? CellSize * Columns : availableSize.Width;
        return new Size(width, CellSize * Rows);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var days = Days;
        if (days is null || days.Count == 0 || ActualWidth <= 0) return;

        var cell = CellSize;
        var cellWidth = ActualWidth / Columns;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var normalTypeface = new Typeface(AppFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var semiTypeface = new Typeface(AppFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var boldTypeface = new Typeface(AppFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        // 整块铺一层透明底，空白格也能接收鼠标
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, cell * Rows));

        for (var i = 0; i < days.Count && i < Columns * Rows; i++)
        {
            var day = days[i];
            if (day.Day.Length == 0) continue;

            var centerX = i % Columns * cellWidth + cellWidth / 2;
            var centerY = i / Columns * cell + cell / 2;

            var background = day.IsSelected ? Brush("accent")
                : day.IsToday ? Brush("accent-weak")
                : i == _hover ? Brush("bg-hover")
                : day.HeatLevel switch
                {
                    1 => Brush("heat-green-bg"),
                    2 => Brush("heat-yellow-bg"),
                    3 => Brush("heat-purple-bg"),
                    4 => Brush("heat-red-bg"),
                    _ => null
                };

            if (background is not null)
            {
                dc.DrawRoundedRectangle(background, null,
                    new Rect(centerX - cell / 2, centerY - cell / 2, cell, cell), cell / 2, cell / 2);
            }

            var foreground = day.IsSelected ? Brush("on-accent")
                : day.IsToday ? Brush("accent")
                : Brush("text-primary");

            var weight = day.IsSelected ? boldTypeface : day.IsToday ? semiTypeface : normalTypeface;

            var text = new FormattedText(day.Day, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                weight, FontSizeValue, foreground, dpi);
            dc.DrawText(text, new Point(centerX - text.Width / 2, centerY - text.Height / 2));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var index = IndexAt(e.GetPosition(this));
        if (index == _hover) return;

        _hover = index;
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Arrow;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        if (_hover < 0) return;
        _hover = -1;
        Cursor = Cursors.Arrow;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        var index = IndexAt(e.GetPosition(this));
        if (index < 0 || Days is not { } days || DayCommand is not { } command) return;
        if (!command.CanExecute(days[index])) return;

        command.Execute(days[index]);
        e.Handled = true;
    }

    /// <summary>点击位置的格子下标；非本月或越界返回 -1。</summary>
    private int IndexAt(Point point)
    {
        var days = Days;
        if (days is null || ActualWidth <= 0) return -1;

        var cell = CellSize;
        var column = (int)(point.X / (ActualWidth / Columns));
        var row = (int)(point.Y / cell);
        if (column is < 0 or >= Columns || row is < 0 or >= Rows) return -1;

        var index = row * Columns + column;
        if (index >= days.Count || days[index].Day.Length == 0) return -1;

        return index;
    }

    private static Brush? Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush;
}
