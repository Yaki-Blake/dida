using System.Collections.Concurrent;
using System.Windows.Media;

namespace DidaLite.App.Services;

/// <summary>
/// 课程配色：同一门课永远同一个颜色，不同课程颜色不同。
/// <para>
/// 色相取课程名的哈希，算法与浏览器端课表页（Assets/schedule_page.html 的
/// <c>hue()</c>）逐字对齐 —— <c>h = h * 31 + charCodeAt(i)</c>（32 位无符号回绕）后
/// 对 360 取余。这样桌面端和 5080 网页、手机端的同一门课是同一个颜色，
/// 调课、重新导入都不会变（颜色只跟课程名走，不跟教室 / 老师 / 索引走）。
/// </para>
/// <para>
/// 底色用「带透明度的中间色」而不是浅色主题专用的淡色：叠在 bg-surface 上，
/// 浅色主题自动是淡彩、深色主题自动是暗彩，文字仍用 text-primary / secondary
/// 令牌就能读，所以主题切换不需要重算颜色，也没有两套色表要对齐。
/// </para>
/// </summary>
public static class CoursePalette
{
    /// <summary>底色透明度：约 18%，淡到不抢文字，又足够把块与块分开。</summary>
    private const byte FillAlpha = 0x2E;

    /// <summary>描边 / 左侧色条用的实心色参数，与网页端 --c 同一档（48% 饱和、52% 亮）。</summary>
    private const double StrokeSaturation = 0.48;
    private const double StrokeLightness = 0.52;

    /// <summary>底色比描边更亮一点，浅色主题下更接近网页端的 96% 淡底。</summary>
    private const double FillSaturation = 0.62;
    private const double FillLightness = 0.62;

    private static readonly ConcurrentDictionary<string, Brushes> Cache = new(StringComparer.Ordinal);

    /// <summary>课程名 → 色相（0–359）。空名给一个固定值，避免一片课撞在 0 度红上。</summary>
    public static int HueOf(string? name)
    {
        if (string.IsNullOrEmpty(name)) return 200;

        // 与 JS 的 >>> 0 一致：uint 乘法自然回绕到 32 位
        uint h = 0;
        foreach (var c in name) h = h * 31 + c;
        return (int)(h % 360);
    }

    /// <summary>块底色（半透明淡彩）。同一课程名返回同一个 frozen 画刷实例。</summary>
    public static Brush FillBrush(string? name) => For(name).Fill;

    /// <summary>描边 / 色条用的实心色。同一课程名返回同一个 frozen 画刷实例。</summary>
    public static Brush StrokeBrush(string? name) => For(name).Stroke;

    private static Brushes For(string? name)
        => Cache.GetOrAdd(name ?? string.Empty, static key => new Brushes(HueOf(key)));

    /// <summary>缓存的两支画刷。字段而非元组，避免每次取值装箱。</summary>
    private sealed class Brushes
    {
        public Brushes(int hue)
        {
            Fill = Freeze(Hsl(hue, FillSaturation, FillLightness, FillAlpha));
            Stroke = Freeze(Hsl(hue, StrokeSaturation, StrokeLightness, 0xFF));
        }

        public Brush Fill { get; }
        public Brush Stroke { get; }

        private static SolidColorBrush Freeze(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }

    /// <summary>HSL（分量 0–1）转 WPF Color，alpha 0–255。</summary>
    private static Color Hsl(double hue, double saturation, double lightness, byte alpha)
    {
        var h = ((hue % 360) + 360) % 360 / 360.0;

        // 灰：没有饱和度就只有一条亮度
        if (saturation <= 0)
        {
            var g = (byte)Math.Round(lightness * 255);
            return Color.FromArgb(alpha, g, g, g);
        }

        var q = lightness < 0.5
            ? lightness * (1 + saturation)
            : lightness + saturation - lightness * saturation;
        var p = 2 * lightness - q;

        return Color.FromArgb(alpha,
            (byte)Math.Round(HueChannel(p, q, h + 1.0 / 3) * 255),
            (byte)Math.Round(HueChannel(p, q, h) * 255),
            (byte)Math.Round(HueChannel(p, q, h - 1.0 / 3) * 255));
    }

    private static double HueChannel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
