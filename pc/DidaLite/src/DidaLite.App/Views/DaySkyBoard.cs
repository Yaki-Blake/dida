using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DidaLite.App.Views;

/// <summary>
/// 日视图左侧的天空色时间轴：按小时从上往下渐变（深夜 → 黎明 → 白天 → 黄昏 → 夜晚），
/// 叠上整点刻度与「现在」红线；右侧日程区只铺一层很淡的天空底色，不抢日程块。
/// 导入课表后左侧让出一条课程带，时间轴整体右移，仍然是一整条清楚的天色，
/// 不做透明度羽化——刻度和边界都要能一眼对准。
/// </summary>
public sealed class DaySkyBoard : FrameworkElement
{
    /// <summary>一天里的天空颜色（小时 → 颜色），日出、日落各有一段暖色。</summary>
    private static readonly (double Hour, Color Color)[] SkyStops =
    [
        (0.0, Color.FromRgb(0x0F, 0x18, 0x28)),
        (4.0, Color.FromRgb(0x16, 0x22, 0x3A)),
        (5.4, Color.FromRgb(0x3E, 0x41, 0x66)),
        (6.0, Color.FromRgb(0x6E, 0x5B, 0x72)),
        (6.8, Color.FromRgb(0xD8, 0x83, 0x4F)),
        (7.8, Color.FromRgb(0xF3, 0xC8, 0x8F)),
        (9.0, Color.FromRgb(0xA9, 0xD4, 0xEE)),
        (11.0, Color.FromRgb(0x7F, 0xC2, 0xE9)),
        (14.0, Color.FromRgb(0x73, 0xBA, 0xE4)),
        (17.0, Color.FromRgb(0xA6, 0xD2, 0xEA)),
        (18.4, Color.FromRgb(0xF0, 0xA2, 0x63)),
        (19.3, Color.FromRgb(0xDC, 0x6F, 0x4E)),
        (20.2, Color.FromRgb(0x6A, 0x5A, 0x8C)),
        (21.2, Color.FromRgb(0x2B, 0x34, 0x57)),
        (22.6, Color.FromRgb(0x17, 0x20, 0x3A)),
        (24.0, Color.FromRgb(0x0F, 0x18, 0x28))
    ];

    private static readonly FontFamily AppFont = new("Microsoft YaHei UI");

