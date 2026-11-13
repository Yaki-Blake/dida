namespace DidaLite.App.Services;

/// <summary>深浅主题切换：替换 MergedDictionaries 第一个（主题）字典。</summary>
public static class ThemeService
{
    /// <summary>当前生效的主题。App.xaml 启动时已合并浅色，所以初始就是浅色。</summary>
    private static bool _isDark;

    public static void Apply(bool dark)
    {
        if (dark == _isDark) return;

        var app = System.Windows.Application.Current;
        if (app is null) return;

        var theme = new System.Windows.ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        };

        var dictionaries = app.Resources.MergedDictionaries;
        if (dictionaries.Count > 0) dictionaries[0] = theme;
        else dictionaries.Add(theme);

        _isDark = dark;
    }
}
