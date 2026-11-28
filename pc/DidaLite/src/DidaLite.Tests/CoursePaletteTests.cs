using System.Windows.Media;
using DidaLite.App.Services;

namespace DidaLite.Tests;

/// <summary>
/// 课程配色：色相必须与浏览器端课表页的 hue() 一致，同一门课必须稳定同一个颜色。
/// 期望值取自 Assets/schedule_page.html 的 hue()，用 node 直接算出来对过。
/// </summary>
public class CoursePaletteTests
{
    [Theory]
    [InlineData("大学英语 II", 357)]
    [InlineData("高等数学 I", 296)]
    [InlineData("C语言程序设计", 165)]
    [InlineData("离散数学", 134)]
    [InlineData("羽毛球", 221)]
    [InlineData("风雨球馆", 261)]
    [InlineData("形势与政策 I", 345)]
    [InlineData("岭南文化地理", 31)]
    [InlineData("湾区财经概论", 217)]
    [InlineData("国家安全教育", 273)]
    [InlineData("阅读与写作", 179)]
    public void HueOf_MatchesBrowserHue(string name, int expected)
        => Assert.Equal(expected, CoursePalette.HueOf(name));

    [Fact]
    public void HueOf_EmptyNameFallsBackToFixedHue()
    {
        Assert.Equal(200, CoursePalette.HueOf(null));
        Assert.Equal(200, CoursePalette.HueOf(""));
    }

    [Fact]
    public void SameCourseNameAlwaysGetsSameBrush()
    {
        var a = CoursePalette.FillBrush("高等数学 I");
        var b = CoursePalette.FillBrush("高等数学 I");

        // 缓存命中：同一个 frozen 实例，绑定重算也不会产生新画刷
        Assert.Same(a, b);
        Assert.True(a.IsFrozen);
    }

    [Fact]
    public void DifferentCoursesGetDifferentHues()
    {
        var names = new[] { "高等数学 I", "离散数学", "C语言程序设计", "羽毛球", "大学英语 II" };
        Assert.Equal(names.Length, names.Distinct().Count());

        var hues = names.Select(CoursePalette.HueOf).ToArray();
        Assert.Equal(names.Length, hues.Distinct().Count());
    }

    [Fact]
    public void FillIsTranslucentAndStrokeIsOpaque()
    {
        var fill = (SolidColorBrush)CoursePalette.FillBrush("大学英语 II");
        var stroke = (SolidColorBrush)CoursePalette.StrokeBrush("大学英语 II");

        // 半透明淡底：叠在浅色主题上是淡彩、叠在深色主题上是暗彩，
        // 所以主题切换不需要两套色表（见 CoursePalette 类注释）
        Assert.True(fill.Color.A is > 0 and < 255);
        Assert.Equal((byte)255, stroke.Color.A);

        // 同一色相：描边与底色只差饱和/亮度。
        // 反推回来的值有 8bit 量化误差（357 → 356.92），所以按整数比较。
        Assert.Equal(RgbToHue(fill.Color), RgbToHue(stroke.Color), 0);
    }

    /// <summary>从 RGB 反推色相（度），用来确认两支画刷同色系。</summary>
    private static double RgbToHue(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        if (d <= 0) return 0;

        var h = max == r
            ? 60 * (((g - b) / d) % 6)
            : max == g
                ? 60 * ((b - r) / d + 2)
                : 60 * ((r - g) / d + 4);

        return h < 0 ? h + 360 : h;
    }
}