    public static readonly DependencyProperty StartHourProperty = DependencyProperty.Register(
        nameof(StartHour), typeof(int), typeof(DaySkyBoard),
        new FrameworkPropertyMetadata(6, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndHourProperty = DependencyProperty.Register(
        nameof(EndHour), typeof(int), typeof(DaySkyBoard),
        new FrameworkPropertyMetadata(23, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HourHeightProperty = DependencyProperty.Register(
        nameof(HourHeight), typeof(double), typeof(DaySkyBoard),
        new FrameworkPropertyMetadata(56d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RulerWidthProperty = DependencyProperty.Register(
        nameof(RulerWidth), typeof(double), typeof(DaySkyBoard),
        new FrameworkPropertyMetadata(64d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>「现在」在一天里的小时位置，带小数；负数表示不画这条线。</summary>
    public static readonly DependencyProperty NowHourProperty = DependencyProperty.Register(
        nameof(NowHour), typeof(double), typeof(DaySkyBoard),
        new FrameworkPropertyMetadata(-1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsDarkProperty = DependencyProperty.Register(
        nameof(IsDark), typeof(bool), typeof(DaySkyBoard),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>时间轴左边留出的课程带宽度；0 表示没有导入课表，时间轴贴着左边缘。</summary>
    public static readonly DependencyProperty LaneWidthProperty = DependencyProperty.Register(
        nameof(LaneWidth), typeof(double), typeof(DaySkyBoard),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public int StartHour
    {
        get => (int)GetValue(StartHourProperty);
        set => SetValue(StartHourProperty, value);
    }

    public int EndHour
    {
        get => (int)GetValue(EndHourProperty);
        set => SetValue(EndHourProperty, value);
    }

    public double HourHeight
    {
        get => (double)GetValue(HourHeightProperty);
        set => SetValue(HourHeightProperty, value);
    }

    public double RulerWidth
    {
        get => (double)GetValue(RulerWidthProperty);
        set => SetValue(RulerWidthProperty, value);
    }

    public double NowHour
    {
        get => (double)GetValue(NowHourProperty);
        set => SetValue(NowHourProperty, value);
    }

    public bool IsDark
    {
        get => (bool)GetValue(IsDarkProperty);
        set => SetValue(IsDarkProperty, value);
    }

    public double LaneWidth
    {
        get => (double)GetValue(LaneWidthProperty);
        set => SetValue(LaneWidthProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0 || HourHeight <= 0) return;

        var start = StartHour;
        var end = Math.Max(start + 1, EndHour);
        var hours = end - start;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 课程带把时间轴整体推到右边，刻度紧挨着课程带的右沿
        var lane = Math.Clamp(LaneWidth, 0, Math.Max(0, width - 1));
        var rulerLeft = lane;
        var rulerRight = Math.Min(width, lane + RulerWidth);
        var ruler = rulerRight - rulerLeft;

        var sky = BuildSky(start, end);

        // 右侧日程区：只铺一层很淡的天空，保留「天花板是白天、地面是夜晚」的感觉
        var wash = sky.Clone();
        wash.Opacity = IsDark ? 0.10 : 0.16;
        dc.DrawRectangle(wash, null, new Rect(0, 0, width, height));

        // 左侧时间轴：完整的天空色，边界就是边界，不做羽化
        if (ruler > 0) dc.DrawRectangle(sky, null, new Rect(rulerLeft, 0, ruler, height));

        var lineBrush = Application.Current?.TryFindResource("line") as Brush ?? Brushes.Gray;
        var pen = new Pen(lineBrush, 1);

        // 整点横线
        for (var hour = start; hour <= end; hour++)
        {
            var y = Math.Round((hour - start) * HourHeight) + 0.5;
            if (y < 0 || y > height) continue;

            dc.DrawLine(pen, new Point(rulerRight, y), new Point(width, y));
        }

        // 整点刻度：轴内右对齐，字色按天空明暗自动取黑 / 白
        var step = HourHeight >= 40 ? 1 : 2;
        var typeface = new Typeface(AppFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        for (var hour = start; hour <= end; hour += step)
        {
            var y = (hour - start) * HourHeight;
            if (y < 4 || y > height - 4) continue;

            var skyColor = ColorAt(hour);
            var text = hour.ToString("00", CultureInfo.InvariantCulture) + ":00";

            var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, 11, LabelBrush(skyColor), pixelsPerDip);

            var ghost = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, 11, ShadowBrush(skyColor), pixelsPerDip);

            var x = rulerRight - label.Width - 8;
            var top = y - label.Height / 2;

            // 先垫一层反差色描边，橙黄天空上也能看清
            dc.DrawText(ghost, new Point(x + 0.7, top + 0.7));
            dc.DrawText(label, new Point(x, top));
        }

        // 「现在」红线
        var now = NowHour;
        if (now >= start && now <= end)
        {
            var danger = Application.Current?.TryFindResource("danger") as Brush ?? Brushes.OrangeRed;
            var y = Math.Round((now - start) * HourHeight) + 0.5;

            dc.DrawLine(new Pen(danger, 1.5), new Point(rulerRight, y), new Point(width, y));
            dc.DrawEllipse(danger, null, new Point(rulerRight + 1, y), 3.5, 3.5);
        }
    }

    /// <summary>把 [start, end] 这段小时的天空色做成一条竖向渐变。</summary>
    private LinearGradientBrush BuildSky(double start, double end)
    {
        var span = end - start;
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };

        brush.GradientStops.Add(new GradientStop(Tint(ColorAt(start)), 0));

        foreach (var (hour, _) in SkyStops)
        {
            if (hour <= start || hour >= end) continue;

            brush.GradientStops.Add(new GradientStop(Tint(ColorAt(hour)), (hour - start) / span));
        }

        brush.GradientStops.Add(new GradientStop(Tint(ColorAt(end)), 1));
        brush.Freeze();

        return brush;
    }

    /// <summary>按小时在停靠点之间线性插值出天空色。</summary>
    private static Color ColorAt(double hour)
    {
        var clamped = Math.Clamp(hour, SkyStops[0].Hour, SkyStops[^1].Hour);

        for (var i = 1; i < SkyStops.Length; i++)
        {
            var (toHour, toColor) = SkyStops[i];
            if (clamped > toHour) continue;

            var (fromHour, fromColor) = SkyStops[i - 1];
            var t = toHour <= fromHour ? 0 : (clamped - fromHour) / (toHour - fromHour);

            return Color.FromRgb(
                (byte)(fromColor.R + (toColor.R - fromColor.R) * t),
                (byte)(fromColor.G + (toColor.G - fromColor.G) * t),
                (byte)(fromColor.B + (toColor.B - fromColor.B) * t));
        }

        return SkyStops[^1].Color;
    }

    /// <summary>深色主题下把天空压暗，避免整条轴在暗色界面上发白。</summary>
    private Color Tint(Color color) =>
        IsDark ? Blend(color, Color.FromRgb(0x12, 0x15, 0x1B), 0.45) : color;

    private static Color Blend(Color a, Color b, double amount) => Color.FromRgb(
        (byte)(a.R * (1 - amount) + b.R * amount),
        (byte)(a.G * (1 - amount) + b.G * amount),
        (byte)(a.B * (1 - amount) + b.B * amount));

    /// <summary>亮天空上用深色字，暗天空上用浅色字。</summary>
    private static Brush LabelBrush(Color skyColor) =>
        Luminance(skyColor) > 0.52
            ? new SolidColorBrush(Color.FromArgb(0xF0, 0x1B, 0x1D, 0x1F))
            : new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));

    /// <summary>刻度字外围的一层淡描边，和字色相反，保证在任何天色上都压得住。</summary>
    private static Brush ShadowBrush(Color skyColor) =>
        Luminance(skyColor) > 0.52
            ? new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0x70, 0x00, 0x00, 0x00));

    private static double Luminance(Color color) =>
        (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
}
