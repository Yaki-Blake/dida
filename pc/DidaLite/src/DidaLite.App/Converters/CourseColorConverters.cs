using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DidaLite.App.Services;

namespace DidaLite.App.Converters;

/// <summary>
/// 课程色块配色：绑课程名（Binding 到 <c>Name</c>），同一门课永远同一个颜色。
/// 具体取色规则在 <see cref="CoursePalette"/>，与浏览器端课表页同一套算法。
/// </summary>
public sealed class CourseFillConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => CoursePalette.FillBrush(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>课程色块的描边 / 左侧色条：与底色同一个色相，只是更实。</summary>
public sealed class CourseStrokeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => CoursePalette.StrokeBrush(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
